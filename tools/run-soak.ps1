<#
.SYNOPSIS
    The M4 30-minute continuous-play soak: launches a dedicated server and a playable client,
    samples both processes' memory, and grades the log and the curve afterwards.

.DESCRIPTION
    M4's clause is "30 minutes of continuous play with no crash and no leak", and P8 section 2
    is explicit that this is NOT P7's five-match soak: that one asserts pool cleanliness between
    matches, this one is wall-clock WITH A HUMAN IN IT. So this script does not simulate a
    player and does not claim to. It starts the two processes, watches them, and produces the
    two artifacts the clause is graded on -- the log and the memory curve -- while somebody
    plays.

    WHAT IT DOES NOT DO, DELIBERATELY. It drives no input. A synthetic 30 minutes is what
    tools/run-lane-a.ps1 already produces and it is not what this clause asks for; a harness
    that played the game for you would grade the harness. The human is the instrument here and
    the script is the recorder.

    THE CSV SHAPE IS NOT NEW. It is the one tools/chart-durability.ps1 already reads --
    tsUtc, workingSetMB, connCurrent, errorsPerMin, uptimeSec -- so the chart and the
    conservative leak verdict come for free rather than being written a second time. That
    script's own reasoning applies unchanged: a rising sawtooth is healthy, and what indicates a
    leak is memory climbing while load stays flat.

    WHY IT SAMPLES THE PLAYER PROCESSES RATHER THAN THE METRICS ENDPOINT.
    IRONFRONT_METRICS_CSV is the MASTER server's instrument. This clause is about the game
    server and the client, neither of which exposes one, so working set is read from the OS.
    That is coarser than a GC-aware number and it is the number a leak actually shows up in.

.PARAMETER Minutes
    Wall-clock length. The clause says 30; shorter values are for proving the script works.

.PARAMETER Record
    Also record the desktop, so the same 30 minutes produces the demo video M4 asks for.
    Requires ffmpeg on PATH -- see tools/capture-lane-b-video.ps1, which owns that path.

.PARAMETER SkipServer
    Do not launch a server; sample and grade a client against one already running elsewhere.

.EXAMPLE
    pwsh tools/run-soak.ps1 -Tag m4-soak-01 -Minutes 30 -Record
    pwsh tools/run-soak.ps1 -Tag smoke -Minutes 2          # prove the plumbing first
