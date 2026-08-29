# tools/run-integration.ps1 — the 2-process integration test.
# section 4 already names this script as the integration-day gate while the file did not
# exist; the replication track owns the scenarios that go in it.
#
# conventions.md section 4:
#     | 2-process integration | All 4 | tools/run-integration.ps1 script | Run every integration day |
#
# WHAT IT DOES: starts the master server as one OS process, runs the load-test client as
# another, and asserts they can actually talk over a real socket. Unit tests cannot catch the
# failures this catches — a wrong byte order, a listener bound to 127.0.0.1 when the client
# dials the LAN address, a framing bug that only appears when the OS splits a TCP write.
#
# HONESTY RULE (conventions.md section 6): while Ironfront.MasterServer and
# Ironfront.Tools.LoadTest are still empty skeletons, this script reports SKIP and says
# exactly why. It does NOT print a green "integration passed" for a scenario it never ran.
# Delete the skip guard when the two projects have real entry points.
#
# Usage:
#     pwsh tools/run-integration.ps1
#     pwsh tools/run-integration.ps1 -Configuration Debug -TimeoutSec 90 -KeepLogs

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    # Master server listen port for the test run. Deliberately not the production default, so
    # a stray real server on the dev machine cannot make this pass by accident.
    [int]$Port = 45510,

    # Budget for the server to accept connections, and for the whole client run.
    [int]$TimeoutSec = 60,

    # Keep the process logs even when the run succeeds.
    [switch]$KeepLogs
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir   = Join-Path $repoRoot "artifacts/integration"
$serverLog = Join-Path $logDir "master-server.log"
$serverErr = Join-Path $logDir "master-server.err.log"
$clientLog = Join-Path $logDir "load-test.log"
$clientErr = Join-Path $logDir "load-test.err.log"

$serverProject = Join-Path $repoRoot "Ironfront.MasterServer/Ironfront.MasterServer.csproj"
$clientProject = Join-Path $repoRoot "Ironfront.Tools.LoadTest/Ironfront.Tools.LoadTest.csproj"

# ---------------------------------------------------------------------------------------
# Readiness guard — skip loudly rather than fail confusingly
#
# "Does the project have .cs files?" is NOT the right question: both projects already ship a
# Program.cs stub that prints "not yet implemented" and returns 0. Running against those
# produces a real but useless failure ("server exited before opening the port"), which reads
# like a regression and is not one.
#
# The question that actually distinguishes a stub from an implementation is whether the code
# touches a socket at all. When the master-server track writes the listener, this guard opens by itself — there
# is no flag to remember to flip.
# ---------------------------------------------------------------------------------------
function Test-HasNetworking {
    param([string]$ProjectDir, [string]$Pattern)

    if (-not (Test-Path $ProjectDir)) { return $false }

    $sources = Get-ChildItem -Path $ProjectDir -Filter *.cs -Recurse -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

    if (($sources | Measure-Object).Count -eq 0) { return $false }

    $hit = $sources | Select-String -Pattern $Pattern -List | Select-Object -First 1
    return $null -ne $hit
}

# A listener binds and accepts; a client connects. Different verbs, different patterns.
$serverReady = Test-HasNetworking (Join-Path $repoRoot "Ironfront.MasterServer")  'TcpListener|\.Bind\(|\.Listen\('
$clientReady = Test-HasNetworking (Join-Path $repoRoot "Ironfront.Tools.LoadTest") 'TcpClient|UdpClient|\.Connect\(|new Socket\('

