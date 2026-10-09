# tools/build-player.ps1 -- build the Windows (or macOS) player and STOP.
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
# WHAT IT BUILDS (since 2026-10-02). By default the player people download: a RELEASE build on
# IL2CPP with Net/Diagnostics compiled out. Every release before that was the lane-B harness's
# Development build on Mono -- "Development Build" in the corner of every screen, profiler hooks
# compiled in, and the harness's scripted aim and input reachable through environment variables.
#   -Development      the old development player on Mono, with diagnostics: what the Unity
#                     Profiler attaches to, and the fast build for a quick playtest.
#   -KeepDiagnostics  release + IL2CPP, but Net/Diagnostics kept, so IRONFRONT_LOG_FRAMES=1 can
#                     measure the release player. Never package it.
# IL2CPP needs the "Windows Build Support (IL2CPP)" module, installed through Unity Hub, and the
# Visual Studio Build Tools C++ workload (MSVC). The first IL2CPP build spends several minutes in
# the C++ compiler; later ones reuse Library/Bee and are much faster.
#
# -Platform macos (since 2026-10-07) builds build/macos/Ironfront.app instead: the same release
# player, but on MONO -- IL2CPP for macOS links with Apple's toolchain and can only be built on a
# Mac -- and as one universal binary for Intel and Apple silicon. It needs the "Mac Build Support
# (Mono)" module, installed through Unity Hub. The first macOS build compiles every shader for
# Metal and takes far longer than a Windows build; later ones reuse the shader cache in Library/.
#
# -Platform linux (since 2026-10-09) builds build/linux/Ironfront.x86_64: the Windows recipe for
# x86_64 Linux, IL2CPP cross-compiled from Windows with the com.unity.toolchain.win-x86_64-linux
# package the project carries. It needs the "Linux Build Support (IL2CPP)" module. IL2CPP is not a
# choice here: Unity ships no non-development Mono player for Linux.
#
# Usage:
#   pwsh tools/build-player.ps1
#   pwsh tools/build-player.ps1 -Platform macos
#   pwsh tools/build-player.ps1 -Platform linux
#   pwsh tools/build-player.ps1 -Development
#   pwsh tools/build-player.ps1 -UnityPath "D:\UnityEditor\6000.3.21f1\Editor\Unity.exe"
#   pwsh tools/build-player.ps1 -OutputDirectory build/windows -LogFile tmp/build-player.log

[CmdletBinding()]
param(
    # The Unity Editor. If omitted, locate the exact project version via Unity Hub.
    # Same variable tools/build-server.ps1 and run-lane-b.ps1 read.
    [string] $UnityPath = $env:UNITY_PATH,

    # windows (Ironfront.exe, IL2CPP), macos (Ironfront.app, Mono, universal) or linux
    # (Ironfront.x86_64, IL2CPP cross-compiled from Windows). See the header.
    [ValidateSet("windows", "macos", "linux")]
    [string] $Platform = "windows",

    # Where the player lands. Default build/windows -- the contract with run-lane-b.ps1 and
    # play-lan.ps1, which both look there -- or build/macos for -Platform macos.
    [string] $OutputDirectory = "",

    [string] $LogFile = "",

    # Use only when the complete plugin closure was just built and copied (for example, while
    # cutting a clean stamped player immediately after committing those binaries). The default
    # deliberately rebuilds everything so protocol constants cannot be mixed across DLLs.
    [switch] $SkipLibraryBuild,

    # Skip the "is an Editor running" refusal. For the case where the process found is somebody
    # else's Unity on another project -- the check cannot tell them apart.
    [switch] $Force,

    # The development player on Mono, diagnostics included. See the header.
    [switch] $Development,

    # Release + IL2CPP with Net/Diagnostics kept, for measuring. See the header.
    [switch] $KeepDiagnostics
)

if ($Development -and $KeepDiagnostics) {
    throw "-KeepDiagnostics only applies to a release build; a -Development build always keeps them."
}

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$isMac = $Platform -eq "macos"
$isLinuxPlayer = $Platform -eq "linux"
if (-not $OutputDirectory) { $OutputDirectory = "build/$Platform" }

. "$PSScriptRoot/lib/build-stamp.ps1"
. "$PSScriptRoot/lib/mac-bundle.ps1"

# Capture source cleanliness BEFORE build-libs replaces the tracked plugin binaries. Managed
# assemblies contain a new PE/MVID on each successful compilation, so testing afterwards makes
# every otherwise-clean build report -dirty merely because this script performed its required
# first step. Real source or prefab edits are still included in this project-scoped snapshot.
$unityProjectDirtyBeforeLibraryBuild = $false
try {
    $unityProjectDirtyBeforeLibraryBuild =
        [bool](& git -C $repoRoot status --porcelain -- Ironfront_Reborn 2>$null)
}
catch { }

