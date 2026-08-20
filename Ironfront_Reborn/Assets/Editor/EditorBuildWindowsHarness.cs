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
    /// <b>This is harness scaffolding, not a shipping target.</b> The product's server is the
    /// Linux dedicated build and stays so; <see cref="EditorBuild"/> is untouched by this file.
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
            bool succeeded;

            try
            {
                succeeded = Build(ref previousDefines);
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
                RestoreDefines(previousDefines);
                if (!Application.isBatchMode) RestoreBuildTarget(previousTarget, previousSubtarget);
            }

            // Outside the try, after the finally: EditorApplication.Exit terminates without
            // unwinding, so an exit inside the try would skip the restore and leave
            // ProjectSettings.asset — a committed file — with the define stripped.
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool Build(ref string[] previousDefines)
        {
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

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = executablePath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development,
            };

            Debug.Log($"[build] lane-B windows player: {scenes.Length} scene(s) -> {executablePath}");

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
                      + $"({summary.totalSize} bytes, {summary.totalWarnings} warning(s))");
            return true;
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

            return Path.GetFullPath(DefaultOutputDirectory);
        }

        private static void Fail(string message) => Debug.LogError($"[build] {message}");
    }
}
