# tools/build-player.ps1 -- build the Windows player and STOP.
#
# WHY THIS EXISTS SEPARATELY. Until now the only way to produce build/windows/Ironfront.exe
# from a shell was `run-lane-b.ps1 -Build`, and that switch does not mean "build" -- it means
# "build, then launch a headless server and three scripted clients and grade them", which is
# another twelve to fifteen minutes of work nobody asked for when all they wanted was a current
# binary. There was no way to say the first half without the second, so anyone rebuilding for a
# human playtest either sat through a lane-B run or built from the Editor menu by hand.
#
# This is the first half, alone. run-lane-b.ps1 -Build is unchanged and still works; it simply
# is no longer the only door.
#
# THE EDITOR MUST BE CLOSED, and for two independent reasons -- the project lock, and the fact
# that BuildWindowsPlayer strips UNITY_MCP_READY, which queues an Editor recompile that
# BuildPlayer refuses to start during. This script checks for a live Editor and says so, rather
# than letting Unity fail forty seconds in with a lock message buried in a log file.
#
# Usage:
#   $env:UNITY_PATH = "D:\UnityEditor\6000.3.21f1\Editor\Unity.exe"
#   pwsh tools/build-player.ps1
#   pwsh tools/build-player.ps1 -OutputDirectory build/windows -LogFile tmp/build-player.log

[CmdletBinding()]
param(
    # The Unity Editor. Same variable tools/build-server.ps1 and run-lane-b.ps1 read.
    [string] $UnityPath = $env:UNITY_PATH,

    # Where the player lands. The contract with run-lane-b.ps1, play-lan.ps1 and
    # playtest-local.ps1 is this default; all three look here.
    [string] $OutputDirectory = "build/windows",

    [string] $LogFile = "",

    # Skip the "is an Editor running" refusal. For the case where the process found is somebody
    # else's Unity on another project -- the check cannot tell them apart.
    [switch] $Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    throw "UNITY_PATH is not set or does not point at Unity.exe. Set it first:`n" +
          "  `$env:UNITY_PATH = 'D:\UnityEditor\6000.3.21f1\Editor\Unity.exe'"
}

# The project lock is held by a running Editor whether or not it is THIS project, and Unity's
# own failure for that case is a batchmode exit with the reason in the log rather than on
# stdout. Refusing here costs a second; discovering it there costs the length of a licence
# check plus an asset scan.
$editors = @(Get-Process Unity -ErrorAction SilentlyContinue)
if ($editors.Count -gt 0 -and -not $Force) {
    throw ("a Unity Editor is running (pid $($editors.Id -join ', ')). Close it first -- " +
           "BuildPlayer cannot start while the project is locked, and this build strips " +
           "UNITY_MCP_READY, which queues a recompile the build would then wait on forever.`n" +
           "  pwsh .claude/scripts/unity-editor.ps1 -Stop`n" +
           "Pass -Force if that Editor is on a different project.")
}

if (-not $LogFile) { $LogFile = Join-Path $repoRoot "tmp/build-player.log" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null

$buildOut = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $buildOut | Out-Null

$exe = Join-Path $buildOut "Ironfront.exe"
$before = if (Test-Path $exe) { (Get-Item $exe).LastWriteTime } else { $null }

Write-Host "[build] Unity : $UnityPath"
Write-Host "[build] output: $buildOut"
Write-Host "[build] log   : $LogFile"
Write-Host "[build] this takes roughly ten minutes. Nothing is printed until it ends."

$buildArgs = @(
    "-batchmode", "-quit", "-nographics",
    "-projectPath", (Join-Path $repoRoot "Ironfront_Reborn"),
    "-executeMethod", "Ironfront.EditorBuildWindowsHarness.BuildWindowsPlayer",
    "-buildOutput", $buildOut,
    "-logFile", $LogFile
)

$started = Get-Date

# -PassThru and then WaitForExit(), NOT -Wait. MEASURED 2026-09-03: Start-Process -Wait waits on
# the whole descendant tree, and a batchmode Unity leaves something behind that outlives it -- the
# Editor exited at 00:57 with a written Build Report and a rebuilt Assembly-CSharp.dll, and the
# -Wait call had still not returned three minutes later. The build succeeds and the script hangs
# for ever, which reads as a failed build. run-lane-b.ps1 -Build has the same shape and the same
# hang; this is the fixed version of it.
#
# The result goes into a name that is NOT a parameter of this script: assigning a Process object
# over a [switch] fails AFTER the build has run, so the build succeeds and the script reports
# failure.
$proc = Start-Process -FilePath $UnityPath -ArgumentList $buildArgs -PassThru -NoNewWindow
$proc.WaitForExit()
$elapsed = [int]((Get-Date) - $started).TotalSeconds

if ($proc.ExitCode -ne 0) {
    throw "the Windows player build exited $($proc.ExitCode) after ${elapsed}s. See $LogFile."
}

if (-not (Test-Path $exe)) {
    throw "the build reported success but there is no $exe. See $LogFile."
}

# Unity keeps the executable and rewrites the managed DLLs, so Ironfront.exe's own timestamp is
# NOT evidence that anything was rebuilt -- a green build routinely leaves it untouched. The
# managed assemblies are what moved.
$after = (Get-Item $exe).LastWriteTime
$asm   = Join-Path $buildOut "Ironfront_Data/Managed/Assembly-CSharp.dll"
$asmStamp = if (Test-Path $asm) { (Get-Item $asm).LastWriteTime } else { "MISSING" }

Write-Host ""
Write-Host "[build] OK in ${elapsed}s"
Write-Host "[build] $exe"
Write-Host "[build]   exe  last written $after$(if ($before -eq $after) { '  (unchanged -- expected)' })"
Write-Host "[build]   Assembly-CSharp.dll last written $asmStamp  <- judge the build by this"
Write-Host ""
Write-Host "[build] next: pwsh tools/playtest-local.ps1 -Clients 4"