#>
[CmdletBinding()]
param(
    # Names every artifact of this run. Must be unique per run or the previous one is overwritten.
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    # Recorded verbatim into the report, and the only place a reader learns why the run happened.
    [string] $Label = "",

    [int] $Minutes = 30,

    [string] $PlayerDirectory = "build/windows",
    [string] $OutputDirectory = "artifacts/soak",

    [string] $Scene = "Dustbowl",
    [int] $Port = 27015,

    # Seconds between memory samples. 10 gives 180 rows over 30 minutes, which is enough to see
    # a slope and few enough to read as a table when the chart is not available.
    [int] $SampleSeconds = 10,

    # How long to give the server to load its map and bind before the client is launched.
    [int] $ServerWarmupSeconds = 25,

    [switch] $Record,
    [switch] $SkipServer
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $player = Join-Path $repoRoot "$PlayerDirectory/Ironfront.exe"
    if (-not (Test-Path $player)) {
        throw "no player at $player. Build it with the Editor CLOSED: " +
              "pwsh tools/run-lane-b.ps1 -Build"
    }

    $runDirectory = Join-Path $repoRoot "$OutputDirectory/$Tag"
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

    $serverLog = Join-Path $runDirectory "server.log"
    $clientLog = Join-Path $runDirectory "client.log"
    $csvPath   = Join-Path $runDirectory "memory.csv"
    $metaPath  = Join-Path $runDirectory "run.json"

    $totalSeconds = $Minutes * 60

    Write-Host "[soak] tag        : $Tag"
    Write-Host "[soak] label      : $Label"
    Write-Host "[soak] length     : $Minutes min ($totalSeconds s)"
    Write-Host "[soak] artifacts  : $runDirectory"
    Write-Host ""

    # ------------------------------------------------------------------ the server
    $server = $null
    if (-not $SkipServer) {
        # A genuine dedicated server, not a harness role: IRONFRONT_LANEB_ROLE is deliberately
        # NOT set, because setting it stands both DedicatedServerSceneBootstrap and
        # ClientFlowBootstrap down -- the harness owns scene loading when it is present, and
        # this run is about the shipped path.
        $env:IRONFRONT_ROLE                          = "server"
        $env:IRONFRONT_GAMESERVER_SCENE              = $Scene
        $env:IRONFRONT_GAMESERVER_TRANSPORT          = "udp"
        $env:IRONFRONT_GAMESERVER_UDP_PORT           = "$Port"
        $env:IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS = "1"

        # Cleared, not left over from a shell that ran lane B: with a secret set the server
        # refuses the unsigned ticket a direct-connect client mints, and the refusal is reported
        # as a bare InvalidTicket, which reads as a full server.
        $env:IRONFRONT_SHARED_SECRET = ""

        $server = Start-Process -FilePath $player -PassThru -NoNewWindow -ArgumentList @(
            "-batchmode", "-nographics", "-logFile", $serverLog)

        Write-Host "[soak] server pid $($server.Id); warming up for $ServerWarmupSeconds s"
        Start-Sleep -Seconds $ServerWarmupSeconds

        if ($server.HasExited) {
            throw "the server exited during warm-up with code $($server.ExitCode). See $serverLog."
        }
    }

    # ------------------------------------------------------------------ the client
    # Windowed and rendered, because a person is going to play it. The flow starts at the login
    # screen; with no master running, "Connect directly" at 127.0.0.1:$Port is the whole route
    # in, and the map now loads itself from the id the server announces in CONNECT_ACCEPTED.
    $env:IRONFRONT_ROLE        = "client"
    $env:IRONFRONT_CLIENT_HOST = "127.0.0.1"
    $env:IRONFRONT_CLIENT_PORT = "$Port"

    $client = Start-Process -FilePath $player -PassThru -ArgumentList @(
        "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720",
        "-logFile", $clientLog)

    Write-Host "[soak] client pid $($client.Id)"
    Write-Host ""
    Write-Host "[soak] PLAY NOW. Direct-connect to 127.0.0.1:$Port from the login screen." -ForegroundColor Yellow
    Write-Host "[soak] Sampling every $SampleSeconds s until $((Get-Date).AddSeconds($totalSeconds).ToString('HH:mm:ss'))." -ForegroundColor Yellow
    Write-Host ""

    # ------------------------------------------------------------------ the recorder
    $recorder = $null
    if ($Record) {
        $videoPath = Join-Path $runDirectory "soak.mp4"
        try {
            $recorder = Start-Process -FilePath "ffmpeg" -PassThru -NoNewWindow -ArgumentList @(
                "-y", "-f", "gdigrab", "-framerate", "30", "-i", "desktop",
                "-t", "$totalSeconds", "-c:v", "libx264", "-preset", "veryfast",
                "-pix_fmt", "yuv420p", $videoPath)
            Write-Host "[soak] recording to $videoPath (pid $($recorder.Id))"
        }
        catch {
            # A missing recorder must not cost the run. The memory curve and the log are the
            # graded artifacts; the video is a separate M4 clause and can be recorded again.
            Write-Warning "[soak] ffmpeg did not start, so no video: $($_.Exception.Message)"
            Write-Warning "[soak] the soak continues -- the log and the curve are unaffected."
            $recorder = $null
        }
    }

    # ------------------------------------------------------------------ sampling
    # The header is chart-durability.ps1's, exactly. connCurrent is 1 while the client process
    # is alive and 0 after it exits: that is the "load" series the leak correlation needs, and a
    # constant would make the correlation meaningless rather than merely coarse.
    "tsUtc,workingSetMB,connCurrent,errorsPerMin,uptimeSec,clientWorkingSetMB" |
        Set-Content -Path $csvPath -Encoding utf8

    $startedAt = Get-Date
    $lastErrorCount = 0

    while (((Get-Date) - $startedAt).TotalSeconds -lt $totalSeconds) {
        Start-Sleep -Seconds $SampleSeconds

        $uptime = [long]((Get-Date) - $startedAt).TotalSeconds

        $serverRss = 0
        if ($server -ne $null -and -not $server.HasExited) {
            $server.Refresh()
            $serverRss = [int]($server.WorkingSet64 / 1MB)
        }
        elseif ($server -ne $null) {
            Write-Warning "[soak] the SERVER exited after $uptime s. That is a crash, and the run has already failed."
            break
        }

        $clientRss = 0
        $clientAlive = 0
        if (-not $client.HasExited) {
            $client.Refresh()
            $clientRss = [int]($client.WorkingSet64 / 1MB)
            $clientAlive = 1
        }
        else {
            Write-Warning "[soak] the CLIENT exited after $uptime s."
            break
        }

        # Counted from the log rather than from a metrics endpoint, because neither process has
        # one. Per-minute rate, so it lines up with chart-durability.ps1's column.
        $errorCount = 0
        if (Test-Path $serverLog) {
            $errorCount = (Select-String -Path $serverLog -Pattern 'Exception|error CS|\[error\]' `
                                         -AllMatches -ErrorAction SilentlyContinue).Count
        }
        $errorsPerMin = [math]::Round((($errorCount - $lastErrorCount) * 60.0 / $SampleSeconds), 2)
        $lastErrorCount = $errorCount

        $row = "{0},{1},{2},{3},{4},{5}" -f `
            (Get-Date).ToUniversalTime().ToString("o"), $serverRss, $clientAlive, $errorsPerMin, $uptime, $clientRss
        Add-Content -Path $csvPath -Value $row

        Write-Host ("[soak] t+{0,5}s  server {1,5} MB  client {2,5} MB  errors/min {3}" -f `
            $uptime, $serverRss, $clientRss, $errorsPerMin)
    }

    $ranSeconds = [long]((Get-Date) - $startedAt).TotalSeconds

    # ------------------------------------------------------------------ shutdown
    foreach ($p in @($recorder, $client, $server)) {
        if ($p -eq $null -or $p.HasExited) { continue }
        try { $p.CloseMainWindow() | Out-Null } catch { }
        if (-not $p.WaitForExit(10000)) { try { $p.Kill() } catch { } }
    }

    @{
        tag            = $Tag
        label          = $Label
        requestedSecs  = $totalSeconds
        actualSecs     = $ranSeconds
        heldToTheEnd   = ($ranSeconds -ge ($totalSeconds - $SampleSeconds))
        scene          = $Scene
        port           = $Port
        sampleSeconds  = $SampleSeconds
        recorded       = ($recorder -ne $null)
        startedAtUtc   = $startedAt.ToUniversalTime().ToString("o")
    } | ConvertTo-Json | Set-Content -Path $metaPath -Encoding utf8

    Write-Host ""
    Write-Host "[soak] ran $ranSeconds s of $totalSeconds requested."
    Write-Host "[soak] artifacts in $runDirectory"
    Write-Host ""
    Write-Host "Grade it:"
    Write-Host "  python tools/grade_soak.py $runDirectory"
    Write-Host "  pwsh tools/chart-durability.ps1 -CsvPath $csvPath -OutputPath $runDirectory/curve.html"
}
finally {
    Pop-Location
}
