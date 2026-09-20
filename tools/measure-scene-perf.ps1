# tools/measure-scene-perf.ps1 - draw-call / batch / frame-time census for Dustbowl and Island.
#
# WHY THIS DRIVES A LIVE EDITOR INSTEAD OF BATCHMODE
#
# The numbers only exist in Play Mode: Unity builds its static batches when a scene LOADS, so an
# edit-mode scene has none to count no matter how it is rendered. Batchmode Play Mode is not
# dependable here, and batchmode Unity runs one at a time anyway - so this asks the Editor that
# is already open, over the MCP bridge tools/mcp-call.py speaks.
#
# The Editor must be running. If it is not, this fails saying so rather than pretending.
#
# WHAT THE OUTPUT IS WORTH
#
#   staticBatchedRenderers   exact, whole-scene, frustum-free. Renderer.isPartOfStaticBatch is
#                            set by the engine when it actually welds a renderer into a combined
#                            mesh, so this is the direct answer to "did the flags do anything".
#   batches / drawCalls      exact for the four pinned poses, summed. Comparable between runs of
#   / setPass / triangles    the same scene because the poses are derived from the scene's own
#                            renderer bounds and the probe owns its camera.
#   frameMs*                 Editor Play Mode, no build, no netcode load. Indicative. The real
#                            frame time comes from a playtest of the built player.
#
# Usage:
#   pwsh tools/measure-scene-perf.ps1 -Label before
#   pwsh tools/measure-scene-perf.ps1 -Label after
#   pwsh tools/measure-scene-perf.ps1 -Compare before,after

[CmdletBinding(DefaultParameterSetName = 'Measure')]
param(
    [Parameter(ParameterSetName = 'Measure', Mandatory = $true)]
    [string]$Label,

    [Parameter(ParameterSetName = 'Measure')]
    [int]$TimeoutSec = 900,

    [Parameter(ParameterSetName = 'Compare', Mandatory = $true)]
    [string[]]$Compare
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repo 'tools/recovered'

function Get-PerfPath([string]$lbl) { Join-Path $outDir "scene-perf.$lbl.json" }

function Show-Run([string]$lbl) {
    $path = Get-PerfPath $lbl
    if (-not (Test-Path $path)) { throw "no measurement at $path" }
    $doc = Get-Content $path -Raw | ConvertFrom-Json
    Write-Host ""
    Write-Host "== $lbl ($($doc.measuredAtUtc), Unity $($doc.unityVersion))" -ForegroundColor Cyan
    $doc.scenes | ForEach-Object {
        '{0,-9} static-batched {1,5}/{2,-5} batches {3,-7} drawCalls {4,-7} setPass {5,-6} tris {6,-9} frame {7,6:F2} ms mean / {8,6:F2} p99' -f `
            $_.scene, $_.staticBatchedRenderers, $_.renderers, $_.batches, $_.drawCalls, $_.setPassCalls, $_.triangles, $_.frameMsMean, $_.frameMsP99
    }
    return $doc
}

if ($PSCmdlet.ParameterSetName -eq 'Compare') {
    if ($Compare.Count -ne 2) { throw "-Compare takes exactly two labels, e.g. -Compare before,after" }
    $a = Show-Run $Compare[0]
    $b = Show-Run $Compare[1]

    Write-Host ""
    Write-Host "== delta: $($Compare[0]) -> $($Compare[1])" -ForegroundColor Cyan
    foreach ($sa in $a.scenes) {
        $sb = $b.scenes | Where-Object { $_.scene -eq $sa.scene }
        if (-not $sb) { Write-Warning "$($sa.scene) missing from $($Compare[1])"; continue }
        '{0,-9} static-batched {1,6} -> {2,-6} ({3,+6})   batches {4,6} -> {5,-6} ({6,+6})   drawCalls {7,6} -> {8,-6} ({9,+6})   frame {10,6:F2} -> {11,6:F2} ms' -f `
            $sa.scene, $sa.staticBatchedRenderers, $sb.staticBatchedRenderers, ($sb.staticBatchedRenderers - $sa.staticBatchedRenderers), `
            $sa.batches, $sb.batches, ($sb.batches - $sa.batches), `
            $sa.drawCalls, $sb.drawCalls, ($sb.drawCalls - $sa.drawCalls), `
            $sa.frameMsMean, $sb.frameMsMean
    }
    Write-Host ""
    Write-Host "frameMs is Editor Play Mode and is indicative only - judge feel from a playtest." -ForegroundColor DarkGray
    exit 0
}

# --- measure -----------------------------------------------------------------------------------

$target = Get-PerfPath $Label
if (Test-Path $target) { Remove-Item $target -Force }

$body = @"
using UnityEngine;
using Ironfront.Tools.RecoveredPort;
public class P23MeasureEntry { public static void Main() { ScenePerfProbe.MeasureAll("$Label"); } }
"@

$args = @{ className = 'P23MeasureEntry'; methodName = 'Main'; csharpCode = $body } | ConvertTo-Json -Compress
$argsFile = Join-Path ([IO.Path]::GetTempPath()) "p23-measure-$Label.json"
Set-Content -Path $argsFile -Value $args -Encoding utf8 -NoNewline

Write-Host "Asking the running Unity Editor to measure '$Label' (Play Mode, both scenes)..." -ForegroundColor Cyan
& python (Join-Path $PSScriptRoot 'mcp-call.py') call script-execute $argsFile | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "could not reach the Unity Editor's MCP bridge. Open the Editor (see .claude/scripts/unity-editor.ps1) and retry."
}

# The run is asynchronous: it enters Play Mode per scene and writes the merged file when the
# queue drains. Poll rather than guess a duration.
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while (-not (Test-Path $target)) {
    if ((Get-Date) -gt $deadline) {
        throw "timed out after ${TimeoutSec}s waiting for $target. Check the Editor console; a half-finished run resumes on the next domain reload."
    }
    Start-Sleep -Seconds 5
}

Show-Run $Label | Out-Null
Write-Host ""
Write-Host "Wrote $target" -ForegroundColor Green
