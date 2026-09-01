# tools/run-e2e.ps1 -- the end-to-end walk: master + game server + one account, login -> join -> UDP.
#
# WHAT THIS CLOSES. M2 criterion 14 has been carried since phase 02 and was never verified end
# to end, because verifying it needed three processes running together and nothing stood them
# up. Every part had its own test: the master has unit tests, the transport has a bench, the
# load harness logs bots in. None of them crosses the junction where the TCP half hands an
# address and a signed ticket to the UDP half, and that junction is where "it all works" is
# either true or false.
#
# WHAT IT PROVES, EXACTLY. One account reaches a live match through the shipped path:
#
#   1. the master accepts a TCP connection
#   2. an account registers and logs in
#   3. a room join makes matchmaking allocate a REGISTERED, heartbeating game server, and the
#      master issues a signed 64-byte ticket for it
#   4. that ticket opens a UDP connection to that server, and the server sends a snapshot back
#
# WHAT IT DOES NOT PROVE. It does not drive Unity's client UI. The harness composes the same
# two collaborators MasterSession composes -- IMasterClient for the TCP half, ITransportClient
# for the UDP half -- so the wire path is the shipped one, but the flow machine, the lobby
# shell and the scene load sit above them and are covered by Ironfront.Client.Flow.Tests
# instead. Say "the protocol path is verified end to end", not "the client is".
#
# WHY THE NEGATIVE RUN IS NOT OPTIONAL. A game server left with
# IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1 admits anybody, and the positive run above
# would still print PASS -- it would be proving that a UDP port is open, which local-server-
# smoke.sh already proves more cheaply. So this script runs the walk TWICE: once normally, and
# once with one byte of the ticket flipped, requiring the second to be refused. A gate that has
# not been watched failing is not a gate.
#
# PORTS ARE DELIBERATELY NOT THE PRODUCTION DEFAULTS. A stray real master on 27000 or a real
# game server on 27015 would make this pass without any of the processes it started being
# involved. Same reasoning as run-integration.ps1's port 45510.
#
# Usage:
#   pwsh tools/run-e2e.ps1
#   pwsh tools/run-e2e.ps1 -KeepLogs -TimeoutSec 180
#   pwsh tools/run-e2e.ps1 -SkipNegative      # positive leg only; says so in the verdict

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    # Not 27000. See the header.
    [int] $MasterPort = 45520,

    # Not 27001.
    [int] $MetricsPort = 45521,

    # Not 27015.
    [int] $UdpPort = 45522,

    # The Windows player, launched headless. -batchmode with no IRONFRONT_LANEB_ROLE is what
    # DedicatedServerSceneBootstrap reads as "a headless build launched to host a map", which
    # is the same declaration the Linux dedicated image makes.
    [string] $PlayerPath = "build/windows/Ironfront.exe",

    [string] $Scene = "Dustbowl",

    # The map the game server advertises to the matchmaker AND the map a created room asks for.
    # One value because a mismatch is silently unmatchable: the matchmaker filters servers by
    # the room's map, so a server advertising 1 and a room wanting 2 is NoGameServerAvailable
    # with everything healthy. Dustbowl is 1.
    [ushort] $MapId = 1,

    # Budget for the game server to boot, load the map, and appear healthy at the master.
    [int] $ServerReadySec = 180,

    # Budget handed to the harness for one walk.
    [int] $TimeoutSec = 120,

    # Run only the positive walk. The verdict then says the gate is ungraded, because it is.
    [switch] $SkipNegative,

    [switch] $KeepLogs
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir   = Join-Path $repoRoot "artifacts/e2e"
$masterLog = Join-Path $outDir "master.log"
$masterErr = Join-Path $outDir "master.err.log"
$serverLog = Join-Path $outDir "game-server.log"
$walkLog   = Join-Path $outDir "walk.log"
$negLog    = Join-Path $outDir "walk-negative.log"
$dbPath    = Join-Path $outDir "e2e.db"

$processes = @()

function Stop-Started {
    foreach ($entry in $processes) {
        $p = $entry.Process
        if ($null -ne $p -and -not $p.HasExited) {
            Write-Host "[e2e] stopping $($entry.Label) (pid $($p.Id))"
            try { $p.Kill($true) } catch { }
        }
    }
}

# Reads the master's metrics endpoint. It is a RAW TCP socket that writes one JSON document and
# closes -- not HTTP -- which is why this is a socket read and not Invoke-RestMethod. Same shape
# tools/alert.sh reads with /dev/tcp.
function Read-Metrics {
    param([int] $Port)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect("127.0.0.1", $Port)
        $reader = New-Object System.IO.StreamReader($client.GetStream())
        return $reader.ReadToEnd()
    }
    catch { return $null }
    finally { $client.Dispose() }
}