if (-not $serverReady -or -not $clientReady) {
    Write-Host ""
    Write-Host "=== INTEGRATION TEST SKIPPED ===" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Not run, because there is nothing to integrate yet:" -ForegroundColor Yellow
    if (-not $serverReady) {
        Write-Host "  - Ironfront.MasterServer opens no socket — still the Program.cs stub." -ForegroundColor Yellow
        Write-Host "    the master server (spec deleted 2026-08-29; see plans/plan.md)" -ForegroundColor Yellow
    }
    if (-not $clientReady) {
        Write-Host "  - Ironfront.Tools.LoadTest opens no socket — still the Program.cs stub." -ForegroundColor Yellow
        Write-Host "    the master-server track, from M3 onward" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "This is a SKIP, not a PASS. Nothing was verified." -ForegroundColor Yellow
    Write-Host "It becomes a real run automatically once both processes speak to a socket."
    Write-Host ""
    exit 0
}

# ---------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------
function Wait-ForPort {
    param([int]$PortNumber, [int]$Seconds, [System.Diagnostics.Process]$ServerProcess)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        # If the server already died, waiting the full timeout only delays the real message.
        if ($ServerProcess.HasExited) {
            throw "Master server exited with code $($ServerProcess.ExitCode) before opening port $PortNumber."
        }

        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $async = $client.BeginConnect("127.0.0.1", $PortNumber, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne(500) -and $client.Connected) {
                $client.EndConnect($async)
                return $true
            }
        }
        catch {
            # Connection refused while the listener is still starting — expected, keep polling.
        }
        finally {
            $client.Close()
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Show-Log {
    param([string]$Title, [string]$Path)

    Write-Host ""
    Write-Host "--- $Title ---" -ForegroundColor Cyan
    if (Test-Path $Path) {
        $content = Get-Content $Path -Tail 80
        if ($content) { $content | ForEach-Object { Write-Host "  $_" } }
        else          { Write-Host "  (empty)" }
    }
    else {
        Write-Host "  (no log file)"
    }
}

# ---------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$server = $null
$failed = $false
$started = Get-Date

Push-Location $repoRoot
try {
    Write-Host "=== 1. Build both processes ($Configuration) ===" -ForegroundColor Cyan
    dotnet build $serverProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: Ironfront.MasterServer" }
    dotnet build $clientProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: Ironfront.Tools.LoadTest" }

    Write-Host ""
    Write-Host "=== 2. Start the master server on port $Port ===" -ForegroundColor Cyan
    $server = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $serverProject, "-c", $Configuration, "--no-build", "--", "--port", "$Port") `
        -RedirectStandardOutput $serverLog `
        -RedirectStandardError  $serverErr `
        -PassThru -NoNewWindow

    if (-not (Wait-ForPort -PortNumber $Port -Seconds $TimeoutSec -ServerProcess $server)) {
        throw "Master server did not accept a TCP connection on port $Port within $TimeoutSec s."
    }
    Write-Host "Server is accepting connections (pid $($server.Id))." -ForegroundColor Green

    Write-Host ""
    Write-Host "=== 3. Run the load-test client against it ===" -ForegroundColor Cyan
    # SCENARIOS GO HERE — the replication track owns this list (conventions.md section 7).
    # The first one should be the cheapest meaningful assertion: connect, authenticate,
    # receive one lobby message, disconnect cleanly. Add scenarios one at a time; an
    # integration script that tests eight things at once tells you nothing when it goes red.
    $client = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $clientProject, "-c", $Configuration, "--no-build", "--", "--host", "127.0.0.1", "--port", "$Port", "--bots", "2", "--seconds", "10") `
        -RedirectStandardOutput $clientLog `
        -RedirectStandardError  $clientErr `
        -PassThru -NoNewWindow

    if (-not $client.WaitForExit($TimeoutSec * 1000)) {
        $client.Kill($true)
        throw "Load-test client did not finish within $TimeoutSec s; killed."
    }

    if ($client.ExitCode -ne 0) {
        throw "Load-test client exited with code $($client.ExitCode)."
    }

    Write-Host "Client finished cleanly." -ForegroundColor Green
}
catch {
    $failed = $true
    Write-Host ""
    Write-Host "INTEGRATION FAILED: $_" -ForegroundColor Red
}
finally {
    # Teardown runs on every path. A leaked server process holds the port and makes the NEXT
    # run fail with a misleading "address already in use".
    if ($server -and -not $server.HasExited) {
        Write-Host ""
        Write-Host "Stopping master server (pid $($server.Id))..."
        try   { $server.Kill($true) | Out-Null; $server.WaitForExit(5000) | Out-Null }
        catch { Write-Warning "Could not stop the server process: $_" }
    }
    Pop-Location
}

if ($failed) {
    Show-Log "master-server stdout" $serverLog
    Show-Log "master-server stderr" $serverErr
    Show-Log "load-test stdout"     $clientLog
    Show-Log "load-test stderr"     $clientErr
    Write-Host ""
    Write-Host "Full logs: $logDir" -ForegroundColor Red
    exit 1
}

if (-not $KeepLogs) {
    Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue
}

$elapsed = (Get-Date) - $started
Write-Host ""
Write-Host ("INTEGRATION PASSED in {0:mm\:ss}" -f $elapsed) -ForegroundColor Green
exit 0
