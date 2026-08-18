# tools/check-unity-meta.ps1 — every imported asset has a .meta, and every .meta has its asset.
# OWNER: Dev D (tools/ and CI, plans/00-shared/conventions.md section 7).
#
# WHY THIS GATE EXISTS
#
# A .meta file carries the asset's GUID, and every prefab, scene and material reference in the
# project is stored as that GUID rather than as a path. The two failure modes are asymmetric and
# both are silent:
#
#   1. MISSING .meta — the asset is committed without its GUID. Unity generates a fresh one on
#      each machine that imports it, so two developers end up with two different GUIDs for the
#      same file. Whoever commits a reference to it first wins; everyone else gets a broken
#      reference that reads as "the script is missing" with no clue why. This is not theoretical:
#      ServerActorDamageSink.cs, ServerCombatBridge.cs and ServerCombatEvents.cs sat in that state
#      from phase-05 until this gate landed, and phase-v10 section 7 item E2 had already noticed.
#
#   2. ORPHAN .meta — the asset is deleted but its .meta is left behind. Harmless day to day,
#      which is exactly why it accumulates; it also makes the missing-meta scan harder to read.
#      Scenes/Splash.meta was one, for a folder that no longer exists.
#
# Both are mechanical, so they belong in a gate rather than in a reviewer's memory. Files created
# by anyone working WITHOUT the Unity Editor — which on this project is Dev B, C and D — are the
# ones that land without a .meta, and they cannot notice locally because nothing they run opens
# Unity.
#
# Usage:  pwsh tools/check-unity-meta.ps1 [-ProjectPath Ironfront_Reborn]

[CmdletBinding()]
param(
    [string]$ProjectPath = "Ironfront_Reborn"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsRel = "$ProjectPath/Assets"
$assetsAbs = Join-Path $repoRoot $assetsRel

if (-not (Test-Path $assetsAbs)) {
    Write-Host "FAIL: no Assets folder at '$assetsRel' (looked under repo root '$repoRoot')." -ForegroundColor Red
    exit 1
}

# Unity's asset pipeline skips these outright — it neither imports them nor creates a .meta for
# them. Reproducing the rule here is what keeps the gate from reporting false positives on files
# Unity was never going to touch (Assets/Plugins/NuGet/.nuget-installed.json is the live example).
# Reference: any path SEGMENT matching one of these is ignored, not just the leaf.
function Test-UnityIgnoredPath {
    param([string]$RelativePath)

    foreach ($segment in $RelativePath.Split('/')) {
        if ($segment -eq "") { continue }
        if ($segment.StartsWith(".")) { return $true }   # .git, .DS_Store, .nuget-installed.json
        if ($segment.EndsWith("~"))   { return $true }   # backup~
        if ($segment.EndsWith(".tmp")) { return $true }
        if ($segment -ieq "cvs")      { return $true }
    }
    return $false
}

Push-Location $repoRoot
try {
    # The tracked set, not the working tree: the question this gate answers is "what will another
    # developer get when they pull", and untracked local scratch files are not part of that.
    $tracked = @(git ls-files -- "$assetsRel" 2>$null)
}
finally {
    Pop-Location
}

# A gate that passes because it looked at nothing is worse than no gate — it reports green forever
# from the wrong working directory. tools/UnitySyntaxCheck refuses an empty file set for the same
# reason; this one refuses it too rather than inheriting the silence.
if ($tracked.Count -eq 0) {
    Write-Host "FAIL: scanned zero tracked files under '$assetsRel'." -ForegroundColor Red
    Write-Host "      Either the path is wrong or this is not a git checkout. Refusing to report PASS." -ForegroundColor Red
    exit 1
}

# Two sets, because the two questions are different directions of the same relation: "does this
# asset have a .meta" reads the meta set, "does this .meta have an asset" reads the asset set.
$assetSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$metaSet  = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$metaPaths = [System.Collections.Generic.List[string]]::new()
$assetPaths = [System.Collections.Generic.List[string]]::new()

foreach ($path in $tracked) {
    if (Test-UnityIgnoredPath $path) { continue }

    if ($path.EndsWith(".meta", [StringComparison]::OrdinalIgnoreCase)) {
        $metaPaths.Add($path)
        [void]$metaSet.Add($path)
    }
    else {
        $assetPaths.Add($path)
        [void]$assetSet.Add($path)
    }
}

# Tracked-only on purpose: a .meta that exists locally but is not committed is exactly as broken
# as an absent one for everybody else, and this gate answers "what does the next puller get".
$missing = [System.Collections.Generic.List[string]]::new()
foreach ($asset in $assetPaths) {
    if (-not $metaSet.Contains("$asset.meta")) { $missing.Add($asset) }
}

$orphans = [System.Collections.Generic.List[string]]::new()
foreach ($meta in $metaPaths) {
    $asset = $meta.Substring(0, $meta.Length - ".meta".Length)

    if ($assetSet.Contains($asset)) { continue }

    # The asset may be a FOLDER, which git does not track as an entry of its own — so the tracked
    # set cannot answer this and the filesystem has to. Folder .meta files are real assets to
    # Unity and carry GUIDs that materials and prefabs can reference.
    if (Test-Path (Join-Path $repoRoot $asset)) { continue }

    $orphans.Add($meta)
}

Write-Host ""
Write-Host "=== Unity .meta consistency ===" -ForegroundColor Cyan
Write-Host "Scanned $($assetPaths.Count) assets and $($metaPaths.Count) .meta files under $assetsRel."

$failed = $false

if ($missing.Count -gt 0) {
    $failed = $true
    Write-Host ""
    Write-Host "MISSING .meta ($($missing.Count)) — Unity will generate a DIFFERENT GUID on every machine:" -ForegroundColor Red
    foreach ($m in $missing) { Write-Host "  $m" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  Fix: open the project in Unity once, let it import, then commit the generated" -ForegroundColor Yellow
    Write-Host "       .meta files ALONGSIDE the asset. Never commit an asset without its .meta." -ForegroundColor Yellow
}

if ($orphans.Count -gt 0) {
    $failed = $true
    Write-Host ""
    Write-Host "ORPHAN .meta ($($orphans.Count)) — the asset they describe no longer exists:" -ForegroundColor Red
    foreach ($o in $orphans) { Write-Host "  $o" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  Fix: git rm the .meta. Check first that nothing references its guid --" -ForegroundColor Yellow
    Write-Host "       git grep -l <guid> -- Ironfront_Reborn/ should return only the .meta itself." -ForegroundColor Yellow
}

if ($failed) {
    Write-Host ""
    Write-Host "FAIL: .meta consistency" -ForegroundColor Red
    exit 1
}

Write-Host "PASS: every asset has a .meta and every .meta has its asset." -ForegroundColor Green
exit 0
