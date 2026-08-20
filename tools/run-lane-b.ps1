# tools/run-lane-b.ps1 -- the phase-3D lane-B runner: one headless server, three rendered
# scripted clients, one artifact per checkpoint, non-zero on any failure.
#
# WHAT IT DOES. Launches the SAME Windows player four times. One process gets
# IRONFRONT_LANEB_ROLE=server and -batchmode -nographics; three get role=client, a distinct
# player id, a distinct display name, and their own recorded input programme. LaneBHarness
# inside each process strips the half of the Dustbowl scene that process is not (the map ships
# an active NetServer AND an active NetClient, so every process that loads it is otherwise a
# listen server), runs the programme, captures at each checkpoint, and quits with a code.
#
# WHY THREE. phase-3-harness.md section 2 check 7 reads "two clients see the same vehicle in the
# same place WHILE A THIRD DRIVES IT". Ten of the eleven lane-B checks read two of the three;
# check 7 needs the third in the driver's seat. Sizing this for two and finding out later is the
# avoidable version of that sentence.
#
# WHY EACH CLIENT GETS ITS OWN PLAYER ID. The server enforces one session per player once a
# shared secret is configured, so instances sharing an id have every join after the first
# rejected -- reported to the client as a bare InvalidTicket, which reads as a full server and is
# not one. Unset now derives an id from the process id, so the collision is no longer automatic;
# a run that has to be replayable against fixed identities sets it anyway, and this does.
#
# WHY THE SERVER IS A WINDOWS PLAYER HERE. The product's server is the Linux dedicated build and
# stays so. This runner is scaffolding on the machine the work happens on, and that machine is
# Windows. A verdict reached here describes the GAME, not the deployment target -- a Windows-Mono
# headless server is not the Linux server byte for byte, and any check that turns on server-side
# floating point or platform behaviour must be re-read on Linux before it is trusted.
#
# Usage:
#   $env:UNITY_PATH = "C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe"
#   pwsh tools/run-lane-b.ps1 -Build -Smoke
#   pwsh tools/run-lane-b.ps1 -Set combat -OutputDirectory artifacts/lane-b/combat-01
#   pwsh tools/run-lane-b.ps1 -Set vehicle -Sim typical -OutputDirectory artifacts/lane-b/vehicle-01
#
# PROGRAMME SETS. -Set names a family of recorded programmes under tools/lane-b/. Each client
# takes <set>-<label>.json when that file exists and <set>.json when it does not, so the smoke
# runs one programme on all three while a check set gives the shooter, the victim and the
# witness a different one each.
#
# OUTPUT: <OutputDirectory>/ holds one -summary.json and one -checkpoints.jsonl per process,
# a PNG per client checkpoint, the four process logs, and run.json with both seeds.

[CmdletBinding()]
param(
    # Build the Windows player before running. Skip it when build/windows is already current.
    [switch] $Build,

    # The Unity Editor, for -Build only. Defaults to $env:UNITY_PATH, like tools/build-server.ps1.
    [string] $UnityPath = $env:UNITY_PATH,

    # Where the player lives (and lands, with -Build).
    [string] $PlayerDirectory = "build/windows",

    # 14 seconds of scripted input, no combat, no vehicle -- proves the three-client bring-up
    # before any check runs. phase-3d-lane-b.md section 8 row 1 requires this first. Sugar for
    # -Set smoke, and it wins if both are given.
    [switch] $Smoke,

    # Which programme set to run. Each client looks for tools/lane-b/<set>-<label>.json and
    # falls back to tools/lane-b/<set>.json when there is no per-label file -- which is how the
    # smoke keeps running one programme on all three while a check set gives each client its
    # own. A check set NEEDS that: check 1 has a shooter, a victim and a witness, and giving
    # all three the shooter's programme would have three clients firing at each other and no
    # observer left to grade the killfeed.
    [string] $Set = "combat",

    # NetworkSimulator preset applied to every process. "typical" is 50 ms one-way (100 ms RTT)
    # with 5% loss, which is check 7's stated condition exactly.
    [ValidateSet("off", "lan", "good", "typical", "bad", "awful")]
    [string] $Sim = "off",

    # The two seeds. Both are printed with the results, because a report naming one claims a
    # reproducibility it does not have (phase-3d-lane-b.md section 4.4).
    [int] $SimSeed = 12345,
    [int] $UnitySeed = 20260821,

    [string] $OutputDirectory = "artifacts/lane-b",

    # Per-process budget. A client that has not finished its programme by then quits 2.
    [int] $TimeoutSeconds = 300,

    # How long to wait for "[lane-b] server ready" -- the SLOTS, not the port. A server whose
    # transport is bound still refuses every join until FillPlayerSlots has run in Start, and
    # racing that is phase-3d-lane-b.md section 8 row 6.
    [int] $ServerReadySeconds = 120,

    [int] $Port = 27015,

    # Signed tickets are the real path (issue #151): with a secret set the client mints and
    # signs its own ticket and the server verifies it. Empty means unsigned, which exercises a
    # path no shipped server should run.
    [string] $SharedSecret = "lane-b-harness-secret"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$player   = Join-Path $repoRoot (Join-Path $PlayerDirectory "Ironfront.exe")
$outDir   = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory }
            else { Join-Path $repoRoot $OutputDirectory }