function Wait-ForTcpPort {
    param([int] $Port, [int] $Seconds, [string] $What)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object System.Net.Sockets.TcpClient
        try { $client.Connect("127.0.0.1", $Port); return $true }
        catch { Start-Sleep -Milliseconds 300 }
        finally { $client.Dispose() }
    }

    Write-Host "[e2e] $What never opened port $Port within ${Seconds}s"
    return $false
}

try {
    # ---- 0. preconditions ---------------------------------------------------------------
    $player = Join-Path $repoRoot $PlayerPath
    if (-not (Test-Path $player)) {
        Write-Host "[e2e] SKIP -- no player build at $PlayerPath."
        Write-Host "       Build one with: pwsh tools/run-lane-b.ps1 -Build"
        Write-Host "       This is a SKIP and not a FAIL on purpose: an absent artifact is not a"
        Write-Host "       broken system, and a red here would train the reader to ignore reds."
        exit 3
    }

    # A master or game server leaked by an earlier run would answer on these ports, and the walk
    # would then pass against processes this script neither started nor configured -- including,
    # possibly, one with ticket validation off. Checked rather than assumed: tools/alert-drill.sh
    # misgraded itself twice for exactly this before it grew the same guard.
    foreach ($busy in @(@{ Port = $MasterPort; What = "a master" },
                        @{ Port = $MetricsPort; What = "a metrics endpoint" })) {
        $probe = New-Object System.Net.Sockets.TcpClient
        try {
            $probe.Connect("127.0.0.1", $busy.Port)
            Write-Host "[e2e] REFUSING TO RUN: $($busy.What) is already listening on 127.0.0.1:$($busy.Port)."
            Write-Host "      This run would grade a process it did not start. Usually a leak from an"
            Write-Host "      earlier run: Get-Process Ironfront.MasterServer,Ironfront | Stop-Process -Force"
            exit 1
        }
        catch { }
        finally { $probe.Dispose() }
    }

    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    # ---- 1. build ------------------------------------------------------------------------
    Write-Host "[e2e] building master server and walk harness ($Configuration)"
    & dotnet build (Join-Path $repoRoot "Ironfront.MasterServer/Ironfront.MasterServer.csproj") `
        -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "the master server did not build" }

    & dotnet build (Join-Path $repoRoot "Ironfront.Tools.E2E/Ironfront.Tools.E2E.csproj") `
        -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "the walk harness did not build" }

    # ---- 2. a secret this run alone knows -------------------------------------------------
    # Generated rather than read from .env so the run cannot accidentally authenticate against
    # a real master, and so a leaked log line is worthless the moment the run ends.
    $secret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))

    # ---- 3. master ------------------------------------------------------------------------
    # A fresh database every run: an account left behind by the last one would make leg 2 pass
    # on history rather than on the master working now.
    $masterEnv = @{
        IRONFRONT_SHARED_SECRET = $secret
        IRONFRONT_MASTER_PORT   = "$MasterPort"
        IRONFRONT_METRICS_PORT  = "$MetricsPort"
        IRONFRONT_METRICS_BIND  = "127.0.0.1"
        IRONFRONT_DB_PATH       = $dbPath
        IRONFRONT_LOG_LEVEL     = "Debug"
    }
    foreach ($k in $masterEnv.Keys) { Set-Item -Path "Env:$k" -Value $masterEnv[$k] }

    Write-Host "[e2e] starting the master (tcp $MasterPort, metrics $MetricsPort, db $dbPath)"
    $master = Start-Process -FilePath "dotnet" -PassThru -NoNewWindow `
        -ArgumentList @("run", "--project",
                        (Join-Path $repoRoot "Ironfront.MasterServer/Ironfront.MasterServer.csproj"),
                        "-c", $Configuration, "--no-build") `
        -RedirectStandardOutput $masterLog -RedirectStandardError $masterErr
    $processes += @{ Label = "master"; Process = $master }

    if (-not (Wait-ForTcpPort -Port $MasterPort -Seconds 60 -What "the master")) {
        throw "the master never opened $MasterPort. See $masterLog and $masterErr."
    }
    Write-Host "[e2e] master is listening"

    # ---- 4. game server -------------------------------------------------------------------
    # IRONFRONT_LANEB_ROLE must be ABSENT: DedicatedServerSceneBootstrap returns early when a
    # harness role is set, and the process would then host nothing and load no map. It is
    # cleared explicitly because this shell may have inherited one from a lane-B run.
    Remove-Item Env:IRONFRONT_LANEB_ROLE -ErrorAction SilentlyContinue
    Remove-Item Env:IRONFRONT_LANEB_LABEL -ErrorAction SilentlyContinue

    $env:IRONFRONT_MASTER_HOST = "127.0.0.1"
    $env:IRONFRONT_GAMESERVER_UDP_PORT = "$UdpPort"
    $env:IRONFRONT_GAMESERVER_PUBLIC_IP = "127.0.0.1"
    $env:IRONFRONT_GAMESERVER_TRANSPORT = "udp"
    $env:IRONFRONT_GAMESERVER_SCENE = $Scene
    # REQUIRED, and the reason this script failed on its first real run: TryRegister refuses a
    # registration whose map list is empty, so a server without this is turned away and then
    # reaped as unauthenticated 30 s later. Unrelated to the scene above -- this advertises to
    # the matchmaker and loads nothing. Dustbowl is MapCatalog.DefaultMapId = 1.
    $env:IRONFRONT_GAMESERVER_MAP_IDS = "$MapId"
    # THE LOAD-BEARING LINE. With this at 1 the negative run below is admitted and the whole
    # gate degrades to "a UDP port is open". Set explicitly rather than relied on as a default.
    $env:IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS = "0"

    Write-Host "[e2e] starting the game server (udp $UdpPort, scene $Scene, signed tickets required)"
    $server = Start-Process -FilePath $player -PassThru `
        -ArgumentList @("-batchmode", "-nographics", "-logFile", $serverLog)
    $processes += @{ Label = "game-server"; Process = $server }

    # Wait for the MASTER's view of it, not for the UDP port. A server that binds its port but
    # never registers is exactly the state that makes a join answer NoGameServerAvailable, and
    # a port check is green for it.
    $deadline = (Get-Date).AddSeconds($ServerReadySec)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        if ($server.HasExited) {
            throw "the game server exited $($server.ExitCode) before registering. See $serverLog."
        }

        $metrics = Read-Metrics -Port $MetricsPort
        if ($metrics -and $metrics -match '"gameServers"\s*:\s*\{[^}]*"healthy"\s*:\s*([1-9][0-9]*)') {
            Write-Host "[e2e] the master reports $($Matches[1]) healthy game server(s)"
            $healthy = $true
            break
        }
        Start-Sleep -Milliseconds 750
    }

    if (-not $healthy) {
        throw ("the game server never became healthy at the master within ${ServerReadySec}s. " +
               "Look for '[net] master link: registered as server' in $serverLog -- " +
               "'staying standalone' there means it never tried or was refused.")
    }

    # ---- 5. the walk ----------------------------------------------------------------------
    Write-Host ""
    Write-Host "[e2e] --- positive walk ---"
    & dotnet run --project (Join-Path $repoRoot "Ironfront.Tools.E2E/Ironfront.Tools.E2E.csproj") `
        -c $Configuration --no-build -- `
        --master-host 127.0.0.1 --master-port $MasterPort --timeout $TimeoutSec --map-id $MapId `
        2>&1 | Tee-Object -FilePath $walkLog
    $positive = $LASTEXITCODE

    $negative = 0
    if (-not $SkipNegative) {
        Write-Host ""
        Write-Host "[e2e] --- negative walk (a corrupted ticket must be refused) ---"
        & dotnet run --project (Join-Path $repoRoot "Ironfront.Tools.E2E/Ironfront.Tools.E2E.csproj") `
            -c $Configuration --no-build -- `
            --master-host 127.0.0.1 --master-port $MasterPort --timeout $TimeoutSec --map-id $MapId `
            --username e2e_forger --negative `
            2>&1 | Tee-Object -FilePath $negLog
        $negative = $LASTEXITCODE
    }

    # ---- 6. verdict -----------------------------------------------------------------------
    Write-Host ""
    if ($positive -ne 0) {
        Write-Host "[e2e] FAIL -- the positive walk exited $positive. See $walkLog."
        exit $positive
    }

    if ($SkipNegative) {
        Write-Host "[e2e] PASS (UNGRADED) -- the walk succeeded, but -SkipNegative means nothing"
        Write-Host "      checked that a bad ticket would have been refused. Do not quote this"
        Write-Host "      run as evidence that ticket validation works."
        exit 0
    }

    if ($negative -ne 0) {
        Write-Host "[e2e] FAIL -- the negative walk exited ${negative}: a corrupted ticket was ADMITTED,"
        Write-Host "      so the positive PASS above proves only that a UDP port is open."
        Write-Host "      See $negLog."
        exit 4
    }

    Write-Host "[e2e] PASS -- login -> join -> UDP walks end to end, and a corrupted ticket does not."
    exit 0
}
finally {
    Stop-Started

    if (-not $KeepLogs -and $LASTEXITCODE -eq 0) {
        Write-Host "[e2e] logs kept anyway at $outDir (they are the evidence, not debris)"
    }
    else {
        Write-Host "[e2e] logs at $outDir"
    }
}
