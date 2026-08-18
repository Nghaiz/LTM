# tools/check-duplicate-assemblies.ps1 — fail when two managed plugins share an assembly
# name but not an assembly version, AND both are enabled for the Editor.
#
# WHY THIS EXISTS
#   The project ships System.Text.Json twice: 10.0.0.0 under Assets/Plugins (ours, the closure
#   tools/build-libs.ps1 builds for Ironfront.MasterClient) and 8.0.0.0 under
#   Assets/Plugins/NuGet (pulled in by the Unity-MCP plugin's NuGet resolver). When both reach
#   the Editor domain, a type from one is not assignable to the same-named type from the other,
#   and the failure surfaces somewhere unrelated and unhelpful:
#
#       HubException: the converter specified on 'McpServerData.ServerTransport' does not
#       derive from JsonConverter or have a public parameterless constructor
#
#   ...about JsonStringEnumConverter, which self-evidently does derive from JsonConverter. It
#   cost a session to trace from that message back to a duplicate DLL.
#
#   A duplicate at the SAME version is fine — one identity, whichever file wins. It is the
#   version split that breaks type identity, so that is what this fails on.
#
# WHAT WOULD MAKE IT GO RED
#   Restore defineConstraints on Assets/Plugins/NuGet/System.Text.Json.dll.meta, or drop a
#   second differing copy of any managed dependency into Assets/. Both are real regressions
#   this repo has already had once.
#
# Usage:  pwsh tools/check-duplicate-assemblies.ps1
[CmdletBinding()]
param(
    [string] $AssetsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Ironfront_Reborn/Assets')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $AssetsPath)) {
    Write-Host "SKIP: no Assets folder at $AssetsPath"
    exit 0
}

# A plugin reaches the Editor domain unless its importer says otherwise. Unity expresses that
# in two independent ways and BOTH have to be read: the platform block, and defineConstraints
# (a UNITY_EDITOR constraint pulled the v8 copy in even though the platform block excluded it).
function Test-EnabledInEditor([string] $dllPath) {
    $meta = "$dllPath.meta"
    if (-not (Test-Path $meta)) { return $true }
    $text = Get-Content $meta -Raw
    $hasEditorConstraint = $text -match '(?m)^\s*-\s*UNITY_EDITOR\s*$'
    if ($hasEditorConstraint) { return $true }
    if ($text -match '(?m)^\s*Exclude Editor:\s*1\s*$') { return $false }
    if ($text -match '(?ms)^\s{4}Editor:\s*\r?\n\s*enabled:\s*0') { return $false }
    return $true
}

$groups = Get-ChildItem -Path $AssetsPath -Filter *.dll -Recurse -File |
    Group-Object Name |
    Where-Object { $_.Count -gt 1 }

$conflicts = @()
foreach ($group in $groups) {
    $live = foreach ($file in $group.Group) {
        if (-not (Test-EnabledInEditor $file.FullName)) { continue }
        try { $version = [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName).Version }
        catch { continue }   # native or unmanaged DLL — not an assembly-identity problem
        [PSCustomObject]@{ Path = $file.FullName; Version = $version }
    }
    $distinct = @($live | Select-Object -ExpandProperty Version -Unique)
    if ($distinct.Count -gt 1) {
        $conflicts += [PSCustomObject]@{ Name = $group.Name; Copies = $live }
    }
}

if ($conflicts.Count -eq 0) {
    $total = ($groups | Measure-Object).Count
    Write-Host "PASS: no Editor-enabled managed assembly is present at two different versions ($total same-named group(s) checked)."
    exit 0
}

Write-Host "FAIL: the same assembly is loaded at two different versions." -ForegroundColor Red
foreach ($conflict in $conflicts) {
    Write-Host ""
    Write-Host "  $($conflict.Name)" -ForegroundColor Yellow
    foreach ($copy in $conflict.Copies) {
        $relative = $copy.Path.Substring($AssetsPath.Length).TrimStart('\', '/')
        Write-Host ("    {0,-12} Assets/{1}" -f $copy.Version, ($relative -replace '\\', '/'))
    }
}
Write-Host ""
Write-Host "Two identities of one type break every [JsonConverter], [Serializable] and cast that"
Write-Host "crosses the boundary, and the error names something unrelated. Exclude one copy from"
Write-Host "the Editor in its .dll.meta, or align the versions."
exit 1
