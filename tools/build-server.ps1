# tools/build-server.ps1 — produces the Unity headless (dedicated-server) build that A and C
# need to test the game without a full client.md section 7).
#
# WHY THIS EXISTS: A and C cannot test server-side gameplay against a real headless build
# without a one-command way to produce one. It is a week-2 handoff item alongside
# build-libs.ps1 for that reason (dev-d phase-00-foundation.md section 5).
#
# THE OWNERSHIP SPLIT. The master-server track owns this script; the Editor-side build method it invokes lives
# under Ironfront_Reborn/Assets/Editor and belongs to the client track (plan.md section 2 — the master-server track does
# not edit the Unity project). Unity cannot produce a player from CLI flags alone: -buildTarget
# only switches the active target, it does not call BuildPipeline.BuildPlayer. So a dedicated
# server build genuinely needs an [MenuItem]/static method on the Unity side. The contract is:
#
#     public static void Ironfront.EditorBuild.BuildDedicatedServer()
#
# reading its output directory from the -buildOutput command-line argument this script passes,
# building StandaloneLinux64 with the Server subtarget, and calling EditorApplication.Exit with
# a non-zero code on failure. If that method is absent Unity exits non-zero and this script
# prints exactly what the client track needs to add.
#
# Like the Unity step in ci.ps1, this is OPT-IN via UNITY_PATH: B, C and the CI runner are
# never blocked by not having an Editor installed.
#
# Usage:
#   $env:UNITY_PATH = "C:\Program Files\Unity\Hub\Editor\6000.0.x\Editor\Unity.exe"
#   pwsh tools/build-server.ps1 [-OutputPath build/server] [-BuildMethod Ironfront.EditorBuild.BuildDedicatedServer]
#                               [-TarballPath build/gameserver-linux.tar.gz]
#
# OUTPUT: the player tree in -OutputPath, and the tarball in -TarballPath that images.yml consumes.
# The archive holds the tree's contents at its root, because the workflow extracts with
# `tar -xzf ... -C build/server` and then asserts build/server/Ironfront.Server.x86_64 exists.