# Rebuild the complete Unity plugin closure before opening the Editor. PROTOCOL_VERSION is a
# const, so its value is inlined into dependent assemblies such as Ironfront.MasterClient.dll.
# Copying only Ironfront.Net.Protocol.dll can therefore produce a player whose game protocol is
# current while its login request still sends the previous version; the master reports that as
# "This build is out of date." Keeping this inside the player build makes that mixed state
# impossible on every future build, not merely repaired in the current working tree.
$libraryBuild = Join-Path $repoRoot "tools/build-libs.ps1"
if (-not $SkipLibraryBuild) {
    & $libraryBuild -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "the Unity plugin libraries did not build" }
}
else {
    Write-Host "[build] using the already-built Unity plugin closure (-SkipLibraryBuild)"
}

# Read the required Editor version from the project and discover that exact version. Unity Hub
# supports a secondary install directory (common on machines where C: is small) and records it
# in a small JSON file. Keep an explicitly supplied valid path first, but do not let a stale
# UNITY_PATH prevent discovery of a working local installation.
$versionFile = Join-Path $repoRoot "Ironfront_Reborn/ProjectSettings/ProjectVersion.txt"
if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
    throw "ProjectVersion.txt was not found at $versionFile."
}

$versionMatch = Select-String -LiteralPath $versionFile -Pattern '^m_EditorVersion:\s*(\S+)' |
    Select-Object -First 1
if (-not $versionMatch) {
    throw "Could not read m_EditorVersion from $versionFile."
}
$requiredVersion = $versionMatch.Matches[0].Groups[1].Value

$candidates = @()
if ($UnityPath) { $candidates += $UnityPath }
$candidates += "C:/Program Files/Unity/Hub/Editor/$requiredVersion/Editor/Unity.exe"
$candidates += "C:/Program Files/Unity/Editor/Unity.exe"

if ($env:APPDATA) {
    $hubSecondary = Join-Path $env:APPDATA "UnityHub/secondaryInstallPath.json"
    if (Test-Path -LiteralPath $hubSecondary -PathType Leaf) {
        try {
            $secondaryRoot = Get-Content -LiteralPath $hubSecondary -Raw | ConvertFrom-Json
            if ($secondaryRoot) {
                $candidates += Join-Path $secondaryRoot "$requiredVersion/Editor/Unity.exe"
            }
        }
        catch {
            Write-Warning "Could not read Unity Hub install directory from ${hubSecondary}: $_"
        }
    }
}

$UnityPath = $candidates |
    Where-Object {
        $_ -and
        (Test-Path -LiteralPath $_ -PathType Leaf) -and
        ([System.IO.Path]::GetFileName($_) -eq "Unity.exe")
    } |
    Select-Object -First 1

if (-not $UnityPath) {
    $lookedIn = ($candidates | Where-Object { $_ } | Select-Object -Unique) -join "`n  "
    throw "Unity $requiredVersion was not found. Looked in:`n  $lookedIn`n" +
          "Install that version in Unity Hub, or pass -UnityPath '<path-to-Unity.exe>'."
}

$UnityPath = (Resolve-Path -LiteralPath $UnityPath).Path

# The project lock is held by a running Editor whether or not it is THIS project, and Unity's
# own failure for that case is a batchmode exit with the reason in the log rather than on
# stdout. Refusing here costs a second; discovering it there costs the length of a licence
# check plus an asset scan.
# Editors only, by path: Unity Hub runs its own helper called unity.exe ("resources\unity.exe serve"),
# and matching on the process name alone refused every build while the Hub was open.
$editors = @(Get-Process Unity -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like '*\Editor\Unity.exe' })
if ($editors.Count -gt 0 -and -not $Force) {
    throw ("a Unity Editor is running (pid $($editors.Id -join ', ')). Close it first -- " +
           "BuildPlayer cannot start while the project is locked, and this build strips " +
           "UNITY_MCP_READY, which queues a recompile the build would then wait on forever.`n" +
           "  pwsh .claude/scripts/unity-editor.ps1 close`n" +
           "Pass -Force if that Editor is on a different project.")
}

