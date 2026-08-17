# tools/setup-unity-dev.ps1 — one-shot, per-developer Unity + git setup for this clone.
# OWNER: Dev D (tools/ and CI, plans/00-shared/conventions.md section 7).
#
# WHY THIS SCRIPT EXISTS
#
# .gitattributes routes *.unity, *.prefab and *.asset through the unityyamlmerge driver, but a
# merge driver is a per-CLONE git setting that lives in .git/config -- which git deliberately does
# NOT distribute, because a driver line names an absolute path to an executable on your machine.
# So .gitattributes declares the intent and every developer has to complete it by hand, and its
# own header says so:
#
#   "Unity rewrites these YAML assets on every Editor open; without a merge driver the conflicts
#    are thousand-line YAML blobs that cannot be resolved by hand."
#
# A step that every developer must do by hand, from a comment, is a step that silently does not
# happen -- and it fails OPEN: git falls back to the default text merge and you only discover it
# during the conflict you needed it for. Dustbowl.unity is 9.2 MB of YAML; that is not a conflict
# anybody resolves by reading it.
#
# The script is safe to run BEFORE Unity is installed: it reports exactly what is missing and
# exits non-zero, changing nothing. Run it again after installing. It is idempotent -- running it
# twice is the same as running it once.
#
# Usage:  pwsh tools/setup-unity-dev.ps1 [-PersistUnityPath]
#
#   -PersistUnityPath   also store UNITY_PATH as a persistent user environment variable, so
#                       tools/ci.ps1 step 4 (the Unity batch-mode compile check) runs in every
#                       future shell. Off by default: it mutates your machine, not this repo.

