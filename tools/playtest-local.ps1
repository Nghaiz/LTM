# tools/playtest-local.ps1 -- the whole game, on one machine, played by hand.
#
# WHAT THIS IS FOR. Everything else in tools/ launches processes that grade themselves:
# run-lane-b.ps1 drives scripted clients through recorded programmes, run-e2e.ps1 drives a
# synthetic client through the protocol. Neither has ever put a human in front of the shipped
# menu. This one does, and it is the only thing that can: M3's acceptance clause asks for
# somebody who did not build the flow to run it, and no harness satisfies that sentence.
#
# WHAT IT STARTS. Three kinds of process, in the order they have to come up:
#
#   1. Ironfront.MasterServer   TCP 27000  -- accounts, room browser, match start, tickets
#   2. Ironfront.exe -batchmode UDP 27015  -- the game server; registers itself with the master
#   3. Ironfront.exe  x N       windowed   -- the clients you actually play
#
# WAITING FOR THE RIGHT THING. Step 2 waits for the MASTER'S VIEW of the game server, not for
# the UDP port. A server that binds its port and never registers is exactly the state that makes
# a room join answer NoGameServerAvailable, and a port check is green for it. run-e2e.ps1 learned
# that the expensive way and the wait is copied from it deliberately.
#
# WHY ALT-TAB IS SAFE. ProjectSettings.asset carries runInBackground: 1, so the three windows you
# are not looking at keep simulating and keep sending input. If that setting is ever flipped, the
# unfocused clients freeze and you will read it as a replication defect. It is checked here.
#
# WHY THE LOGIN RATE IS RAISED. Every account you register logs in from 127.0.0.1 and they all
# share one bucket. The shipped default is five per minute, so registering four players in a row
# has the fourth REFUSED -- reported through the menu as a failed login, which reads as a wrong
# password and is not one.
#
# Usage:
#   pwsh tools/build-player.ps1                       # once, after any code change
#   pwsh tools/playtest-local.ps1 -Clients 4
#   pwsh tools/playtest-local.ps1 -Clients 2 -Scene Island
#   pwsh tools/playtest-local.ps1 -Stop               # kill anything a previous run left behind
#
# Ctrl+C, or closing this window, tears the whole stack down.

