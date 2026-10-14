# tools/build-server-in-editor.ps1 -- a STAMPED dedicated-server build in the Unity Editor that is
# already open, driven over the Editor's MCP bridge (tools/mcp-call.py). The batchmode twin is
# tools/build-server.ps1, which needs the Editor closed.
#
# WHY THIS EXISTS. Every game-server deploy builds the server, and closing the Editor for it costs a
# full reopen and reimport. The in-Editor path was run by hand from a scratch script on 2026-10-12
# and again on 2026-10-15; this is that script, so the next deploy does not re-derive it.
#
# WHAT IT DOES, in order:
#   1. Bakes the commit into the source (tools/lib/build-stamp.ps1), refreshes, waits for the
#      compile. The stamp is restored in a finally, so an aborted build never leaves it committed.
#   2. Registers a ONE-SHOT EditorApplication.update handler that calls
#      Ironfront.EditorBuild.BuildDedicatedServer and writes its outcome to tmp/server-build.result.
#      Not EditorApplication.delayCall: with the Editor in the background a delayCall registered
#      over the bridge never fired (2026-10-15), while an update handler ran within a second.
#      Not a blocking call either: a build outlasts the bridge's call timeout, and the server
#      re-executes a call that times out.
#   3. Waits on that FILE, not on the Console. console-get-logs returns the oldest entries of its
#      window first, so a poll of the latest build lines never sees them.
#   4. Checks the server's Managed DLLs were rewritten (Unity keeps the executable when only managed
#      code changed, so the executable's time proves nothing) and prints the stamp they carry.
#   5. Restores the stamp, refreshes, and puts back Menu.unity, which the build rewrites.
#
# Usage:
#   pwsh tools/build-server-in-editor.ps1            # build/server, then:
#   docker build -f infra/docker/gameserver.Dockerfile -t ironfront-game-server:<rev> build/server
#   pwsh tools/deploy-gameservers-azure.ps1 -Image ironfront-game-server:<rev>
#
# Needs: the Editor open on this repo with its MCP server up (.claude/scripts/unity-editor.ps1
# -Action status), and a clean tree, so the stamp names a commit that is what was built.
[CmdletBinding()]
param(
    [int]$TimeoutMinutes = 40
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/build-stamp.ps1')
$bridge = Join-Path $PSScriptRoot 'mcp-call.py'
$resultPath = Join-Path $repoRoot 'tmp/server-build.result'
$managed = Join-Path $repoRoot 'build/server/Ironfront.Server_Data/Managed'

function Invoke-Editor([string]$tool, [string]$argsJson) {
    $out = $argsJson | python $bridge call $tool - 2>&1
    if ($LASTEXITCODE -ne 0) { throw "the Editor bridge failed on ${tool}: $($out -join ' ')" }
    return ($out -join "`n")
}

function Wait-EditorIdle([int]$seconds = 600) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Invoke-Editor 'editor-application-get-state' '{}'
        if ($state -match '"IsCompiling":false' -and $state -match '"IsUpdating":false') { return }
        Start-Sleep -Seconds 3
    }
    throw "the Editor was still compiling or importing after $seconds s"
}

# The Editor builds ITS project: run this from the checkout the Editor has open, or the build lands
# somewhere this script does not look.
$probe = @{ csharpCode = 'public class WhichProject { public static object Main() { return UnityEngine.Application.dataPath; } }'; className = 'WhichProject'; methodName = 'Main' } | ConvertTo-Json -Compress
$editorAssets = Invoke-Editor 'script-execute' $probe
$expected = ((Join-Path $repoRoot 'Ironfront_Reborn/Assets') -replace '\\', '/')
if ($editorAssets -notmatch [regex]::Escape($expected)) {
    throw "the open Editor is not on this checkout ($expected): $editorAssets"
}

$dirty = @(& git -C $repoRoot status --porcelain --untracked-files=no)
if ($dirty.Count -gt 0) {
    throw "the tree has uncommitted changes, so the stamp would name a commit that is not what gets built:`n$($dirty -join "`n")"
}

New-Item -ItemType Directory -Force -Path (Split-Path $resultPath) | Out-Null
Remove-Item $resultPath -ErrorAction SilentlyContinue
$serverDll = Join-Path $managed 'Ironfront.Net.Unity.Server.dll'
$before = if (Test-Path $serverDll) { (Get-Item $serverDll).LastWriteTimeUtc } else { [datetime]::MinValue }

$stamp = Write-BuildStamp -RepoRoot $repoRoot -Dirty $false
try {
    Invoke-Editor 'assets-refresh' '{}' | Out-Null
    Start-Sleep -Seconds 5
    Wait-EditorIdle

    $target = ($resultPath -replace '\\', '/')
    $code = @"
using UnityEditor;
public class InEditorServerBuild { static EditorApplication.CallbackFunction once; public static object Main() {
    once = () => {
        EditorApplication.update -= once;
        try { Ironfront.EditorBuild.BuildDedicatedServer(); System.IO.File.WriteAllText("$target", "returned " + System.DateTime.Now.ToString("o")); }
        catch (System.Exception e) { System.IO.File.WriteAllText("$target", "threw " + e); }
    };
    EditorApplication.update += once;
    EditorApplication.QueuePlayerLoopUpdate();
    return "queued";
} }
"@
    $payload = @{ csharpCode = $code; className = 'InEditorServerBuild'; methodName = 'Main' } | ConvertTo-Json -Compress
    $queued = Invoke-Editor 'script-execute' $payload
    if ($queued -notmatch 'queued') { throw "the build was not queued: $queued" }
    Write-Host "[build] queued in the Editor; waiting for $resultPath"

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-Path $resultPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 10 }
    if (-not (Test-Path $resultPath)) { throw "no build outcome after $TimeoutMinutes minutes" }
    $outcome = Get-Content $resultPath -Raw
    if (-not $outcome.StartsWith('returned')) { throw "the build failed: $outcome" }

    $after = (Get-Item $serverDll).LastWriteTimeUtc
    if ($after -le $before) { throw "the server's Managed DLLs were not rewritten (still $after UTC)" }
    $baked = (& strings -e l $serverDll 2>$null | Select-String -Pattern ([regex]::Escape($stamp.Commit)) | Select-Object -First 1)
    if ($stamp.Commit -and -not $baked) { throw "the built server assembly does not carry the stamp $($stamp.Commit)" }
    Write-Host "[build] done: $($stamp.Line); Managed DLLs written $after UTC"
}
finally {
    Restore-BuildStamp -Originals $stamp.Originals
    Invoke-Editor 'assets-refresh' '{}' | Out-Null
    Start-Sleep -Seconds 5
    Wait-EditorIdle
    & git -C $repoRoot checkout -- Ironfront_Reborn/Assets/Scenes/Menu.unity 2>$null
    Remove-Item $resultPath -ErrorAction SilentlyContinue
}
