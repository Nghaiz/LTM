# tools/ci.ps1 — the local mirror of the GitHub Actions pipeline.
# OWNER: Dev D (plans/00-shared/conventions.md section 7).
#
# conventions.md section 5 requires this to finish in under 5 minutes and to cover:
#   1. dotnet build across every .NET project, warnings as errors
#   2. dotnet test across the board, zero failures
#   3. ProtocolConstants.cs still matches the table in protocol-spec.md
#   4. Unity batch-mode compile check (only when Unity is present)
#
# Run it before pushing. CI is the referee that tells you whether you broke someone
# else's build without having to ask them.
#
# Usage:  pwsh tools/ci.ps1 [-Configuration Release] [-SkipUnity]

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipUnity
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$started = Get-Date
$failures = @()

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    try {
        & $Body
        if ($LASTEXITCODE -ne 0) { throw "$Name exited with code $LASTEXITCODE" }
        Write-Host "PASS: $Name" -ForegroundColor Green
    }
    catch {
        Write-Host "FAIL: $Name — $_" -ForegroundColor Red
        $script:failures += $Name
    }
}

try {
    Invoke-Step "1. Build" {
        dotnet build "$repoRoot/Ironfront.sln" -c $Configuration --nologo
    }

    Invoke-Step "2. Test" {
        dotnet test "$repoRoot/Ironfront.sln" -c $Configuration --nologo `
            --logger "console;verbosity=normal"
    }

    Invoke-Step "3. Protocol constants match the spec" {
        dotnet run --project "$repoRoot/tools/SpecChecker" -c $Configuration --nologo -- $repoRoot
    }

    # Step 4 is opt-in: only Dev A's machine and a Unity-equipped runner can do this, and
    # the other three must not be blocked by its absence.
    if ($SkipUnity) {
        Write-Host ""
        Write-Host "=== 4. Unity compile check === SKIPPED (-SkipUnity)" -ForegroundColor Yellow
    }
    elseif (-not $env:UNITY_PATH) {
        Write-Host ""
        Write-Host "=== 4. Unity compile check === SKIPPED (UNITY_PATH not set)" -ForegroundColor Yellow
    }
    else {
        Invoke-Step "4. Unity compile check" {
            & $env:UNITY_PATH -batchmode -nographics -quit `
                -projectPath "$repoRoot/Ironfront_Reborn" `
                -logFile "$repoRoot/unity-compile.log"
        }
    }
}
finally {
    Pop-Location
}

$elapsed = (Get-Date) - $started
Write-Host ""
Write-Host ("Elapsed: {0:mm\:ss}" -f $elapsed)

if ($failures.Count -gt 0) {
    Write-Host "CI FAILED — $($failures.Count) step(s): $($failures -join ', ')" -ForegroundColor Red
    exit 1
}

if ($elapsed.TotalMinutes -gt 5) {
    Write-Warning "CI took longer than the 5-minute budget in conventions.md section 5."
}

Write-Host "CI PASSED" -ForegroundColor Green
exit 0