[CmdletBinding()]
param(
    # The Unity Editor executable. Defaults to $env:UNITY_PATH so it matches ci.ps1's opt-in.
    [string]$UnityPath = $env:UNITY_PATH,

    # Where the headless build lands, relative to the repo root unless absolute.
    [string]$OutputPath = "build/server",

    # The static Editor method that actually calls BuildPipeline.BuildPlayer. The client track's contract.
    [string]$BuildMethod = "Ironfront.EditorBuild.BuildDedicatedServer",

    # The tarball images.yml consumes. The filename is matched literally by the workflow, so it is
    # part of the contract rather than a preference; the directory is not.
    [string]$TarballPath = "build/gameserver-linux.tar.gz"
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
    # the client track's build method; -buildOutput is our own argument that method reads to know where
    # to write. Unity passes every unrecognised argument through to Environment.GetCommandLineArgs.
    #
    # -buildTarget Linux64 does not build anything on its own (see the ownership note above),
    # but it makes Linux the active platform during Unity's own startup. Without it the build
    # method has to switch platforms itself, which forces a full asset reimport in the middle of
    # the build; and on this Windows host the Server subtarget it sets would otherwise have
    # landed on Windows, not Linux. The method still switches defensively for the menu-item path.
    # Start-Process -Wait, NOT the call operator. Unity.exe is a WINDOWS_GUI subsystem binary
    # (PE Optional Header Subsystem = 2, checked against 6000.3.21f1), and PowerShell does not
    # wait for one: `& $UnityPath ...` returns the instant the process is spawned and leaves
    # $LASTEXITCODE unset. Every line below then ran against a build that had not happened —
    # $unityExit was $null, `$null -ne 0` is true, and this script threw
    # "Headless server build failed (Unity exit )" on EVERY run including the successful ones.
    # The empty slot where the code should be is the tell. It also meant the tar step would
    # have packaged a half-written directory, had the throw not pre-empted it.
    #
    # Note it only LOOKS like it waits when run from a shell that pipes stdout: the orphaned
    # Unity inherits the pipe and the parent shell blocks on it until Unity exits, so the
    # script's own failure is reported minutes later, exactly as though it had waited.
    $unityArgs = @(
        '-batchmode', '-nographics', '-quit',
        '-projectPath', $projectPath,
        '-buildTarget', 'Linux64',
        '-executeMethod', $BuildMethod,
        '-buildOutput', $outAbsolute,
        '-logFile', $logFile
    )

    $unityProcess = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -Wait -PassThru
    $unityExit = $unityProcess.ExitCode

    if ($unityExit -ne 0) {
        Write-Host ""
        Write-Warning "Unity exited with code $unityExit. See $logFile."
        Write-Warning @"
If the log says the method '$BuildMethod' could not be found, the Editor-side build script is
not in the project yet. That script is the client track's (Ironfront_Reborn/Assets/Editor); it must expose:

    public static void $BuildMethod()   // reads -buildOutput, builds StandaloneLinux64 (Server
                                         // subtarget), and EditorApplication.Exit(1) on failure.
"@
        throw "Headless server build failed (Unity exit $unityExit)."
    }

    Write-Host ""
    # A zero exit is not evidence that a player was written. Unity's BuildPipeline can return
    # BuildResult.Succeeded, report a nonzero total size, and write nothing at all: it does that when
    # UnityEditor.PostprocessBuildPlayer.Postprocess throws, which is the step that installs the
    # compiled player into the output directory. EditorBuild.cs now checks its own output for exactly
    # this reason and exits non-zero, but the check that matters to the pipeline belongs here too —
    # everything downstream keys on this one file existing.
    $executable = Join-Path $outAbsolute "Ironfront.Server.x86_64"
    if (-not (Test-Path $executable)) {
        Write-Warning "Unity exited 0 but $executable does not exist. See $logFile — look for"
        Write-Warning "`"not supported`" thrown out of PostprocessBuildPlayer.Postprocess. If the"
        Write-Warning "Linux Dedicated Server Build Support module was installed while an Editor was"
        Write-Warning "already open, that Editor never loaded it: Unity registers platform support"
        Write-Warning "modules only at startup, so it has to be restarted."
        throw "Headless server build produced no player at $executable."
    }

    Write-Host "Headless server build complete -> $outAbsolute"

    # Package it. images.yml downloads the release asset named exactly gameserver-linux.tar.gz,
    # extracts it with `tar -xzf ... -C build/server`, and then asserts
    # build/server/Ironfront.Server.x86_64 exists — so the executable has to sit at the ROOT of the
    # archive, which is what -C $outAbsolute . gives and what `tar -czf x.tar.gz build/server` would
    # not. Producing the tarball here rather than leaving it to the person running the script is the
    # difference between this script satisfying the runbook and only half-satisfying it; the D1.2
    # instructions say "produces gameserver-linux.tar.gz" and two rounds of client reports have
    # recorded that it produced a directory instead.
    $tarAbsolute = if ([System.IO.Path]::IsPathRooted($TarballPath)) {
        $TarballPath
    } else {
        Join-Path $repoRoot $TarballPath
    }

    $tarParent = Split-Path -Parent $tarAbsolute
    if ($tarParent -and -not (Test-Path $tarParent)) {
        New-Item -ItemType Directory -Path $tarParent -Force | Out-Null
    }

    # bsdtar ships with Windows 10 1803+ and every Linux runner has GNU tar; both accept these flags.
    # Absent on an older host, in which case the build is still there and the operator can pack it.
    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if (-not $tar) {
        Write-Warning "tar was not found on PATH, so $tarAbsolute was not created. Pack it yourself:"
        Write-Warning "    tar -czf `"$tarAbsolute`" -C `"$outAbsolute`" ."
        return
    }

    if (Test-Path $tarAbsolute) { Remove-Item $tarAbsolute -Force }

    Write-Host "Packaging -> $tarAbsolute"

    # Chdir to the destination and pass a BARE filename to -f. GNU tar parses the archive argument
    # as host:path, so `tar -czf E:\...\x.tar.gz` fails on a Windows host with "Cannot connect to E:
    # resolve failed" — and Git for Windows ships GNU tar on PATH ahead of the bsdtar in System32,
    # so that is the common case, not the exotic one. Only -f is parsed that way; -C keeps its
    # absolute path. --force-local would also fix it for GNU tar and is unrecognised by bsdtar.
    $tarName = Split-Path -Leaf $tarAbsolute
    Push-Location $tarParent
    try {
        # `-C $outAbsolute .` puts the tree's CONTENTS at the archive root. `tar -czf x build/server`
        # would nest them under build/server/, and images.yml extracts with -C build/server and then
        # tests build/server/Ironfront.Server.x86_64, so the nested shape fails there.
        & tar -czf $tarName -C $outAbsolute .
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed with exit code $LASTEXITCODE packaging $outAbsolute."
        }

        # Assert what images.yml asserts, here, where the failure is cheap to read — otherwise the
        # archive fails its check in the workflow, several manual steps and one release later.
        # Anchored to the archive root on purpose: a pattern like (^|/)Ironfront\.Server\.x86_64$
        # also matches server/Ironfront.Server.x86_64, so it would pass the exact wrong-shape archive
        # this check exists to catch.
        $listed = & tar -tzf $tarName
        if ($LASTEXITCODE -ne 0) {
            throw "tar could not list $tarAbsolute back."
        }
        if (-not ($listed -match '^(\./)?Ironfront\.Server\.x86_64$')) {
            throw @"
$tarAbsolute does not have Ironfront.Server.x86_64 at its root. images.yml extracts with
`tar -xzf ... -C build/server` and then tests build/server/Ironfront.Server.x86_64, so a
nested layout fails there. Archive contents:
$($listed -join "`n")
"@
        }
    }
    finally { Pop-Location }

    $sizeMb = [math]::Round((Get-Item $tarAbsolute).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Packaged $tarAbsolute ($sizeMb MB)"
    Write-Host "Next: attach it to a GitHub Release as gameserver-linux.tar.gz, then run the"
    Write-Host "      'images' workflow with gameserver_release_tag set to that tag."
}
finally {
    Pop-Location
}