[CmdletBinding()]
param(
    # Rendered clients to launch. Four fit on a 1080p desktop at the default window size.
    [ValidateRange(1, 8)]
    [int] $Clients = 4,

    [ValidateSet("Dustbowl", "Island")]
    [string] $Scene = "Dustbowl",

    # The shipped defaults, unlike run-e2e.ps1's deliberately-wrong ports. A human types the
    # master's address into the menu, and the field is pre-filled with 127.0.0.1:27000 from
    # GameClientConfig.DefaultMasterPort -- so using anything else here means typing on every
    # client, every session. The port guard below covers what the wrong-port trick covered.
    [int] $MasterPort  = 27000,
    [int] $MetricsPort = 27001,
    [int] $UdpPort     = 27015,

    [string] $PlayerPath = "build/windows/Ironfront.exe",

    # Windowed, small enough that four do not overlap on a 1080p desktop.
    [int] $Width  = 940,
    [int] $Height = 528,

    # Leave the windows wherever Unity puts them (stacked). The 2x2 tiling is best-effort
    # Win32 and this is the escape hatch when it misbehaves on a multi-monitor desktop.
    [switch] $NoTile,

    # Wipe the account database. Off by default: re-registering four players every session is
    # the kind of friction that stops a playtest happening at all.
    [switch] $FreshDb,

    # Kill a leaked master / game server / clients from an earlier run, then exit.
    [switch] $Stop,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [int] $ServerReadySec = 180
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib/local-stack.ps1")

$outDir     = Join-Path $repoRoot "tmp/playtest"
$masterLog  = Join-Path $outDir "master.log"
$masterErr  = Join-Path $outDir "master.err.log"
$serverLog  = Join-Path $outDir "game-server.log"
$dbPath     = Join-Path $outDir "playtest.db"

$processes = @()

function Stop-Started {
    foreach ($entry in $processes) {
        $p = $entry.Process
        if ($null -ne $p -and -not $p.HasExited) {
            Write-Host "[playtest] stopping $($entry.Label) (pid $($p.Id))"
            try { $p.Kill($true) } catch { }
        }
    }
}

# Best-effort 2x2 (or 1xN) tiling, so four windows do not land on top of each other. Win32
# MoveWindow rather than anything Unity offers: the player has no command-line switch that
# positions its window, only one that sizes it. Failure here is cosmetic and never fatal --
# -NoTile skips it entirely.
function Set-TiledWindows {
    param([object[]] $Launched, [int] $Width, [int] $Height)

    try {
        if (-not ("Ironfront.Win32" -as [type])) {
            Add-Type -Namespace Ironfront -Name Win32 -MemberDefinition @"
[DllImport("user32.dll")]
public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
"@
        }
    }
    catch {
        Write-Host "[playtest] window tiling unavailable ($($_.Exception.Message)); arrange them by hand."
        return
    }

    $columns = if ($Launched.Count -le 2) { $Launched.Count } else { 2 }

    for ($k = 0; $k -lt $Launched.Count; $k++) {
        $p = $Launched[$k].Process

        # The window does not exist the instant the process does. Ten seconds is generous for a
        # player that is still loading Splash, and giving up is harmless.
        $waited = 0
        while ($p.MainWindowHandle -eq [IntPtr]::Zero -and $waited -lt 10000 -and -not $p.HasExited) {
            Start-Sleep -Milliseconds 250
            $waited += 250
            $p.Refresh()
        }
        if ($p.MainWindowHandle -eq [IntPtr]::Zero) { continue }

        $col = $k % $columns
        $row = [math]::Floor($k / $columns)
        [void][Ironfront.Win32]::MoveWindow(
            $p.MainWindowHandle, $col * ($Width + 8), $row * ($Height + 40), $Width, $Height, $true)
    }
}

# ------------------------------------------------------------------------------------------
# -Stop: clean up after a run that was killed rather than closed.
# ------------------------------------------------------------------------------------------
if ($Stop) {
    $found = 0
    foreach ($name in @("Ironfront.MasterServer", "Ironfront")) {
        foreach ($p in @(Get-Process $name -ErrorAction SilentlyContinue)) {
            Write-Host "[playtest] killing $name (pid $($p.Id))"
            try { $p.Kill($true); $found++ } catch { }
        }
    }
    # `dotnet run` starts the master as a CHILD, so killing the dotnet host is what actually
    # frees port 27000 when the run was interrupted before Stop-Started could fire.
    foreach ($p in @(Get-Process dotnet -ErrorAction SilentlyContinue)) {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)" -ErrorAction SilentlyContinue).CommandLine
        if ($cmd -and $cmd -match "Ironfront\.MasterServer") {
            Write-Host "[playtest] killing dotnet host of the master (pid $($p.Id))"
            try { $p.Kill($true); $found++ } catch { }
        }
    }
    Write-Host "[playtest] stopped $found process(es)."
    exit 0
}

# ------------------------------------------------------------------------------------------
# 0. Preconditions
# ------------------------------------------------------------------------------------------
$player = Join-Path $repoRoot $PlayerPath
if (-not (Test-Path $player)) {
    Write-Host "[playtest] no player build at $PlayerPath."
    Write-Host "           pwsh tools/build-player.ps1     (Editor closed, ~10 minutes)"
    exit 1
}

# The player is a build artifact and says nothing about its own age. A binary older than the
# newest client source is a binary without the menu, the room browser or the scoreboard in it --
# and the symptom is a screen that looks like the old game, which reads as "the work was never
# done" rather than "you are running last week's build".
$asm = Join-Path $repoRoot "build/windows/Ironfront_Data/Managed/Assembly-CSharp.dll"
if (Test-Path $asm) {
    $built = (Get-Item $asm).LastWriteTime
    $newestSource = Get-ChildItem (Join-Path $repoRoot "Ironfront_Reborn/Assets/Scripts") -Recurse -Filter *.cs |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newestSource -and $newestSource.LastWriteTime -gt $built) {
        Write-Host "[playtest] WARNING: the player was built $built and"
        Write-Host "           $($newestSource.Name) changed $($newestSource.LastWriteTime)."
        Write-Host "           You are about to play a stale build. pwsh tools/build-player.ps1"
        Write-Host ""
    }
}

# runInBackground gates whether the three windows you are not looking at keep running. With it
# off, they freeze on focus loss and the frozen bodies read as a replication defect.
$projectSettings = Join-Path $repoRoot "Ironfront_Reborn/ProjectSettings/ProjectSettings.asset"
if ((Select-String -Path $projectSettings -Pattern "runInBackground: 1" -Quiet) -ne $true) {
    Write-Host "[playtest] WARNING: runInBackground is not 1 in ProjectSettings.asset."
    Write-Host "           Unfocused clients will freeze, and that will look like a network bug."
    Write-Host ""
}