$progDir  = Join-Path $repoRoot "tools/lane-b"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --------------------------------------------------------------------------------------
# Build
# --------------------------------------------------------------------------------------
if ($Build) {
    if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
        throw "UNITY_PATH is not set or does not exist. -Build needs the Editor; without it, " +
              "build the player once from the menu (Ironfront > Build Windows Player) and " +
              "re-run without -Build."
    }

    $buildOut = Join-Path $repoRoot $PlayerDirectory
    $buildLog = Join-Path $outDir "build.log"
    Write-Host "[lane-b] building the Windows player -> $buildOut (log: $buildLog)"

    $buildArgs = @(
        "-batchmode", "-quit", "-nographics",
        "-projectPath", (Join-Path $repoRoot "Ironfront_Reborn"),
        "-executeMethod", "Ironfront.EditorBuildWindowsHarness.BuildWindowsPlayer",
        "-buildOutput", $buildOut,
        "-logFile", $buildLog
    )

    # NOT $build: that is this script's own -Build switch parameter, and assigning a Process to
    # it fails with a SwitchParameter conversion error AFTER the build has already run -- so the
    # build succeeds and the script reports failure.
    $buildProcess = Start-Process -FilePath $UnityPath -ArgumentList $buildArgs -PassThru -Wait -NoNewWindow
    if ($buildProcess.ExitCode -ne 0) {
        throw "the Windows player build exited $($buildProcess.ExitCode). See $buildLog."
    }
}

if (-not (Test-Path $player)) {
    throw "no player at $player. Run once with -Build, with the Unity Editor CLOSED. The " +
          "Editor holds the project lock, and the menu item of the same name refuses in a " +
          "live Editor for a second reason: stripping UNITY_MCP_READY queues a recompile and " +
          "BuildPlayer will not start during one."
}

# --------------------------------------------------------------------------------------
# The three clients
#
# Player ids are fixed and well above GameClientConfig.ReservedIdCeiling (1024), which is the
# range the load harness numbers its synthetic clients from. Display names are distinct because
# check 1 grades a killfeed line WITH A NAME, and three clients on one default produce a
# killfeed nobody can read.
# --------------------------------------------------------------------------------------
if ($Smoke) { $Set = "smoke" }

$clients = @(
    @{ Label = "driver";     PlayerId = 5001; Name = "DRIVER" }
    @{ Label = "observer-a"; PlayerId = 5002; Name = "OBS-A"  }
    @{ Label = "observer-b"; PlayerId = 5003; Name = "OBS-B"  }
)

# Per-label first, shared second. The smoke has one programme for all three because it proves
# bring-up rather than a check; a check set gives each client its own, because the roles are
# not interchangeable.
foreach ($c in $clients) {
    $perLabel = "$Set-$($c.Label).json"
    $shared   = "$Set.json"

    if (Test-Path (Join-Path $progDir $perLabel))   { $c.Programme = $perLabel }
    elseif (Test-Path (Join-Path $progDir $shared)) { $c.Programme = $shared }
    else {
        throw "no input programme for '$($c.Label)' in set '$Set': looked for " +
              "$perLabel then $shared under $progDir"
    }

    $c.ProgrammePath = Join-Path $progDir $c.Programme
}

