using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Ironfront
{
    /// <summary>
    /// Produces the Windows player that phase-3D lane B's runner launches four times: once
    /// headless as the server, three times rendered as scripted clients -- and, since 2026-10-07,
    /// the macOS player as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: <c>tools/run-lane-b.ps1</c> calls
    /// <c>-executeMethod Ironfront.EditorBuildWindowsHarness.BuildWindowsPlayer</c> and passes
    /// <c>-buildOutput &lt;path&gt;</c>, exactly as <c>tools/build-server.ps1</c> does for
    /// <see cref="EditorBuild.BuildDedicatedServer"/>. Unity cannot produce a player from CLI
    /// flags alone — <c>-buildTarget</c> only switches the active target — so a static method is
    /// the only channel there is.
    /// </para>
    /// <para>
    /// <b>Harness scaffolding, and since 2026-10-02 the shipping client too.</b>
    /// <c>tools/build-player.ps1</c> passes <c>-release -noDiagnostics</c>, which is the zip
    /// players download; without them this is the development player lane B has always used.
    /// The product's server is the Linux dedicated build and stays so; <see cref="EditorBuild"/>
    /// is untouched by this file.
    /// What this exists for is that lane B needs three RENDERED clients as separate OS
    /// processes on the machine the work is being done on, and that machine is Windows. A
    /// verdict reached here therefore describes the game, not the deployment target: a
    /// Windows-Mono headless server is not the Linux server byte for byte, and any check that
    /// turns on server-side floating-point or platform behaviour has to be re-read on Linux
    /// before it is trusted. The phase report says so beside the verdicts.
    /// </para>
    /// <para>
    /// <b>One binary, two roles.</b> The launched process decides which half of the scene it is
    /// by <c>IRONFRONT_LANEB_ROLE</c>, read by <c>LaneBHarness</c> — so there is one build to
    /// wait for rather than two, and the server and the clients are provably the same code.
    /// </para>
    /// <para>
    /// <b>The macOS player</b> (<see cref="BuildMacPlayer"/>, <c>build-player.ps1 -Platform macos</c>)
    /// goes through the same steps with two differences the build machine forces. It is always
    /// Mono: IL2CPP for macOS links with Apple's toolchain, which only runs on a Mac, and Unity
    /// cross-compiles IL2CPP from Windows to Windows and Linux only. And it is a universal binary
    /// (Intel + Apple silicon), set explicitly, because an Intel-only player runs on an M-series
    /// Mac only under Rosetta, which Apple is retiring.
    /// </para>
    /// </remarks>
    public static class EditorBuildWindowsHarness
    {
        private const string BuildOutputArgument = "-buildOutput";

        // Opt-in strip of Assets/Scripts/Net/Diagnostics/. Passed as an EXTRA define rather
        // than by editing the project define set, because extraScriptingDefines applies to the
        // player compilation alone and queues no Editor recompile -- unlike StripEditorOnlyDefines
        // below, which is why that one refuses to run outside batchmode.
        //
        // The flag exists so the guard can be PROVEN rather than asserted: build once without
        // it and LaneBHarness is in the player, build once with it and it is not. Every release
        // build passes it (tools/build-player.ps1): the harness's scripted aim and input must not
        // ship in a client anybody can start with environment variables.
        private const string NoDiagnosticsArgument = "-noDiagnostics";
        private const string NoDiagnosticsDefine   = "IRONFRONT_NO_DIAGNOSTICS";

        // The player people download. Until 2026-10-02 every release zip was this harness's
        // Development build: "Development Build" in the corner of every screen, the profiler and
        // player-connection hooks compiled in, and Mono. -release builds it without
        // BuildOptions.Development and, on Windows, on IL2CPP, which compiles the game's C# to
        // native code.
        //
        // IL2CPP is switched on for this build only and the project's own backend put back after,
        // the same way UNITY_MCP_READY is. The lane-B harness and quick playtest builds stay on
        // Mono because an IL2CPP build spends minutes in the C++ compiler, and they are rebuilt
        // many times a day. The IL2CPP options that do live in ProjectSettings.asset (compiler
        // configuration, code generation, stack-trace line numbers) only take effect on a build
        // that is IL2CPP, so they can stay committed. The Minimal stripping level applies to Mono
        // builds as well, on purpose: a type that only reflection reaches breaks in the playtest
        // build first rather than in the release.
        private const string ReleaseArgument = "-release";

        // Same Editor-only package define EditorBuild strips, for the same reason: with it set
        // the MCP runtime assembly compiles into the player against precompiled references that
        // are all constrained to UNITY_EDITOR. See EditorBuild.StripEditorOnlyDefines.
        private const string McpReadyDefine = "UNITY_MCP_READY";

        // OSArchitecture.x64ARM64, by name: the enum's assembly is always there, but the property
        // that takes it ships with the Mac Build Support module. See UseUniversalMacArchitecture.
        private const string UniversalMacArchitecture = "x64ARM64";

        /// <summary>What differs between the players this class builds.</summary>
        private sealed class PlayerPlatform
        {
            public string Name;
            public BuildTarget Target;
            public string Module;
            public string DefaultOutputDirectory;

            // Ironfront.exe / Ironfront.app. Matched literally by the tools/ scripts, so it is
            // part of the contract.
            public string PlayerFileName;

            // Written after a verified build. build-player.ps1 accepts it in place of an exit
            // code Windows sometimes loses, so the Windows wording must not change.
            public string CompletionMarker;

            // null = whatever the project is set to (Mono, see ProjectSettings.asset).
            public ScriptingImplementation? ReleaseBackend;
            public ScriptingImplementation? DevelopmentBackend;

            // A batch build normally leaves the target where it built, to save a switch. The
            // Editor's working target is Windows, though, so a batch macOS build switches back:
            // otherwise the next interactive Editor opens compiling the game for macOS.
            public bool RestoreTargetInBatchMode;
        }

        private static readonly PlayerPlatform Windows = new PlayerPlatform
        {
            Name = "windows",
            Target = BuildTarget.StandaloneWindows64,
            Module = "Windows Build Support",
            DefaultOutputDirectory = "build/windows",
            PlayerFileName = "Ironfront.exe",
            CompletionMarker = "lane-B windows player complete",
            ReleaseBackend = ScriptingImplementation.IL2CPP,
            DevelopmentBackend = null,
            RestoreTargetInBatchMode = false,
        };

        private static readonly PlayerPlatform MacOS = new PlayerPlatform
        {
            Name = "macos",
            Target = BuildTarget.StandaloneOSX,
            Module = "Mac Build Support (Mono)",
            DefaultOutputDirectory = "build/macos",
            PlayerFileName = "Ironfront.app",
            CompletionMarker = "macos player complete",
            ReleaseBackend = ScriptingImplementation.Mono2x,
            DevelopmentBackend = ScriptingImplementation.Mono2x,
            RestoreTargetInBatchMode = true,
        };

        [MenuItem("Ironfront/Build Windows Player (lane-B harness)")]
        public static void BuildWindowsPlayer() => BuildPlayer(Windows);

        [MenuItem("Ironfront/Build macOS Player")]
        public static void BuildMacPlayer() => BuildPlayer(MacOS);

        private static void BuildPlayer(PlayerPlatform platform)
        {
            BuildTarget previousTarget = EditorUserBuildSettings.activeBuildTarget;
            StandaloneBuildSubtarget previousSubtarget =
                EditorUserBuildSettings.standaloneBuildSubtarget;

            string[] previousDefines = null;
            ScriptingImplementation? previousBackend = null;
            bool succeeded;

            try
            {
                succeeded = Build(platform, ref previousDefines, ref previousBackend);
            }
            catch (Exception ex)
            {
                // A throw inside a batch build otherwise surfaces as a zero exit with the stack
                // buried in the log, which reads to automation as success.
                Fail($"build threw: {ex}");
                succeeded = false;
            }
            finally
            {
                RestoreBackend(previousBackend);
                RestoreDefines(previousDefines);
                if (!Application.isBatchMode || platform.RestoreTargetInBatchMode)
                {
                    RestoreBuildTarget(previousTarget, previousSubtarget);
                }
            }

            // Outside the try, after the finally: EditorApplication.Exit terminates without
            // unwinding, so an exit inside the try would skip the restore and leave
            // ProjectSettings.asset — a committed file — with the define stripped.
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool Build(
            PlayerPlatform platform,
            ref string[] previousDefines,
            ref ScriptingImplementation? previousBackend)
        {
            bool release = HasFlag(ReleaseArgument);
            string outputDirectory = ResolveOutputDirectory(platform);
            Directory.CreateDirectory(outputDirectory);

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Fail($"no scenes are enabled in Build Settings — the {platform.Name} player would "
                     + "have no map to load.");
                return false;
            }

            string playerPath = Path.Combine(outputDirectory, platform.PlayerFileName);

            // Switch the platform BEFORE the subtarget: standaloneBuildSubtarget applies to
            // whichever standalone platform is active, and BuildPlayer against a non-active
            // target performs the switch mid-build, triggering a reimport inside a batch run.
            if (EditorUserBuildSettings.activeBuildTarget != platform.Target
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(
                       BuildTargetGroup.Standalone, platform.Target))
            {
                Fail($"could not switch the active build target to {platform.Target} — the "
                     + $"{platform.Module} module is most likely not installed for this Editor "
                     + "version. Add it through Unity Hub (Installs -> Manage -> Manage modules).");
                return false;
            }

            // Player, not Server: these processes need a framebuffer. The one launched with
            // -batchmode -nographics acts as the server, and LaneBHarness strips its client
            // half; a Server-subtarget build would define UNITY_SERVER and make every one of
            // the three rendered clients report LocalClient.Exists == false.
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;

            previousDefines = StripEditorOnlyDefines();

            // A define change QUEUES A RECOMPILE, and outside batchmode BuildPlayer refuses to
            // start while one is running. Batchmode never sees it because the compile completes
            // before the next statement runs; a live Editor does, every time, and the failure
            // arrived as "Error building Player because scripts are compiling" followed by this
            // class's own "build Unknown with 0 error(s)" — accurate and unreadable. Waiting
            // here cannot help: the compile needs Editor update ticks to progress and finishes
            // with a domain reload that would destroy this call's stack anyway.
            //
            // So the interactive path refuses, and says what to do. Making it work would take a
            // SessionState continuation across two domain reloads, which is a lot of machinery
            // for verification scaffolding whose batch path already works.
            if (previousDefines != null && !Application.isBatchMode)
            {
                Fail($"a live Editor cannot run this build: stripping {McpReadyDefine} queues a "
                     + "script recompile, and BuildPlayer refuses to start during one. Close "
                     + $"the Editor and run `pwsh tools/build-player.ps1 -Platform {platform.Name}` "
                     + "(-Development for the development player), which builds it in batchmode. "
                     + "The defines are restored either way.");
                return false;
            }

            ScriptingImplementation? backend = release ? platform.ReleaseBackend : platform.DevelopmentBackend;
            if (backend != null)
            {
                previousBackend = UseBackend(backend.Value);
            }

            if (platform.Target == BuildTarget.StandaloneOSX)
            {
                UseUniversalMacArchitecture();
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = playerPath,
                target = platform.Target,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = release ? BuildOptions.None : BuildOptions.Development,
                extraScriptingDefines = HasFlag(NoDiagnosticsArgument)
                    ? new[] { NoDiagnosticsDefine }
                    : null,
            };

            if (HasFlag(NoDiagnosticsArgument))
            {
                Debug.Log($"[build] {NoDiagnosticsDefine} set: Net/Diagnostics is compiled out.");
            }

            Debug.Log($"[build] {platform.Name} player ({Describe(release)}): {scenes.Length} scene(s) -> {playerPath}");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Fail($"build {summary.result} with {summary.totalErrors} error(s); see the "
                     + $"Unity log. Output: {playerPath}");
                return false;
            }

            // BuildResult.Succeeded does not mean a player was written — see
            // EditorBuild.VerifyOutput for the observed case where it was not.
            string missing = platform.Target == BuildTarget.StandaloneOSX
                ? FindMissingMacBundlePart(playerPath)
                : FindMissingWindowsPlayerPart(outputDirectory, playerPath);
            if (missing != null)
            {
                Fail($"build reported {summary.result} and {summary.totalSize} bytes, but {missing}; "
                     + "the player will not start.");
                return false;
            }

            Debug.Log($"[build] {platform.CompletionMarker} -> {playerPath} "
                      + $"({summary.totalSize} bytes, {summary.totalWarnings} warning(s), {Describe(release)})");
            return true;
        }

        private static string FindMissingWindowsPlayerPart(string outputDirectory, string executablePath)
        {
            if (!File.Exists(executablePath)) return $"nothing was written to {executablePath}";
            if (Directory.GetDirectories(outputDirectory, "*_Data").Length == 0)
            {
                return $"no *_Data folder was written beside the executable in {outputDirectory}";
            }
            return null;
        }

        // An .app is a folder. The two parts without which macOS cannot start it: the executable
        // Info.plist names, and the player data Unity reads from Contents/Resources/Data.
        private static string FindMissingMacBundlePart(string appPath)
        {
            string macOsFolder = Path.Combine(appPath, "Contents", "MacOS");
            if (!Directory.Exists(macOsFolder) || Directory.GetFiles(macOsFolder).Length == 0)
            {
                return $"{appPath} has no executable in Contents/MacOS";
            }
            if (!Directory.Exists(Path.Combine(appPath, "Contents", "Resources", "Data")))
            {
                return $"{appPath} has no Contents/Resources/Data";
            }
            return null;
        }

        private static string Describe(bool release)
            => (release ? "release, " : "development, ")
               + PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone);

        /// <summary>
        /// Sets the STANDALONE scripting backend for this build and returns the one it replaced,
        /// or null when nothing had to change.
        /// </summary>
        private static ScriptingImplementation? UseBackend(ScriptingImplementation backend)
        {
            var target = UnityEditor.Build.NamedBuildTarget.Standalone;
            ScriptingImplementation current = PlayerSettings.GetScriptingBackend(target);
            if (current == backend) return null;

            PlayerSettings.SetScriptingBackend(target, backend);
            Debug.Log($"[build] scripting backend {current} -> {backend} for this build");
            return current;
        }

        /// <summary>
        /// Builds the macOS player for Intel and Apple silicon in one universal binary.
        /// </summary>
        /// <remarks>
        /// Through reflection because <c>UnityEditor.OSXStandalone.UserBuildSettings</c> ships in
        /// the Mac Build Support module's own editor assembly: a direct reference would stop this
        /// whole Editor folder compiling on a machine without that module, which is every machine
        /// that never builds for macOS. Throws rather than building whatever the default happens to
        /// be — an Intel-only player starts on an M-series Mac only through Rosetta, and nothing
        /// downstream would notice. The setting lives in Library/, so nothing committed changes.
        /// </remarks>
        private static void UseUniversalMacArchitecture()
        {
            Type settings = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.OSXStandalone.UserBuildSettings", false))
                .FirstOrDefault(t => t != null);
            PropertyInfo architecture = settings?.GetProperty(
                "architecture", BindingFlags.Public | BindingFlags.Static);
            if (architecture == null || !architecture.CanWrite)
            {
                throw new InvalidOperationException(
                    "UnityEditor.OSXStandalone.UserBuildSettings.architecture was not found, so the "
                    + "macOS architecture cannot be set. Is the Mac Build Support (Mono) module installed?");
            }

            architecture.SetValue(null, Enum.Parse(architecture.PropertyType, UniversalMacArchitecture));
            object now = architecture.GetValue(null);
            if (!string.Equals(now.ToString(), UniversalMacArchitecture, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"set the macOS architecture to {UniversalMacArchitecture} and read back {now}.");
            }
            Debug.Log($"[build] macOS architecture: {now} (Intel + Apple silicon)");
        }

        // Saved explicitly for the reason RestoreDefines gives: BuildPlayer flushes project
        // settings mid-build, so an in-memory restore alone would leave the committed file on IL2CPP.
        private static void RestoreBackend(ScriptingImplementation? backend)
        {
            if (backend == null) return;

            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, backend.Value);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Removes <c>UNITY_MCP_READY</c> from the STANDALONE define set for this build.
        /// </summary>
        /// <remarks>
        /// <c>NamedBuildTarget.Standalone</c>, not <c>.Server</c>: the two keep separate define
        /// sets, and stripping the wrong one strips nothing while reporting that it did. Windows
        /// and macOS share the Standalone set.
        /// </remarks>
        private static string[] StripEditorOnlyDefines()
        {
            var target = UnityEditor.Build.NamedBuildTarget.Standalone;

            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);
            if (defines == null || !defines.Contains(McpReadyDefine)) return null;

            PlayerSettings.SetScriptingDefineSymbols(
                target, defines.Where(d => d != McpReadyDefine).ToArray());

            Debug.Log($"[build] stripped {McpReadyDefine} from the {target.TargetName} player "
                      + "defines for this build; the MCP integration is Editor-only");
            return defines;
        }

        private static void RestoreDefines(string[] defines)
        {
            if (defines == null) return;

            // The explicit save is not belt-and-braces: BuildPlayer flushes project settings
            // mid-build, so the stripped set reaches the committed ProjectSettings.asset and an
            // in-memory restore alone leaves the file dirty and wrong.
            PlayerSettings.SetScriptingDefineSymbols(
                UnityEditor.Build.NamedBuildTarget.Standalone, defines);
            AssetDatabase.SaveAssets();
        }

        private static void RestoreBuildTarget(BuildTarget target, StandaloneBuildSubtarget subtarget)
        {
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildPipeline.GetBuildTargetGroup(target), target);
            }

            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
        }

        /// <summary>True when <paramref name="flag"/> was passed on the command line.</summary>
        private static bool HasFlag(string flag)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, flag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ResolveOutputDirectory(PlayerPlatform platform)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], BuildOutputArgument, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return args[i + 1];
                }
            }

            // Anchored to the project folder, not to the working directory. An interactive build
            // from the menu inherits whatever CWD the Editor was launched with, and a batch run
            // that forgot -buildOutput would land the player somewhere the runner does not look
            // and report success. Application.dataPath is <project>/Assets, so two levels up is
            // the repo root -- the same build/ the Linux server build writes into.
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", platform.DefaultOutputDirectory));
        }

        private static void Fail(string message) => Debug.LogError($"[build] {message}");
    }
}
