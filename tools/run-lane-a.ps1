# tools/run-lane-a.ps1 -- the lane-A runner: one headless Windows player as the game server,
# one harness process with N synthetic clients, one report and one capture per run.
#
# WHY THIS EXISTS. Every lane-A run before this one was assembled by hand from the procedure
# written into a report (phase-3e section 3, phase-4 section 3). That is fine once and wrong
# twice: the two seeds, the shared secret, the server's role variable and the tick-JSONL path
# all have to agree between two processes, and a run that gets one of them wrong does not fail
# -- it produces a report that looks exactly like a good one. R3 needed four more runs, which
# made this the second occurrence (rules/replicate-and-automate.md).
#
# TWO SEEDS, BECAUSE THEY ARE TWO GENERATORS. IRONFRONT_LOAD_SEED pins the server's spawn
# selection; -SimSeed pins the harness's impairment sequence. A run that reported one of them
# would be claiming reproducibility it does not have.
#
# Usage:
#   pwsh tools/run-lane-a.ps1 -Tag r3-clean   -Label "R3 clean baseline"
#   pwsh tools/run-lane-a.ps1 -Tag r3-typical -Sim typical -Label "R3 at check 7's condition"
#
# OUTPUT: artifacts/lane-a/<OutputDirectory>/<tag>-{report.json,capture.jsonl,ticks.jsonl,server.log}