if (-not (Assert-TcpPortsFree -Tag "playtest" -Ports @(
        @{ Port = $MasterPort;  What = "a master server" },
        @{ Port = $MetricsPort; What = "a metrics endpoint" }))) {
    Write-Host "      pwsh tools/playtest-local.ps1 -Stop"
    exit 1
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if ($FreshDb -and (Test-Path $dbPath)) {
    Remove-Item -Force $dbPath -ErrorAction SilentlyContinue
    Write-Host "[playtest] account database wiped -- every player registers again."
}

try {
    # --------------------------------------------------------------------------------------
    # 1. Master
    # --------------------------------------------------------------------------------------
    Write-Host "[playtest] building the master ($Configuration)"
    & dotnet build (Join-Path $repoRoot "Ironfront.MasterServer/Ironfront.MasterServer.csproj") `
        -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "the master server did not build" }

    # One secret per run, shared by the master and the game server this script starts. It signs
    # the join ticket; a client never sees it.
    $secret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))

    $masterEnv = @{
        IRONFRONT_SHARED_SECRET = $secret
        IRONFRONT_MASTER_PORT   = "$MasterPort"
        IRONFRONT_METRICS_PORT  = "$MetricsPort"
        IRONFRONT_METRICS_BIND  = "127.0.0.1"
        IRONFRONT_DB_PATH       = $dbPath
        # Error|Warn|Debug -- and NOTHING else. "Information" is rejected at startup and the
        # master then never opens its port, which this script reports as a 60-second timeout.
        # Debug rather than Warn because a first playtest is a diagnosis session.
        IRONFRONT_LOG_LEVEL     = "Debug"
        # See the header. Four registrations plus four logins against a 5/minute bucket has the
        # last of them refused, and the menu reports that refusal as a login failure.
        IRONFRONT_LOGIN_RATE_PER_MINUTE = "60"
    }
    foreach ($k in $masterEnv.Keys) { Set-Item -Path "Env:$k" -Value $masterEnv[$k] }

    Write-Host "[playtest] starting the master (tcp $MasterPort, db $dbPath)"
    $master = Start-Process -FilePath "dotnet" -PassThru -NoNewWindow `
        -ArgumentList @("run", "--project",
                        (Join-Path $repoRoot "Ironfront.MasterServer/Ironfront.MasterServer.csproj"),
                        "-c", $Configuration, "--no-build") `
        -RedirectStandardOutput $masterLog -RedirectStandardError $masterErr
    $processes += @{ Label = "master"; Process = $master }

    if (-not (Wait-ForTcpPort -Port $MasterPort -Seconds 60 -What "the master" -Tag "playtest")) {
        throw "the master never opened $MasterPort. See $masterLog and $masterErr."
    }
    Write-Host "[playtest] master is listening"

    # --------------------------------------------------------------------------------------
    # 2. Game server
    # --------------------------------------------------------------------------------------
    # IRONFRONT_LANEB_ROLE must be ABSENT or DedicatedServerSceneBootstrap stands down and the
    # process hosts nothing. This shell may have inherited one from a lane-B run.
    foreach ($stale in @("IRONFRONT_LANEB_ROLE", "IRONFRONT_LANEB_LABEL", "IRONFRONT_LANEB_SCENE",
                         "IRONFRONT_LANEB_PROGRAMME", "IRONFRONT_LANEB_OUTPUT", "IRONFRONT_ROLE")) {
        Remove-Item ("Env:" + $stale) -ErrorAction SilentlyContinue
    }

    $mapId = if ($Scene -eq "Island") { 2 } else { 1 }

    $env:IRONFRONT_MASTER_HOST          = "127.0.0.1"
    $env:IRONFRONT_GAMESERVER_UDP_PORT  = "$UdpPort"
    $env:IRONFRONT_GAMESERVER_PUBLIC_IP = "127.0.0.1"
    $env:IRONFRONT_GAMESERVER_TRANSPORT = "udp"
    $env:IRONFRONT_GAMESERVER_SCENE     = $Scene
    # REQUIRED. TryRegister refuses a registration with an empty map list, and the server is then
    # reaped as unauthenticated thirty seconds later -- which surfaces to a player as
    # "No server available" with a perfectly healthy-looking server process running.
    $env:IRONFRONT_GAMESERVER_MAP_IDS   = "$mapId"
    $env:IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS = "0"

    Write-Host "[playtest] starting the game server (udp $UdpPort, scene $Scene, map id $mapId)"
    $server = Start-Process -FilePath $player -PassThru `
        -ArgumentList @("-batchmode", "-nographics", "-logFile", $serverLog)
    $processes += @{ Label = "game-server"; Process = $server }

    $deadline = (Get-Date).AddSeconds($ServerReadySec)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        if ($server.HasExited) {
            throw "the game server exited $($server.ExitCode) before registering. See $serverLog."
        }
        $metrics = Read-Metrics -Port $MetricsPort
        if ($metrics -and $metrics -match $IronfrontHealthyPattern) {
            Write-Host "[playtest] the master reports $($Matches[1]) healthy game server(s)"
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

    # --------------------------------------------------------------------------------------
    # 3. The clients
    # --------------------------------------------------------------------------------------
    # A distinct id per client is not cosmetic: the server enforces one session per player, and
    # a second client reusing the first's id is refused with a bare InvalidTicket -- which reads
    # as a full server and is not one.
    # Set BACK ON, having been stripped above so the headless game server could claim its own
    # role. Belt-and-braces beside the declaration ClientFlowBootstrap now makes at the join:
    # this one lands in the log at startup ("[net] this process is a client") rather than at the
    # moment a map loads, so a run that goes wrong says which side each window was on before the
    # first thing goes wrong. Without either, an undeclared client wins nothing -- every map scene
    # carries an active NetServer, NetServerBootstrap takes the role, and all four windows bind
    # UDP :27015 against the server they just joined.
    $env:IRONFRONT_ROLE = "client"

    $env:IRONFRONT_CLIENT_MASTER_HOST = "127.0.0.1"
    $env:IRONFRONT_CLIENT_MASTER_PORT = "$MasterPort"

    $launched = @()
    for ($i = 1; $i -le $Clients; $i++) {
        $log = Join-Path $outDir "client-$i.log"
        $env:IRONFRONT_CLIENT_PLAYER_ID    = "$i"
        $env:IRONFRONT_CLIENT_DISPLAY_NAME = "P$i"

        $args = @("-logFile", $log,
                  "-screen-fullscreen", "0",
                  "-screen-width", "$Width",
                  "-screen-height", "$Height")

        $c = Start-Process -FilePath $player -PassThru -ArgumentList $args
        $processes += @{ Label = "client-$i"; Process = $c }
        $launched  += @{ Index = $i; Process = $c; Log = $log }
        Write-Host "[playtest] client $i (P$i) started, log $log"
        Start-Sleep -Milliseconds 900   # stagger: four Unity players opening at once thrash the GPU
    }

    if (-not $NoTile) { Set-TiledWindows -Launched $launched -Width $Width -Height $Height }

    # --------------------------------------------------------------------------------------
    # 4. What the human does now
    # --------------------------------------------------------------------------------------
    Write-Host ""
    Write-Host "================ $Clients client(s) up. Do this in EACH window: ================"
    Write-Host "  1. Register  -- username p1 .. p$Clients, any password you will remember."
    Write-Host "     The account database is kept between runs, so this is a first-time step;"
    Write-Host "     pass -FreshDb when you want it wiped."
    Write-Host "  2. Log in. The master address is pre-filled at 127.0.0.1:$MasterPort."
    Write-Host "  3. Room browser -> the $Scene room -> pick a side -> Ready."
    Write-Host "  4. When every player is ready the match starts and the map loads."
    Write-Host "     Tab shows the scoreboard; alt-tab between windows to play the other side."
    Write-Host ""
    Write-Host "Logs:   $outDir"
    Write-Host "Master: $masterLog"
    Write-Host "Server: $serverLog"
    Write-Host ""
    Write-Host "Ctrl+C here tears the whole stack down."
    Write-Host "==============================================================================="

    # Hold the stack up until the human is done, or until the last client is closed.
    while ($true) {
        Start-Sleep -Seconds 2
        $alive = @($launched | Where-Object { -not $_.Process.HasExited })
        if ($alive.Count -eq 0) {
            Write-Host "[playtest] every client has closed; shutting the stack down."
            break
        }
        if ($server.HasExited) {
            Write-Host "[playtest] the game server exited $($server.ExitCode). See $serverLog."
            break
        }
    }
}
finally {
    Stop-Started
}