if (-not $LogFile) { $LogFile = Join-Path $repoRoot "tmp/build-player.log" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null

$buildOut = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $buildOut | Out-Null

# A folder that holds the OTHER scripting backend's player is emptied first. Unity does not clean
# what it does not write: a release build into a Mono folder would leave MonoBleedingEdge/ and
# Ironfront_Data/Managed/ beside GameAssembly.dll, package-release.ps1 would ship both, and anybody
# dating the build by a Managed DLL would read the previous build's timestamp.
#
# The macOS player is always Mono, so there is no other backend to find -- but the .app is a folder
# Unity writes INTO, and a file a previous build left inside it (a development build's, say) would
# be inside the bundle players download. Removed first; the slow part of the build (the Metal
# shader cache and Library/Bee) is not in the output folder, so this costs only a copy.
if ($isMac) {
    $staleApp = Join-Path $buildOut "Ironfront.app"
    if (Test-Path -LiteralPath $staleApp) {
        Write-Host "[build] removing the previous $staleApp"
        Remove-Item -LiteralPath $staleApp -Recurse -Force
    }
}
else {
    $holdsMono   = (Test-Path (Join-Path $buildOut "MonoBleedingEdge")) -or
                   (Test-Path (Join-Path $buildOut "Ironfront_Data/Managed"))
    # GameAssembly.dll on Windows, GameAssembly.so on Linux.
    $holdsIl2cpp = (Test-Path (Join-Path $buildOut "GameAssembly.dll")) -or
                   (Test-Path (Join-Path $buildOut "GameAssembly.so"))
    if (($Development -and $holdsIl2cpp) -or (-not $Development -and $holdsMono)) {
        Write-Host "[build] $buildOut holds the other scripting backend's player; emptying it first"
        Get-ChildItem -LiteralPath $buildOut -Force | Remove-Item -Recurse -Force
    }
}

# What the player is, what proves it was written, and the line the Editor logs once it has checked
# the output (EditorBuildWindowsHarness.PlayerPlatform.CompletionMarker). The macOS executable is
# named in the bundle's Info.plist, which does not exist until the build has run (see below).
if ($isMac) {
    $exe            = Join-Path $buildOut "Ironfront.app/Contents/Info.plist"
    $executeMethod  = "Ironfront.EditorBuildWindowsHarness.BuildMacPlayer"
    $completionLine = "[build] macos player complete ->"
}
elseif ($isLinuxPlayer) {
    $exe            = Join-Path $buildOut "Ironfront.x86_64"
    $executeMethod  = "Ironfront.EditorBuildWindowsHarness.BuildLinuxPlayer"
    $completionLine = "[build] linux player complete ->"
}
else {
    $exe            = Join-Path $buildOut "Ironfront.exe"
    $executeMethod  = "Ironfront.EditorBuildWindowsHarness.BuildWindowsPlayer"
    $completionLine = "[build] lane-B windows player complete ->"
}
$before = if (Test-Path $exe) { (Get-Item $exe).LastWriteTime } else { $null }

Write-Host "[build] Unity : $UnityPath"
Write-Host "[build] output: $buildOut"
Write-Host "[build] log   : $LogFile"
Write-Host ("[build] this takes roughly ten minutes" +
            $(if ($isMac) { ", far longer on the first macOS build (every shader compiles for Metal)" }
              elseif ($Development) { "" } else { ", longer on the first IL2CPP build" }) +
            ". Nothing is printed until it ends.")

# STAMP THE BUILD WITH THE COMMIT IT CAME FROM, then put the sources back.
#
# WHY THE FILES ARE REWRITTEN IN PLACE. The stamp has to be compiled INTO the assemblies, because
# the failure it exists to catch is somebody copying one stale DLL into an otherwise current build
# folder -- and a stamp written beside the executable describes the FOLDER, so it would read
# "current" while the code was old. It would lie in exactly the case it was written for. The cost
# of compiling it in is that a tracked source file has to hold the value for the length of one
# build; the restore in the finally below means a failed or cancelled build leaves the tree as it
# found it.
#
# A checkout therefore always reads "dev", the Editor always compiles, and only a build produced by
# one of the build scripts claims an identity -- which is the honest arrangement, because only
# those builds are a thing anybody hands to anybody else.
#
# The rewrite itself lives in tools/lib/build-stamp.ps1 because build-server.ps1 needs the same
# thing and did not have it: the dedicated server shipped an unstamped "dev" binary for as long as
# this logic lived only here.
$stamp = Write-BuildStamp -RepoRoot $repoRoot -Dirty $unityProjectDirtyBeforeLibraryBuild
$stampOriginals = $stamp.Originals

$buildArgs = @(
    "-batchmode", "-quit", "-nographics",
    "-projectPath", (Join-Path $repoRoot "Ironfront_Reborn"),
    "-executeMethod", $executeMethod,
    "-buildOutput", $buildOut,
    "-logFile", $LogFile
)
# Linux STARTS on its target. The IL2CPP cross-compiler comes from the sysroot/toolchain packages,
# whose classes implement Unity's Sysroot interface only when UNITY_STANDALONE_LINUX_API is defined
# at compile time. A batch run that starts on Windows and switches inside the build method only
# QUEUES the recompile ("Requested script compilation because: Switching to platform
# LinuxStandaloneSupport"), builds with the Windows-compiled scripts, and fails with "No Toolchain
# found for host platform" although the packages are installed (measured 2026-10-09).
# tools/build-server.ps1 passes -buildTarget Linux64 for the same reason.
if ($isLinuxPlayer) { $buildArgs += @("-buildTarget", "Linux64") }
if (-not $Development) { $buildArgs += "-release" }
if (-not $Development -and -not $KeepDiagnostics) { $buildArgs += "-noDiagnostics" }

$releaseBackend = if ($isMac) { "Mono, universal" } else { "IL2CPP" }
$flavour = if ($Development) { "development, Mono, diagnostics" }
           elseif ($KeepDiagnostics) { "release, $releaseBackend, diagnostics KEPT (measuring only -- never package)" }
           else { "release, $releaseBackend, no diagnostics" }
$flavour = "$Platform, $flavour"
Write-Host "[build] flavour: $flavour"

$started = Get-Date
$unityExitCode = $null

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
try {
    $proc = Start-Process -FilePath $UnityPath -ArgumentList $buildArgs -PassThru -NoNewWindow
    $proc.WaitForExit()
    # Refresh before reading ExitCode. On Windows, Start-Process can otherwise leave the
    # property empty even though WaitForExit returned; PowerShell then compares that empty
    # value as non-zero and reports a failed build after Unity already wrote a valid player.
    $proc.Refresh()
    $unityExitCode = $proc.ExitCode
}
finally {
    # In a finally so a failed build, a thrown check or a Ctrl-C all leave the tree as they found
    # it. Restoring from the captured text rather than `git checkout --` deliberately: the latter
    # would also discard any unrelated uncommitted edit to these files, which is a destructive
    # answer to a bookkeeping question.
    Restore-BuildStamp -Originals $stampOriginals
}

$elapsed = [int]((Get-Date) - $started).TotalSeconds

if ($null -eq $unityExitCode) {
    # Some Unity/Windows combinations release the native process handle before PowerShell can
    # read ExitCode, even after WaitForExit + Refresh. The editor harness writes this marker only
    # after BuildPipeline returned success and the complete artifact was measured. Since -logFile
    # starts a fresh log for this invocation, it is a safe success witness rather than a stale
    # file-exists check.
    $completed = Select-String -LiteralPath $LogFile -SimpleMatch $completionLine |
        Select-Object -Last 1
    if (-not $completed) {
        throw "the $Platform player build returned no exit code and no completion marker after ${elapsed}s. See $LogFile."
    }
    Write-Warning "[build] Unity returned no readable exit code; accepted the harness completion marker."
}

if ($null -ne $unityExitCode -and $unityExitCode -ne 0) {
    throw "the $Platform player build exited $unityExitCode after ${elapsed}s. See $LogFile."
}

if ($isMac -and (Test-Path $exe)) { $exe = Get-MacBundleExecutable (Join-Path $buildOut "Ironfront.app") }
if (-not (Test-Path $exe)) {
    throw "the build reported success but there is no $exe. See $LogFile."
}

# Unity keeps the executable and rewrites the code, so Ironfront.exe's own timestamp is NOT
# evidence that anything was rebuilt -- a green build routinely leaves it untouched. The code is
# what moved: the managed assemblies on Mono, GameAssembly.dll on IL2CPP.
$after = (Get-Item $exe).LastWriteTime
$code = if ($isMac) { Join-Path $buildOut "Ironfront.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll" }
        elseif ($Development) { Join-Path $buildOut "Ironfront_Data/Managed/Assembly-CSharp.dll" }
        elseif ($isLinuxPlayer) { Join-Path $buildOut "GameAssembly.so" }
        else { Join-Path $buildOut "GameAssembly.dll" }
$codeStamp = if (Test-Path $code) { (Get-Item $code).LastWriteTime } else { "MISSING" }

Write-Host ""
Write-Host "[build] OK in ${elapsed}s ($flavour)"
Write-Host "[build] $exe"
Write-Host "[build]   exe  last written $after$(if ($before -eq $after) { '  (unchanged -- expected)' })"
Write-Host "[build]   $(Split-Path -Leaf $code) last written $codeStamp  <- judge the build by this"
Write-Host ""
if ($isMac -or $isLinuxPlayer) {
    Write-Host "[build] next: pwsh tools/package-release.ps1 -Platform $Platform -Version <vX.Y.Z>"
}
else {
    Write-Host "[build] next: pwsh tools/play-lan.ps1 -PlayerId <id>   (joins the live fly master)"
}
