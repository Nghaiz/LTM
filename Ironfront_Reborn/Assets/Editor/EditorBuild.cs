using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Ironfront
{
    /// <summary>
    /// The Editor-side half of <c>tools/build-server.ps1</c>: produces a Linux headless
    /// (dedicated-server) player from the command line so a container image can be built
    /// without a human driving the Editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: this method is the contract <c>tools/build-server.ps1</c> documents and calls —
    /// <c>-executeMethod Ironfront.EditorBuild.BuildDedicatedServer</c>. The script passes the
    /// output directory as <c>-buildOutput &lt;path&gt;</c>, which Unity forwards verbatim
    /// through <see cref="Environment.GetCommandLineArgs"/>; there is no other channel, because
    /// <c>-buildTarget</c> only switches the active target and never calls
    /// <see cref="BuildPipeline.BuildPlayer"/>.
    /// </para>
    /// <para>
    /// <b>It must exit the process itself.</b> Under <c>-batchmode</c> Unity does not fail the
    /// run just because a build failed — only an explicit non-zero
    /// <see cref="EditorApplication.Exit(int)"/> makes CI and the shipping Dockerfile stop
    /// rather than package an empty output directory. Interactive runs (the menu item) skip the
    /// exit so a failed build does not close the Editor from under you.
    /// </para>
    /// </remarks>
    public static class EditorBuild
    {
        private const string BuildOutputArgument = "-buildOutput";
        private const string DefaultOutputDirectory = "build/server";

        // The systemd unit and the Dockerfile entrypoint both invoke this exact name, so it is
        // part of the deployment contract rather than a local convenience.
        private const string ExecutableName = "Ironfront.Server.x86_64";

        [MenuItem("Ironfront/Build Dedicated Server (Linux)")]
        public static void BuildDedicatedServer()
        {
            // Captured before anything switches platforms so an interactive run can be handed
            // back the Editor it started with. See the restore in the finally below.
            BuildTarget previousTarget = EditorUserBuildSettings.activeBuildTarget;
            StandaloneBuildSubtarget previousSubtarget =
                EditorUserBuildSettings.standaloneBuildSubtarget;

            try
            {
                string outputDirectory = ResolveOutputDirectory();
                Directory.CreateDirectory(outputDirectory);

                string[] scenes = EnabledScenePaths();
                if (scenes.Length == 0)
                {
                    Fail("no scenes are enabled in Build Settings — a dedicated server build " +
                         "would have nothing to run.");
                    return;
                }

                string executablePath = Path.Combine(outputDirectory, ExecutableName);

                // Switch the active platform to Linux BEFORE touching the subtarget. Two
                // reasons, and both bit this script:
                //
                //  - standaloneBuildSubtarget applies to whichever standalone platform is
                //    currently active. On the documented Windows build host that was Windows,
                //    so the Server subtarget was written against the wrong platform and the
                //    Linux build kept the Player subtarget it already had.
                //  - BuildPlayer with a non-active target performs the switch itself, mid-build,
                //    triggering a full reimport inside a -quit batch run. Doing it here keeps
                //    the switch (and any failure to switch) observable in the log.
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneLinux64
                    && !EditorUserBuildSettings.SwitchActiveBuildTarget(
                           BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
                {
                    Fail("could not switch the active build target to StandaloneLinux64 — the " +
                         "Linux Dedicated Server Build Support module is most likely not " +
                         "installed for this Editor version.");
                    return;
                }

                // Set the subtarget on EditorUserBuildSettings as well as on the options: the
                // options field is what BuildPlayer honours, and the persisted setting keeps a
                // later interactive build from silently reverting to the Player subtarget.
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = executablePath,
                    target = BuildTarget.StandaloneLinux64,
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    options = BuildOptions.None,
                };

                Debug.Log($"[build] dedicated server: {scenes.Length} scene(s) -> {executablePath}");

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                if (summary.result != BuildResult.Succeeded)
                {
                    Fail($"build {summary.result} with {summary.totalErrors} error(s); " +
                         $"see the Unity log. Output: {executablePath}");
                    return;
                }

                Debug.Log($"[build] dedicated server complete -> {executablePath} " +
                          $"({summary.totalSize} bytes, {summary.totalWarnings} warning(s))");

                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                // A throw inside a batch build otherwise surfaces as a zero exit with a stack
                // trace buried in the log, which reads to automation as success.
                Fail($"build threw: {ex}");
            }
            finally
            {
                // Interactive runs share this Editor with the rest of your day, and both values
                // we changed are Editor-global: leaving them on Linux/Server means the next Play
                // or the next client build silently uses a headless-server subtarget. A batch run
                // is already exiting, so restoring there would only pay for a second reimport.
                if (!Application.isBatchMode) RestoreBuildTarget(previousTarget, previousSubtarget);
            }
        }

        /// <summary>
        /// Puts the Editor-global build target and standalone subtarget back the way an
        /// interactive build found them.
        /// </summary>
        private static void RestoreBuildTarget(BuildTarget target, StandaloneBuildSubtarget subtarget)
        {
            // Order matters: standaloneBuildSubtarget applies to the active standalone platform,
            // so the platform has to be back first for the subtarget to land where it came from.
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                // Not necessarily Standalone — the Editor may have been on Android or iOS.
                EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildPipeline.GetBuildTargetGroup(target), target);
            }

            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
        }

        /// <summary>
        /// Reads <c>-buildOutput</c> from the command line, falling back to a repo-relative
        /// default for an interactive build that passed no argument.
        /// </summary>
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

        private static string[] EnabledScenePaths()
            => EditorBuildSettings.scenes
                .Where(scene => scene.enabled && !string.IsNullOrEmpty(scene.path))
                .Select(scene => scene.path)
                .ToArray();

        private static void Fail(string message)
        {
            Debug.LogError($"[build] {message}");
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