# --------------------------------------------------------------------------------------
# Launch
# --------------------------------------------------------------------------------------
function Set-CommonEnvironment {
    $env:IRONFRONT_LANEB_ARTIFACTS = $outDir
    $env:IRONFRONT_LANEB_UNITY_SEED = "$UnitySeed"
    $env:IRONFRONT_LANEB_TIMEOUT = "$TimeoutSeconds"
    $env:IRONFRONT_LANEB_SCENE = "Dustbowl"
    $env:IRONFRONT_SHARED_SECRET = $SharedSecret

    # Unrecognised or absent returns a DISABLED config by design, so "off" needs no special case.
    $env:IRONFRONT_SIM = $Sim
    $env:IRONFRONT_SIM_SEED = "$SimSeed"
}

function Clear-ClientEnvironment {
    foreach ($n in @("IRONFRONT_LANEB_ROLE", "IRONFRONT_LANEB_LABEL", "IRONFRONT_LANEB_PROGRAMME",
                     "IRONFRONT_CLIENT_PLAYER_ID", "IRONFRONT_CLIENT_DISPLAY_NAME",
                     "IRONFRONT_GAMESERVER_TRANSPORT", "IRONFRONT_GAMESERVER_UDP_PORT")) {
        Remove-Item "env:$n" -ErrorAction SilentlyContinue
    }
}

$processes = @()