[CmdletBinding()]
param(
    # Names every artifact of this run. Must be unique per run or the previous one is overwritten.
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    # Recorded verbatim into the report, and the only place a reader learns why the run happened.
    [string] $Label = "",

    [string] $PlayerDirectory = "build/windows",
    [string] $OutputDirectory = "artifacts/lane-a",

    [int] $Clients = 8,
    [int] $Seconds = 120,
    [int] $InputHz = 30,
    # combat is R5's addition (ledger X-34): it drives, fires, dies and respawns, which are
    # check 11's four verbs. It puts reliable channel-2 traffic on the wire that move never
    # sends, so a bandwidth figure taken under it is NOT comparable with the phase-4 baselines.
    [ValidateSet("idle", "move", "combat")]
    [string] $Behavior = "move",

    # A NetworkSimulator preset name, or "off" for a clean wire.
    [ValidateSet("off", "lan", "good", "typical", "bad", "awful")]
    [string] $Sim = "off",

    [int] $SimSeed = 12345,
    [int] $LoadSeed = 12345,
    [int] $Port = 27015,
    [string] $Secret = "lane-b-harness-secret",

    # How long to give the player to load Dustbowl and open its socket before the harness runs.
    [int] $ServerWarmupSeconds = 20,

    # The SERVER's own lifetime, in seconds. Zero derives it from -Seconds.
    #
    # WHY THIS EXISTS. -Seconds reaches the HARNESS only; the server ends on LaneBHarness's own
    # schedule, whose default is 300 s. So -Seconds 360 did not make a longer run, it made a
    # TRUNCATED one -- snapshots froze at t+307 s, every client disconnected with LocalRequest,
    # and the report still printed a full verb table computed over the ~300 s that did happen.
    # A reader skimming for a pass saw a clean one. Deriving the server's lifetime from the
    # harness's, with a margin for warm-up and shutdown, is what makes the two agree by
    # construction instead of by the operator remembering.
    [int] $ServerTimeoutSeconds = 0
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

    $outDir = Join-Path $repoRoot $OutputDirectory
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $serverLog = Join-Path $outDir "$Tag-server.log"
    $ticks     = Join-Path $outDir "$Tag-ticks.jsonl"
    $report    = Join-Path $outDir "$Tag-report.json"
    $capture   = Join-Path $outDir "$Tag-capture.jsonl"
    $errors    = Join-Path $outDir "$Tag-errors.jsonl"
    $summary   = "$ticks.summary.json"

    # 15 s of margin over the harness window. Long enough that the server outlives warm-up,
    # the run and the harness's shutdown -- a server that ends first truncates the run while the
    # report still looks complete -- and short enough that waiting for its own exit below is
    # cheap. It has to END ON ITS OWN: see the wait in the finally block.
    if ($ServerTimeoutSeconds -le 0) {
        $ServerTimeoutSeconds = $ServerWarmupSeconds + $Seconds + 15
    }
    elseif ($ServerTimeoutSeconds -lt ($ServerWarmupSeconds + $Seconds)) {
        throw "-ServerTimeoutSeconds $ServerTimeoutSeconds is shorter than warm-up + run " +
              "($ServerWarmupSeconds + $Seconds). The server would end mid-run and the report " +
              "would still look complete. Raise it, or lower -Seconds."
    }

    Write-Host "[lane-a] $Tag : $Clients client(s) / $Seconds s / server lifetime ${ServerTimeoutSeconds}s / sim=$Sim / loadSeed=$LoadSeed simSeed=$SimSeed"

    # --------------------------------------------------------------- the server
    $env:IRONFRONT_LANEB_ROLE             = "server"
    $env:IRONFRONT_GAMESERVER_TRANSPORT   = "udp"
    $env:IRONFRONT_GAMESERVER_UDP_PORT    = "$Port"
    $env:IRONFRONT_SHARED_SECRET          = $Secret
    $env:IRONFRONT_LOAD_JSONL             = $ticks
    $env:IRONFRONT_LOAD_SEED              = "$LoadSeed"
    $env:IRONFRONT_LOAD_ERRORS            = $errors
    $env:IRONFRONT_LANEB_TIMEOUT          = "$ServerTimeoutSeconds"

    $server = Start-Process -FilePath $player -PassThru -NoNewWindow -ArgumentList @(
        "-batchmode", "-nographics", "-logFile", $serverLog)

    Write-Host "[lane-a] server pid $($server.Id); warming up for $ServerWarmupSeconds s"
    Start-Sleep -Seconds $ServerWarmupSeconds

    if ($server.HasExited) {
        throw "the server exited during warm-up with code $($server.ExitCode). See $serverLog."
    }

    # --------------------------------------------------------------- the harness
    try {
        $harnessArgs = @(
            "Ironfront.Net.LoadHarness/bin/Release/net8.0/Ironfront.Net.LoadHarness.dll",
            "--clients", "$Clients",
            "--seconds", "$Seconds",
            "--behavior", $Behavior,
            "--input-hz", "$InputHz",
            "--port", "$Port",
            "--secret", $Secret,
            "--report", $report,
            "--capture", $capture)

        if ($Label) { $harnessArgs += @("--label", $Label) }
        if ($Sim -ne "off") { $harnessArgs += @("--sim", $Sim, "--sim-seed", "$SimSeed") }

        & dotnet @harnessArgs
        $harnessExit = $LASTEXITCODE
    }
    finally {
        # WAIT FIRST, KILL SECOND -- and the order is the whole point.
        #
        # Stop-Process -Force takes the player down without OnApplicationQuit, so
        # HeadlessLoadBootstrap.Close never runs: no summary file, no logByType, and the last
        # partial buffer of the tick JSONL lost. That is why no lane-A run before this one ever
        # produced a *.summary.json -- the runner had been killing the writer every time. The
        # server now ends on its own IRONFRONT_LANEB_TIMEOUT a few seconds after the harness, so
        # the right move is to let it, and to kill only a server that overstays.
        #
        # The kill still has to exist: a leftover player holds UDP 27015 and the NEXT run's
        # clients are refused by a server nobody is looking at.
        $graceMs = ($ServerTimeoutSeconds + 60) * 1000
        if (-not $server.HasExited) {
            Write-Host "[lane-a] waiting up to $([int]($graceMs / 1000)) s for the server to write its summary and exit"
            $server.WaitForExit($graceMs) | Out-Null
        }

        if (-not $server.HasExited) {
            Write-Warning ("[lane-a] the server outstayed its own timeout and was killed. " +
                           "$summary will be MISSING, so the criterion-11 LogType counts for " +
                           "this run are UNKNOWN rather than zero.")
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            $server.WaitForExit(10000) | Out-Null
        }
    }

    # ------------------------------------------------- what the server actually threw
    #
    # WHY EVERY TYPE, NOT THE ONE BEING HUNTED. Orphan-closure O6 graded "a lane-A drill with
    # ZERO throws at ANY site" MET on a run carrying 72 ArgumentExceptions, because the
    # measurement behind that sentence counted NullReferenceException and nothing else. The
    # gate's wording excluded nothing; the measurement excluded almost everything, and X-59 and
    # X-60 both survived it (green-that-proves-nothing.md). So this groups by TYPE and prints
    # every group -- a third kind cannot pass by not being the kind anyone was looking for.
    #
    # The two named below are printed even at zero. An absent line reads as "not measured";
    # "ArgumentException 0" reads as measured and clean, and those must not look alike.
    #
    # AND WHY THE TEXT GREP IS NO LONGER THE WHOLE ANSWER. Unity's -logFile writes a
    # Debug.LogError message with the same shape as a Debug.Log one -- no level marker anywhere
    # on the line -- so a pattern anchored on an exception TYPE NAME can only ever find entries
    # whose text happens to begin with one. "[net] match reset left state behind" is a
    # LogType.Error and was invisible to it, and criterion 11 grades Error AND Exception.
    # HeadlessLoadBootstrap subscribes to Application.logMessageReceived, which is the only
    # place the LogType still exists; its summary carries the authoritative per-type counts and
    # they are printed below beside the text tally. When the two disagree, the sink is right.
    $exceptionsPath = Join-Path $outDir "$Tag-exceptions.json"
    $tally = [ordered]@{}

    if (Test-Path $serverLog) {
        Select-String -Path $serverLog -Pattern '^(?<type>[A-Za-z][A-Za-z0-9_.]*Exception)\b' |
            ForEach-Object { $_.Matches[0].Groups['type'].Value } |
            Group-Object |
            Sort-Object -Property @{ Expression = 'Count'; Descending = $true }, Name |
            ForEach-Object { $tally[$_.Name] = $_.Count }
    }
    else {
        Write-Warning "[lane-a] no server log at $serverLog -- the tally below is UNKNOWN, not zero."
    }

    foreach ($named in @("ArgumentException", "NullReferenceException")) {
        if (-not $tally.Contains($named)) { $tally[$named] = 0 }
    }

    $total = ($tally.Values | Measure-Object -Sum).Sum
    Write-Host "[lane-a] $Tag exceptions in the server log, by type ($total total):"
    foreach ($type in $tally.Keys) {
        $colour = if ($tally[$type] -gt 0) { "Yellow" } else { "Green" }
        Write-Host ("  {0,-40} {1}" -f $type, $tally[$type]) -ForegroundColor $colour
    }

    # The authoritative half: counted by LogType inside the process, so nothing depends on
    # how a message happens to be spelled.
    $logByType = $null
    if (Test-Path $summary) {
        try { $logByType = (Get-Content -Raw $summary | ConvertFrom-Json).logByType }
        catch { Write-Warning "[lane-a] could not read logByType from $summary : $_" }
    }

    if ($null -eq $logByType) {
        Write-Warning ("[lane-a] no logByType in $summary -- the per-type counts below are " +
                       "UNKNOWN, not zero. The sink writes it at shutdown; a server that was " +
                       "force-killed never got there.")
    }
    else {
        $gradedErrors = [int]$logByType.Error + [int]$logByType.Exception + [int]$logByType.Assert
        $colour = if ($gradedErrors -gt 0) { "Red" } else { "Green" }
        Write-Host "[lane-a] $Tag log entries by LogType (the criterion-11 grade):"
        Write-Host ("  Error {0}  Exception {1}  Assert {2}  | Warning {3}  Log {4}" -f
                    $logByType.Error, $logByType.Exception, $logByType.Assert,
                    $logByType.Warning, $logByType.Log) -ForegroundColor $colour
        if ($gradedErrors -gt 0 -and (Test-Path $errors)) {
            Write-Host "[lane-a] $Tag bodies -> $errors"
        }
    }

    [pscustomobject]@{
        tag         = $Tag
        seconds     = $Seconds
        behavior    = $Behavior
        serverLog   = $serverLog
        total       = $total
        byType      = $tally
        logByType   = $logByType
        errorsPath  = $errors
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $exceptionsPath -Encoding utf8

    Write-Host "[lane-a] $Tag done, harness exit $harnessExit -> $report"
    Write-Host "[lane-a] $Tag exception tally -> $exceptionsPath"
    exit $harnessExit
}
finally {
    Pop-Location
}
