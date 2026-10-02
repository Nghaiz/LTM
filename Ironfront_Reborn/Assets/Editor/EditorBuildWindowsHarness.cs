using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Ironfront
{
    /// <summary>
    /// Produces the Windows player that phase-3D lane B's runner launches four times: once
    /// headless as the server, three times rendered as scripted clients.
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
        // BuildOptions.Development and on IL2CPP, which compiles the game's C# to native code.
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

        private const string DefaultOutputDirectory = "build/windows";

        // Matched literally by tools/run-lane-b.ps1, so it is part of the contract.
        private const string ExecutableName = "Ironfront.exe";

        // Same Editor-only package define EditorBuild strips, for the same reason: with it set
        // the MCP runtime assembly compiles into the player against precompiled references that
        // are all constrained to UNITY_EDITOR. See EditorBuild.StripEditorOnlyDefines.
        private const string McpReadyDefine = "UNITY_MCP_READY";

        [MenuItem("Ironfront/Build Windows Player (lane-B harness)")]
        public static void BuildWindowsPlayer()
        {
            BuildTarget previousTarget = EditorUserBuildSettings.activeBuildTarget;
            StandaloneBuildSubtarget previousSubtarget =
                EditorUserBuildSettings.standaloneBuildSubtarget;

            string[] previousDefines = null;
            ScriptingImplementation? previousBackend = null;
            bool succeeded;

            try
            {
                succeeded = Build(ref previousDefines, ref previousBackend);
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
                if (!Application.isBatchMode) RestoreBuildTarget(previousTarget, previousSubtarget);
            }

            // Outside the try, after the finally: EditorApplication.Exit terminates without
            // unwinding, so an exit inside the try would skip the restore and leave
            // ProjectSettings.asset — a committed file — with the define stripped.
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool Build(ref string[] previousDefines, ref ScriptingImplementation? previousBackend)
        {
            bool release = HasFlag(ReleaseArgument);
            string outputDirectory = ResolveOutputDirectory();
            Directory.CreateDirectory(outputDirectory);

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Fail("no scenes are enabled in Build Settings — the lane-B player would have "
                     + "no map to load.");
                return false;
            }

            string executablePath = Path.Combine(outputDirectory, ExecutableName);

            // Switch the platform BEFORE the subtarget: standaloneBuildSubtarget applies to
            // whichever standalone platform is active, and BuildPlayer against a non-active
            // target performs the switch mid-build, triggering a reimport inside a batch run.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(
                       BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            {
                Fail("could not switch the active build target to StandaloneWindows64 — the "
                     + "Windows Build Support module is most likely not installed for this "
                     + "Editor version.");
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
                     + "the Editor and run `pwsh tools/run-lane-b.ps1 -Build`, which builds the "
                     + "same player in batchmode. The defines are restored either way.");
                return false;
            }

            if (release)
            {
                previousBackend = UseBackend(ScriptingImplementation.IL2CPP);
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = executablePath,
                target = BuildTarget.StandaloneWindows64,
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

            Debug.Log($"[build] lane-B windows player ({Describe(release)}): {scenes.Length} scene(s) -> {executablePath}");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Fail($"build {summary.result} with {summary.totalErrors} error(s); see the "
                     + $"Unity log. Output: {executablePath}");
                return false;
            }

            // BuildResult.Succeeded does not mean a player was written — see
            // EditorBuild.VerifyOutput for the observed case where it was not.
            if (!File.Exists(executablePath))
            {
                Fail($"build reported {summary.result} and {summary.totalSize} bytes, but "
                     + $"nothing was written to {executablePath}.");
                return false;
            }

            if (Directory.GetDirectories(outputDirectory, "*_Data").Length == 0)
            {
                Fail($"the executable exists but no *_Data folder was written beside it in "
                     + $"{outputDirectory}; the player will not start.");
                return false;
            }

            Debug.Log($"[build] lane-B windows player complete -> {executablePath} "
                      + $"({summary.totalSize} bytes, {summary.totalWarnings} warning(s), {Describe(release)})");
            return true;
        }

        private static string Describe(bool release)
            => release ? "release, IL2CPP" : "development, " + PlayerSettings.GetScriptingBackend(
                UnityEditor.Build.NamedBuildTarget.Standalone);

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
        /// sets, and stripping the wrong one strips nothing while reporting that it did.
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

        private static string ResolveOutputDirectory()
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
                Path.Combine(Application.dataPath, "..", "..", DefaultOutputDirectory));
        }

        private static void Fail(string message) => Debug.LogError($"[build] {message}");
    }
}