try {
    Set-CommonEnvironment
    Clear-ClientEnvironment

    # ---- server ----
    $serverLog = Join-Path $outDir "server.log"
    $env:IRONFRONT_LANEB_ROLE = "server"
    $env:IRONFRONT_LANEB_LABEL = "server"
    $env:IRONFRONT_GAMESERVER_TRANSPORT = "udp"
    $env:IRONFRONT_GAMESERVER_UDP_PORT = "$Port"

    Write-Host "[lane-b] starting the server (port $Port, sim=$Sim/$SimSeed)"
    $server = Start-Process -FilePath $player -PassThru -ArgumentList @(
        "-batchmode", "-nographics", "-logFile", $serverLog)
    $processes += @{ Label = "server"; Process = $server }

    # Wait for the SLOTS, not the port. See -ServerReadySeconds.
    $deadline = (Get-Date).AddSeconds($ServerReadySeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($server.HasExited) {
            throw "the server exited $($server.ExitCode) before it was ready. See $serverLog."
        }
        if ((Test-Path $serverLog) -and
            (Select-String -Path $serverLog -Pattern '\[lane-b\] server ready' -Quiet -ErrorAction SilentlyContinue)) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        throw "the server never logged '[lane-b] server ready' within ${ServerReadySeconds}s. See $serverLog."
    }

    $readyLine = (Select-String -Path $serverLog -Pattern '\[lane-b\] server ready.*').Matches[0].Value
    Write-Host "[lane-b] $readyLine"

    # ---- clients ----
    foreach ($c in $clients) {
        Clear-ClientEnvironment
        $env:IRONFRONT_LANEB_ROLE = "client"
        $env:IRONFRONT_LANEB_LABEL = $c.Label
        $env:IRONFRONT_LANEB_PROGRAMME = $c.ProgrammePath
        $env:IRONFRONT_CLIENT_PLAYER_ID = "$($c.PlayerId)"
        $env:IRONFRONT_CLIENT_DISPLAY_NAME = $c.Name
        $env:IRONFRONT_CLIENT_HOST = "127.0.0.1"
        $env:IRONFRONT_CLIENT_PORT = "$Port"

        # A client process must be CONFIGURED not to open a server socket, not merely left
        # unset. LaneBHarness strips the scene's NetServer, but the strip runs in sceneLoaded
        # and the transport is bound in Awake -- so by the time anything can be stripped the
        # socket is already open. Leaving this unset is what the first three-client run did:
        # every process loads the repo-root .env from its working directory, .env says
        # IRONFRONT_GAMESERVER_TRANSPORT=udp, and all three clients bound 27015 behind the real
        # server, took a SocketException, and lost their own connection to TransportError a
        # second after joining. Stating loopback here beats whatever .env says, because DotEnv
        # skips a variable that is already set in the process.
        $env:IRONFRONT_GAMESERVER_TRANSPORT = "loopback"

        # Belt and braces, and a distinct one each: if some future path binds anyway, three
        # clients must not collide with the server OR with each other. Loopback opens no
        # socket, so this number is never used on the intended path.
        $env:IRONFRONT_GAMESERVER_UDP_PORT = "$($Port + 100 + $clients.IndexOf($c))"

        $log = Join-Path $outDir "$($c.Label).log"
        Write-Host "[lane-b] starting client '$($c.Label)' id=$($c.PlayerId) name=$($c.Name) prog=$($c.Programme)"

        $p = Start-Process -FilePath $player -PassThru -ArgumentList @(
            "-screen-width", "960", "-screen-height", "540", "-screen-fullscreen", "0",
            "-logFile", $log)

        $processes += @{ Label = $c.Label; Process = $p }

        # Staggered: three Unity players opening a window and loading the same scene at once on
        # one machine is a disk and GPU stampede, and the join order stops being reproducible.
        Start-Sleep -Seconds 3
    }

    # ---- wait ----
    $clientProcs = $processes | Where-Object { $_.Label -ne "server" }
    $waitDeadline = (Get-Date).AddSeconds($TimeoutSeconds + 120)

    while ((Get-Date) -lt $waitDeadline) {
        if (($clientProcs | Where-Object { -not $_.Process.HasExited }).Count -eq 0) { break }
        Start-Sleep -Seconds 2
    }
}
finally {
    Clear-ClientEnvironment

    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            Write-Host "[lane-b] stopping '$($entry.Label)' (pid $($entry.Process.Id))"
            Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

# --------------------------------------------------------------------------------------
# Grade
#
# From the per-process summary files, not from the logs. A runner that graded a log would make
# the verdict a regex over prose that nothing keeps stable.
# --------------------------------------------------------------------------------------
$failures = @()
$rows = @()

foreach ($c in $clients) {
    $summaryPath = Join-Path $outDir "$($c.Label)-summary.json"
    if (-not (Test-Path $summaryPath)) {
        $failures += "$($c.Label): wrote no summary (see $($c.Label).log)"
        $rows += [pscustomobject]@{ Client = $c.Label; Exit = "-"; Checkpoints = 0; Reason = "no summary" }
        continue
    }

    $summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
    $rows += [pscustomobject]@{
        Client      = $c.Label
        Exit        = $summary.exitCode
        Checkpoints = $summary.checkpoints
        Reason      = $summary.reason
    }

    if ($summary.exitCode -ne 0) { $failures += "$($c.Label): exit $($summary.exitCode) -- $($summary.reason)" }
    elseif ($summary.checkpoints -lt 1) { $failures += "$($c.Label): exit 0 but captured no checkpoint" }

    # The seed this runner PRINTS must be the seed the process actually DREW from. They are
    # two different numbers the moment anything mistypes the parse, and the first run of this
    # harness proved it: LaneBHarness read the seed through a float, float32 stops representing
    # consecutive integers above 16,777,216, and 20260821 silently became 20260820. The run
    # reported a seed that would not reproduce it, which is worse than reporting none -- it
    # looks reproducible. This is the only check that can tell the difference.
    if ($summary.unitySeed -ne $UnitySeed) {
        $failures += "$($c.Label): drew from unity seed $($summary.unitySeed) but the run says $UnitySeed"
    }
    if ($summary.simSeed -ne $SimSeed) {
        $failures += "$($c.Label): drew from simulator seed $($summary.simSeed) but the run says $SimSeed"
    }
    if ($summary.playerId -ne $c.PlayerId) {
        $failures += "$($c.Label): joined as player $($summary.playerId), not $($c.PlayerId)"
    }
}

Write-Host ""
Write-Host "[lane-b] seeds -- UnityEngine.Random=$UnitySeed  NetworkSimulator=$Sim/$SimSeed"
Write-Host "[lane-b] artifacts -> $outDir"
$rows | Format-Table -AutoSize | Out-String | Write-Host

$run = [ordered]@{
    unitySeed      = $UnitySeed
    simulatorPreset= $Sim
    simulatorSeed  = $SimSeed
    port           = $Port
    set            = $Set
    smoke          = [bool]$Smoke
    clients        = $clients | ForEach-Object { @{ label = $_.Label; playerId = $_.PlayerId; displayName = $_.Name; programme = $_.Programme } }
    failures       = $failures
    passed         = ($failures.Count -eq 0)
}
$run | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $outDir "run.json")

if ($failures.Count -gt 0) {
    Write-Host "[lane-b] FAILED:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "[lane-b] all $($clients.Count) clients completed their programme." -ForegroundColor Green
exit 0
