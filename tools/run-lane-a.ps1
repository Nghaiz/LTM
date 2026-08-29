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
    [int] $ServerWarmupSeconds = 20
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

    Write-Host "[lane-a] $Tag : $Clients client(s) / $Seconds s / sim=$Sim / loadSeed=$LoadSeed simSeed=$SimSeed"

    # --------------------------------------------------------------- the server
    $env:IRONFRONT_LANEB_ROLE             = "server"
    $env:IRONFRONT_GAMESERVER_TRANSPORT   = "udp"
    $env:IRONFRONT_GAMESERVER_UDP_PORT    = "$Port"
    $env:IRONFRONT_SHARED_SECRET          = $Secret
    $env:IRONFRONT_LOAD_JSONL             = $ticks
    $env:IRONFRONT_LOAD_SEED              = "$LoadSeed"

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
        # Always stop the server, including on a harness crash: a leftover player holds UDP
        # 27015 and the NEXT run's clients are refused by a server nobody is looking at.
        if (-not $server.HasExited) {
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

    [pscustomobject]@{
        tag         = $Tag
        seconds     = $Seconds
        behavior    = $Behavior
        serverLog   = $serverLog
        total       = $total
        byType      = $tally
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $exceptionsPath -Encoding utf8

    Write-Host "[lane-a] $Tag done, harness exit $harnessExit -> $report"
    Write-Host "[lane-a] $Tag exception tally -> $exceptionsPath"
    exit $harnessExit
}
finally {
    Pop-Location
}
