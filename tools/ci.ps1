# tools/ci.ps1 — the local mirror of the GitHub Actions pipeline.
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
    [switch]$SkipUnity,

    # Also run tools/run-integration.ps1 (2-process test, conventions.md section 4). Opt-in
    # because it starts real processes and binds a port, which the 5-minute pre-push budget
    # does not have room for. Run it on integration day.
    [switch]$Integration
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

    # Mirrors the "Check Unity .meta files are consistent" step in ci.yml. Costs well under a
    # second and catches the one class of breakage that only shows up on somebody ELSE's machine:
    # an asset committed without its GUID. Runs with or without Unity installed — it reads git,
    # not the Editor — so B, C and D get the same answer A does.
    Invoke-Step "3b. Unity .meta consistency" {
        & "$PSScriptRoot/check-unity-meta.ps1"
    }

    # ADVISORY — mirrors the `style` job in .github/workflows/ci.yml, which is
    # continue-on-error. Deliberately NOT routed through Invoke-Step: a formatting nit must
    # not add to $failures and make this script exit 1, or people will stop running it.
    #
    # `style` + `analyzers` only, never `whitespace`: this codebase aligns enum members and
    # constant tables into columns on purpose, and the whitespace formatter wants to collapse
    # all of them. Same command set as the CI job, so local and CI agree.
    Write-Host ""
    Write-Host "=== 3b. Style, analyzers and commit-scope (advisory) ===" -ForegroundColor Cyan
    dotnet format style "$repoRoot/Ironfront.sln" --verify-no-changes --severity warn --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Naming/style differs from .editorconfig (conventions.md section 3.1)."
    }
    else {
        Write-Host "PASS: style" -ForegroundColor Green
    }

    dotnet format analyzers "$repoRoot/Ironfront.sln" --verify-no-changes --severity warn --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Analyzer findings above. Fix with: dotnet format analyzers Ironfront.sln"
    }
    else {
        Write-Host "PASS: analyzers" -ForegroundColor Green
    }

    & "$PSScriptRoot/check-commit-scope.ps1" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Some commit subjects do not match conventions.md section 1.2 (advisory)."
    }

    # Step 4 is opt-in: only the client track's machine and a Unity-equipped runner can do this, and
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
        # Start-Process -Wait, NOT the call operator. Unity.exe is a GUI-subsystem binary, so
        # PowerShell does not wait for it and does not set $LASTEXITCODE from it -- the call
        # operator returned instantly, Invoke-Step read the PREVIOUS command's exit code, and
        # this step printed PASS on every run it has ever had. It went green on a real
        # "Aborting batchmode due to failure: Scripts have compiler errors" on 2026-08-18,
        # which is when it was noticed. A gate that cannot go red is worse than no gate.
        Invoke-Step "4. Unity compile check" {
            $unityLog = "$repoRoot/unity-compile.log"
            $unity = Start-Process -FilePath $env:UNITY_PATH -Wait -PassThru -NoNewWindow `
                -ArgumentList @(
                    '-batchmode', '-nographics', '-quit',
                    '-projectPath', "$repoRoot/Ironfront_Reborn",
                    '-logFile', $unityLog)

            if ($unity.ExitCode -ne 0) {
                # The exit code alone says nothing about WHICH script failed, and the log is
                # thousands of lines. Surface the compiler errors themselves.
                if (Test-Path $unityLog) {
                    Select-String -Path $unityLog -Pattern 'error CS' `
                        | Select-Object -ExpandProperty Line -Unique | Out-Host
                }
                throw "Unity exited with code $($unity.ExitCode) — see $unityLog"
            }

            $global:LASTEXITCODE = 0
        }
    }

    # Step 5 is opt-in for the same reason step 4 is: it costs more than the 5-minute
    # pre-push budget allows. It is a HARD step when requested — an integration failure on
    # integration day is exactly the thing this script exists to catch.
    if ($Integration) {
        Invoke-Step "5. 2-process integration" {
            & "$PSScriptRoot/run-integration.ps1" -Configuration $Configuration
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
