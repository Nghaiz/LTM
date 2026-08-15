<#
.SYNOPSIS
    Runs the phase-03 load-test matrix and writes one JSON report per scenario.

.DESCRIPTION
    The six scenarios in plans/dev-d-master-server/phases/phase-03-operations.md section 2,
    task 4, in one command.

    Run it from a machine OTHER than the VPS. Running it on the box measures loopback, which
    is not the number anybody cares about — the point of the VPS row in the comparison table
    is that it includes the real network path.

    IMPORTANT: the master's per-IP connection limit defaults to 5, so every scenario past the
    fifth bot is refused when they all come from one machine. Set
    IRONFRONT_MAX_CONNECTIONS_PER_IP on the SERVER (not here) before running:

        IRONFRONT_MAX_CONNECTIONS_PER_IP=64

    That limit is correct for production and wrong for a test rig; raising the default
    instead would let a benchmark's convenience set the number that protects the server.

.PARAMETER Master
    host:port of the master server.

.PARAMETER Metrics
    host:port of the metrics endpoint, so the report carries server-side RAM and connection
    counts. Reachable over an SSH tunnel: ssh -L 27001:127.0.0.1:27001 user@vps

.PARAMETER Quick
    Runs each scenario for 30 seconds instead of the full durations. For proving the harness
    works before committing to 85 minutes.
#>
[CmdletBinding()]
param(
    [string] $Master = "127.0.0.1:27000",
    [string] $Metrics = "",
    [string] $OutputDirectory = "./loadtest-results",
    [switch] $Quick,
    [switch] $Tls,
    [string] $Pin = ""
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

# Durations straight from the phase document's table.
$scenarios = @(
    @{ Label = "16-random-walk";  Clients = 16;  Behavior = "random-walk";       Duration = 1800 }
    @{ Label = "16-spin";         Clients = 16;  Behavior = "spin";              Duration = 900  }
    @{ Label = "16-join-leave";   Clients = 16;  Behavior = "join-leave";        Duration = 900  }
    @{ Label = "16-abrupt";       Clients = 16;  Behavior = "disconnect-abrupt"; Duration = 600  }
    @{ Label = "32-breaking";     Clients = 32;  Behavior = "random-walk";       Duration = 600  }
    @{ Label = "100-connections"; Clients = 100; Behavior = "connect-storm";     Duration = 300  }
)

$totalSeconds = ($scenarios | Measure-Object -Property Duration -Sum).Sum
if ($Quick) { $totalSeconds = $scenarios.Count * 30 }

Write-Host "Load-test suite against $Master" -ForegroundColor Cyan
Write-Host "  scenarios : $($scenarios.Count)"
Write-Host "  wall clock: ~$([math]::Round($totalSeconds / 60, 1)) minutes"
Write-Host "  output    : $OutputDirectory"
Write-Host ""

$failed = @()

foreach ($scenario in $scenarios) {
    $duration = if ($Quick) { 30 } else { $scenario.Duration }
    $reportPath = Join-Path $OutputDirectory "$($scenario.Label).json"

    $arguments = @(
        "--master",   $Master
        "--clients",  $scenario.Clients
        "--duration", $duration
        "--behavior", $scenario.Behavior
        "--label",    $scenario.Label
        "--report",   $reportPath
    )
    if ($Metrics) { $arguments += @("--metrics", $Metrics) }
    if ($Tls)     { $arguments += "--tls" }
    if ($Pin)     { $arguments += @("--pin", $Pin) }

    Write-Host "[$($scenario.Label)] $($scenario.Clients) clients, $($scenario.Behavior), ${duration}s" -ForegroundColor Yellow
    $started = Get-Date

    dotnet run --project Ironfront.Tools.LoadTest -c Release --no-build -- @arguments | Out-Null
    $exitCode = $LASTEXITCODE

    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    if (Test-Path $reportPath) {
        $report = Get-Content $reportPath -Raw | ConvertFrom-Json
        Write-Host ("  ops {0}  p50 {1}ms  p99 {2}ms  failures {3}  peakRSS {4}MB  ({5}s)" -f `
            $report.operations,
            $report.operationLatencyMs.p50,
            $report.operationLatencyMs.p99,
            $report.failures,
            $(if ($report.server) { $report.server.peakWorkingSetMb } else { "n/a" }),
            $elapsed)
    }

    # A non-zero exit means the harness saw failures. Recorded and carried on: one bad
    # scenario should not cost the other five, and the comparison table wants every row.
    if ($exitCode -ne 0) { $failed += $scenario.Label }
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "All scenarios completed with zero failures." -ForegroundColor Green
    exit 0
}

Write-Host "Scenarios reporting failures: $($failed -join ', ')" -ForegroundColor Red
Write-Host "Read the 'errors' array in the matching JSON before treating this as a defect —" -ForegroundColor DarkGray
Write-Host "a 32-client run is SUPPOSED to find the breaking point." -ForegroundColor DarkGray
exit 1
