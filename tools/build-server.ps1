# tools/build-server.ps1 — produces the Unity headless (dedicated-server) build that A and C
# need to test the game without a full client. OWNER: Dev D (conventions.md section 7).
#
# WHY THIS EXISTS: A and C cannot test server-side gameplay against a real headless build
# without a one-command way to produce one. It is a week-2 handoff item alongside
# build-libs.ps1 for that reason (dev-d phase-00-foundation.md section 5).
#
# THE OWNERSHIP SPLIT. Dev D owns this script; the Editor-side build method it invokes lives
# under Ironfront_Reborn/Assets/Editor and belongs to Dev A (plan.md section 2 — Dev D does
# not edit the Unity project). Unity cannot produce a player from CLI flags alone: -buildTarget
# only switches the active target, it does not call BuildPipeline.BuildPlayer. So a dedicated
# server build genuinely needs an [MenuItem]/static method on the Unity side. The contract is:
#
#     public static void Ironfront.EditorBuild.BuildDedicatedServer()
#
# reading its output directory from the -buildOutput command-line argument this script passes,
# building StandaloneLinux64 with the Server subtarget, and calling EditorApplication.Exit with
# a non-zero code on failure. If that method is absent Unity exits non-zero and this script
# prints exactly what Dev A needs to add.
#
# Like the Unity step in ci.ps1, this is OPT-IN via UNITY_PATH: B, C and the CI runner are
# never blocked by not having an Editor installed.
#
# Usage:
#   $env:UNITY_PATH = "C:\Program Files\Unity\Hub\Editor\6000.0.x\Editor\Unity.exe"
#   pwsh tools/build-server.ps1 [-OutputPath build/server] [-BuildMethod Ironfront.EditorBuild.BuildDedicatedServer]

[CmdletBinding()]
param(
    # The Unity Editor executable. Defaults to $env:UNITY_PATH so it matches ci.ps1's opt-in.
    [string]$UnityPath = $env:UNITY_PATH,

    # Where the headless build lands, relative to the repo root unless absolute.
    [string]$OutputPath = "build/server",

    # The static Editor method that actually calls BuildPipeline.BuildPlayer. Dev A's contract.
    [string]$BuildMethod = "Ironfront.EditorBuild.BuildDedicatedServer"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if ([string]::IsNullOrWhiteSpace($UnityPath)) {
        Write-Warning @"
UNITY_PATH is not set, so the headless server build was SKIPPED (this is not a failure).
Set it to your Unity Editor executable and re-run, e.g.:
    `$env:UNITY_PATH = "C:\Program Files\Unity\Hub\Editor\6000.0.x\Editor\Unity.exe"
    pwsh tools/build-server.ps1
"@
        exit 0
    }

    if (-not (Test-Path $UnityPath)) {
        throw "UNITY_PATH points at a file that does not exist: $UnityPath"
    }

    $projectPath = Join-Path $repoRoot "Ironfront_Reborn"
    if (-not (Test-Path $projectPath)) {
        throw "Unity project not found: $projectPath"
    }

    # Resolve the output to an absolute path so the Editor method does not have to guess what
    # the working directory was when Unity launched.
    $outAbsolute = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath
    } else {
        Join-Path $repoRoot $OutputPath
    }

    if (-not (Test-Path $outAbsolute)) {
        New-Item -ItemType Directory -Path $outAbsolute -Force | Out-Null
    }

    $logFile = Join-Path $repoRoot "unity-server-build.log"

    Write-Host "Building headless server via $BuildMethod"
    Write-Host "  project: $projectPath"
    Write-Host "  output:  $outAbsolute"
    Write-Host "  log:     $logFile"
    Write-Host ""

    # -batchmode -nographics -quit: no window, no GPU, exit when done. -executeMethod runs
    # Dev A's build method; -buildOutput is our own argument that method reads to know where
    # to write. Unity passes every unrecognised argument through to Environment.GetCommandLineArgs.
    & $UnityPath -batchmode -nographics -quit `
        -projectPath $projectPath `
        -executeMethod $BuildMethod `
        -buildOutput $outAbsolute `
        -logFile $logFile

    $unityExit = $LASTEXITCODE

    if ($unityExit -ne 0) {
        Write-Host ""
        Write-Warning "Unity exited with code $unityExit. See $logFile."
        Write-Warning @"
If the log says the method '$BuildMethod' could not be found, the Editor-side build script is
not in the project yet. That script is Dev A's (Ironfront_Reborn/Assets/Editor); it must expose:

    public static void $BuildMethod()   // reads -buildOutput, builds StandaloneLinux64 (Server
                                         // subtarget), and EditorApplication.Exit(1) on failure.
"@
        throw "Headless server build failed (Unity exit $unityExit)."
    }

    Write-Host ""
    Write-Host "Headless server build complete -> $outAbsolute"
}
finally {
    Pop-Location
}