[CmdletBinding()]
param(
    [switch]$PersistUnityPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$problems = @()
$actions  = @()

function Write-Section { param([string]$Name) Write-Host ""; Write-Host "=== $Name ===" -ForegroundColor Cyan }
function Write-Ok      { param([string]$M) Write-Host "  OK   $M" -ForegroundColor Green }
function Write-Bad     { param([string]$M) Write-Host "  FAIL $M" -ForegroundColor Red;    $script:problems += $M }
function Write-Did     { param([string]$M) Write-Host "  SET  $M" -ForegroundColor Yellow; $script:actions  += $M }

try {
    # -------------------------------------------------------------------------------------
    # 1. Which Unity does THIS project require? Read it, never assume it.
    # -------------------------------------------------------------------------------------
    Write-Section "1. Required Unity version"

    $versionFile = Join-Path $repoRoot "Ironfront_Reborn/ProjectSettings/ProjectVersion.txt"
    if (-not (Test-Path $versionFile)) {
        Write-Bad "ProjectVersion.txt not found at Ironfront_Reborn/ProjectSettings/ -- wrong repo root?"
        throw "cannot continue without the required Unity version"
    }

    $required = (Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    Write-Ok "project requires Unity $required"

    # -------------------------------------------------------------------------------------
    # 2. Locate that exact Editor. An almost-right version is worse than none: opening the
    #    project with a newer Editor silently UPGRADES it and rewrites assets project-wide.
    # -------------------------------------------------------------------------------------
    Write-Section "2. Unity Editor"

    $candidates = @()
    if ($env:UNITY_PATH) { $candidates += $env:UNITY_PATH }
    $candidates += "C:/Program Files/Unity/Hub/Editor/$required/Editor/Unity.exe"
    $candidates += "C:/Program Files/Unity/Editor/Unity.exe"
    $candidates += "/Applications/Unity/Hub/Editor/$required/Unity.app/Contents/MacOS/Unity"

    # Unity Hub can be told to install elsewhere; it records that choice here.
    $hubSecondary = Join-Path $env:APPDATA "UnityHub/secondaryInstallPath.json"
    if (Test-Path $hubSecondary) {
        $alt = (Get-Content $hubSecondary -Raw).Trim().Trim('"')
        if ($alt) { $candidates += "$alt/$required/Editor/Unity.exe" }
    }

    $unityExe = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $unityExe) {
        Write-Bad "Unity $required not found. Looked in:"
        foreach ($c in $candidates) { Write-Host "         $c" -ForegroundColor DarkGray }
        Write-Host ""
        Write-Host "  Install it, then run this script again:" -ForegroundColor Yellow
        Write-Host "    1. Unity Hub          https://unity.com/download" -ForegroundColor Yellow
        Write-Host "    2. Editor $required   unityhub://$required" -ForegroundColor Yellow
        Write-Host "  Installing the Editor touches nothing in this repo. Do NOT open the project" -ForegroundColor Yellow
        Write-Host "  until this script reports ALL GREEN -- see docs/unity-setup.md." -ForegroundColor Yellow
    }
    else {
        Write-Ok "found at $unityExe"
        $env:UNITY_PATH = $unityExe

        if ($PersistUnityPath) {
            [Environment]::SetEnvironmentVariable("UNITY_PATH", $unityExe, "User")
            Write-Did "UNITY_PATH persisted for your user (new shells pick it up)"
        }
        else {
            Write-Host "       tools/ci.ps1 step 4 needs UNITY_PATH. Re-run with -PersistUnityPath," -ForegroundColor DarkGray
            Write-Host "       or set it yourself: `$env:UNITY_PATH = '$unityExe'" -ForegroundColor DarkGray
        }
    }

    # -------------------------------------------------------------------------------------
    # 3. The merge driver. This is the whole point of the script.
    # -------------------------------------------------------------------------------------
    Write-Section "3. UnityYAMLMerge driver"

    if (-not $unityExe) {
        Write-Bad "skipped -- the driver line must name UnityYAMLMerge.exe by absolute path"
    }
    else {
        # Windows: <Editor>/Data/Tools/UnityYAMLMerge.exe, alongside Unity.exe's own folder.
        # macOS:   <Unity.app>/Contents/Tools/UnityYAMLMerge
        $editorDir = Split-Path -Parent $unityExe
        $mergeExe = @(
            (Join-Path $editorDir "Data/Tools/UnityYAMLMerge.exe"),
            (Join-Path $editorDir "../Tools/UnityYAMLMerge")
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if (-not $mergeExe) {
            Write-Bad "UnityYAMLMerge not found next to $unityExe -- is the Editor install complete?"
        }
        else {
            $mergeExe = (Resolve-Path $mergeExe).Path -replace '\\', '/'
            $driver = "`"$mergeExe`" merge -p %O %A %B %A"

            $currentDriver = (git config --get merge.unityyamlmerge.driver 2>$null)
            if ($currentDriver -eq $driver) {
                Write-Ok "already configured, unchanged"
            }
            else {
                git config merge.unityyamlmerge.name "Unity SmartMerge"      | Out-Null
                git config merge.unityyamlmerge.driver $driver               | Out-Null
                Write-Did "merge.unityyamlmerge -> $mergeExe"
            }
        }
    }

    # -------------------------------------------------------------------------------------
    # 4. The repo-side half of the same contract. A driver with nothing routed to it is a
    #    green that proves nothing, so assert BOTH halves rather than trusting either.
    # -------------------------------------------------------------------------------------
    Write-Section "4. .gitattributes routing"

    $ga = Join-Path $repoRoot ".gitattributes"
    if (-not (Test-Path $ga)) {
        Write-Bad ".gitattributes is missing -- nothing routes to the merge driver"
    }
    else {
        $gaText = Get-Content $ga -Raw
        foreach ($ext in @("*.unity", "*.prefab", "*.asset")) {
            $pattern = [regex]::Escape($ext) + '\s+merge=unityyamlmerge'
            if ($gaText -match $pattern) { Write-Ok "$ext routed to unityyamlmerge" }
            else { Write-Bad "$ext is NOT routed to unityyamlmerge in .gitattributes" }
        }
    }

    # -------------------------------------------------------------------------------------
    # 5. Machine-local Unity output must stay out of git. Library/ alone is tens of thousands
    #    of files; if it is not ignored, the first Editor open buries every real change.
    # -------------------------------------------------------------------------------------
    Write-Section "5. Unity output is ignored"

    foreach ($dir in @("Library", "Temp", "obj", "Logs", "UserSettings")) {
        $probe = "Ironfront_Reborn/$dir/probe"
        git check-ignore -q -- $probe 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Ok "Ironfront_Reborn/$dir/ ignored" }
        else { Write-Bad "Ironfront_Reborn/$dir/ is NOT ignored -- opening Unity will flood git status" }
    }

    # -------------------------------------------------------------------------------------
    # 6. .meta consistency, delegated to the gate CI runs. One implementation, two callers.
    # -------------------------------------------------------------------------------------
    Write-Section "6. .meta consistency"

    & "$PSScriptRoot/check-unity-meta.ps1" *> $null
    if ($LASTEXITCODE -eq 0) { Write-Ok "every asset has a .meta and every .meta has its asset" }
    else { Write-Bad "run 'pwsh tools/check-unity-meta.ps1' for the list" }
}
finally {
    Pop-Location
}

Write-Section "Result"

if ($actions.Count -gt 0) {
    Write-Host "Changed on this machine:" -ForegroundColor Yellow
    foreach ($a in $actions) { Write-Host "  - $a" -ForegroundColor Yellow }
    Write-Host ""
}

if ($problems.Count -gt 0) {
    Write-Host "NOT READY -- $($problems.Count) problem(s):" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Do NOT open the project in Unity yet. Fix the above and re-run." -ForegroundColor Red
    exit 1
}

Write-Host "ALL GREEN -- safe to open Ironfront_Reborn in Unity." -ForegroundColor Green
Write-Host "Read docs/unity-setup.md section 'The first open' before you do: the first import" -ForegroundColor Green
Write-Host "rewrites files, and which of those to commit is not obvious." -ForegroundColor Green
exit 0
