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

        // Editor-only tooling that the MCP package makes eligible for a player build. See
        // StripEditorOnlyDefines for why this has to be removed for the duration of the build.
        private const string McpReadyDefine = "UNITY_MCP_READY";

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

            // Passed by reference rather than returned: Build strips these before the build and can
            // throw afterwards, and the finally below has to be able to put them back either way.
            string[] previousDefines = null;
            bool succeeded;

            try
            {
                succeeded = Build(ref previousDefines);
            }
            catch (Exception ex)
            {
                // A throw inside a batch build otherwise surfaces as a zero exit with a stack
                // trace buried in the log, which reads to automation as success.
                Fail($"build threw: {ex}");
                succeeded = false;
            }
            finally
            {
                // Restored unconditionally, batch mode included: these defines are persisted in
                // ProjectSettings.asset, which is a committed file, so leaving them stripped would
                // hand every server build a dirty working tree and eventually commit a project in
                // which the Editor's own MCP integration no longer compiles.
                RestoreDefines(previousDefines);

                // The build target, by contrast, lives in Library and is machine-local, and putting
                // it back costs a full reimport. An interactive run shares this Editor with the rest
                // of your day and must have it — leaving the Editor on Linux/Server means the next
                // Play or the next client build silently uses a headless-server subtarget. A batch
                // run is about to exit, so it would pay for that reimport and throw it away.
                if (!Application.isBatchMode) RestoreBuildTarget(previousTarget, previousSubtarget);
            }

            // Exiting happens here, after the finally and outside the try, because
            // EditorApplication.Exit terminates the process without unwinding the managed stack.
            // Called from inside the try — as both the success path and Fail used to do — it skips
            // the finally entirely, so every batch build left UNITY_MCP_READY stripped out of
            // ProjectSettings.asset. Nothing above this line may exit the process.
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        /// <summary>
        /// The build itself. Returns <see langword="false"/> after reporting the reason; never exits
        /// the process, so that <see cref="BuildDedicatedServer"/>'s restore always runs.
        /// </summary>
        private static bool Build(ref string[] previousDefines)
        {
            string outputDirectory = ResolveOutputDirectory();
            Directory.CreateDirectory(outputDirectory);

            string[] scenes = EnabledScenePaths();
            if (scenes.Length == 0)
            {
                Fail("no scenes are enabled in Build Settings — a dedicated server build " +
                     "would have nothing to run.");
                return false;
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
                return false;
            }

            // Set the subtarget on EditorUserBuildSettings as well as on the options: the
            // options field is what BuildPlayer honours, and the persisted setting keeps a
            // later interactive build from silently reverting to the Player subtarget.
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;

            previousDefines = StripEditorOnlyDefines();

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
                return false;
            }

            if (!VerifyOutput(executablePath, outputDirectory, out string problem))
            {
                Fail($"build reported {summary.result} and {summary.totalSize} bytes, but " +
                     $"{problem}. The player was compiled and then not installed: look for " +
                     "\"not supported\" thrown out of PostprocessBuildPlayer.Postprocess in " +
                     "the Editor log. If the platform module was installed while this Editor " +
                     "was already running, restart it — module registration happens once, at " +
                     "startup.");
                return false;
            }

            Debug.Log($"[build] dedicated server complete -> {executablePath} " +
                      $"({summary.totalSize} bytes, {summary.totalWarnings} warning(s))");
            return true;
        }

        /// <summary>
        /// Removes <c>UNITY_MCP_READY</c> from the server player's define set for the duration of
        /// the build, returning the previous set for <see cref="RestoreDefines"/>, or
        /// <see langword="null"/> if nothing needed changing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this the dedicated server build fails with 1284 compiler errors, every one of
        /// them inside the MCP package and none of them in this project. The chain:
        /// </para>
        /// <list type="number">
        /// <item>The MCP package's dependency resolver writes <c>UNITY_MCP_READY</c> into
        /// <em>every</em> platform's <c>scriptingDefineSymbols</c> — Server and Standalone
        /// included, not just the Editor.</item>
        /// <item><c>com.IvanMurzak.Unity.MCP.Runtime.asmdef</c> gates on that define and declares
        /// <c>includePlatforms: []</c>, i.e. all platforms. With the define set, it compiles into
        /// the player.</item>
        /// <item>Every DLL it lists in <c>precompiledReferences</c> — <c>McpPlugin.dll</c>,
        /// <c>ReflectorNet.dll</c>, <c>R3.dll</c>, <c>Microsoft.Extensions.*</c>, SignalR — carries
        /// <c>defineConstraints: [UNITY_EDITOR]</c> in its own <c>.meta</c>, so in a player build
        /// the assembly compiles with none of its references present.</item>
        /// </list>
        /// <para>
        /// The package's own asmdef is in <c>Library/PackageCache</c>, regenerated on every package
        /// resolve, so it cannot be fixed there. Removing the define permanently is not an option
        /// either: the Editor compiles with the active platform's define set, so that would stop
        /// the MCP integration working in the Editor, which is the only place it is wanted.
        /// Stripping it for the build is what is left.
        /// </para>
        /// <para>
        /// Removing it also settles a shipping question rather than only a compile one. With the
        /// define gone the MCP assembly is excluded from the build outright, so the plugin and its
        /// NuGet closure — SignalR, <c>Microsoft.Extensions.*</c>, and the Roslyn
        /// <c>Microsoft.CodeAnalysis.dll</c> — cannot reach the server image. An editor automation
        /// bridge that speaks HTTP and compiles arbitrary C# has no business in a public game
        /// server.
        /// </para>
        /// </remarks>
        private static string[] StripEditorOnlyDefines()
        {
            // NamedBuildTarget.Server, not .Standalone: the two keep separate define sets and the
            // Server subtarget compiles against this one. NamedBuildTarget.FromBuildTargetGroup
            // ignores the subtarget and would hand back Standalone, silently stripping nothing.
            var target = UnityEditor.Build.NamedBuildTarget.Server;

            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);
            if (defines == null || !defines.Contains(McpReadyDefine)) return null;

            PlayerSettings.SetScriptingDefineSymbols(
                target, defines.Where(d => d != McpReadyDefine).ToArray());

            Debug.Log($"[build] stripped {McpReadyDefine} from the {target.TargetName} player " +
                      "defines for this build; the MCP integration is Editor-only");
            return defines;
        }

        /// <summary>
        /// Puts back whatever <see cref="StripEditorOnlyDefines"/> removed. A no-op when it
        /// returned <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// The explicit save is not belt-and-braces. <see cref="BuildPipeline.BuildPlayer"/> flushes
        /// the project settings itself, so the stripped define set reaches
        /// <c>ProjectSettings.asset</c> — a committed file — partway through the build. Restoring the
        /// in-memory value therefore leaves the file dirty and wrong until something else happens to
        /// save; observed as a <c>Server:</c> with no defines in <c>git diff</c> after a build whose
        /// restore had demonstrably run.
        /// </remarks>
        private static void RestoreDefines(string[] defines)
        {
            if (defines == null) return;
            PlayerSettings.SetScriptingDefineSymbols(
                UnityEditor.Build.NamedBuildTarget.Server, defines);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Confirms the build actually wrote a player, because <see cref="BuildResult.Succeeded"/>
        /// does not mean it did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Observed here: a Linux server build returned <see cref="BuildResult.Succeeded"/> with a
        /// 49 MB <see cref="BuildSummary.totalSize"/> and zero errors, and wrote nothing at all —
        /// the output directory was empty afterwards. The Editor log carried
        /// <c>UnityException: Build target 'StandaloneLinux64' not supported</c> thrown out of
        /// <c>UnityEditor.PostprocessBuildPlayer.Postprocess</c>, the step that installs the
        /// compiled player into the output directory, and the pipeline reported success regardless.
        /// </para>
        /// <para>
        /// The cause in that instance was that the Linux Dedicated Server Build Support module had
        /// been installed while the Editor was already running. Unity registers platform support
        /// modules once, at startup, so the build post-processor for the target did not exist in
        /// that session — and nothing reachable through a public API said so beforehand:
        /// <see cref="BuildPipeline.IsBuildTargetSupported"/> answers from the installed files and
        /// the licence, so it reported the target supported, and
        /// <see cref="EditorUserBuildSettings.SwitchActiveBuildTarget"/> succeeded too.
        /// </para>
        /// <para>
        /// Which is why this is a post-condition on the output tree rather than a pre-flight check
        /// on the target: an output directory with no player in it is the one symptom every
        /// silent-write failure shares, whatever caused it. Without this check a batch run exits
        /// zero and the packaging step in <c>tools/build-server.ps1</c> tars up an empty directory,
        /// which then fails in the container, an hour later, as something else entirely.
        /// </para>
        /// </remarks>
        private static bool VerifyOutput(string executablePath, string outputDirectory,
                                         out string problem)
        {
            if (!File.Exists(executablePath))
            {
                problem = $"nothing was written to {executablePath}";
                return false;
            }

            // A player is an executable plus the data folder beside it, and the executable alone
            // will not start. Unity derives that folder's name from the executable's, and the
            // derivation differs across versions and platforms, so match the shape of the name
            // rather than reconstructing it.
            if (Directory.GetDirectories(outputDirectory, "*_Data").Length == 0)
            {
                problem = $"the executable exists but no *_Data folder was written beside it in " +
                          $"{outputDirectory}";
                return false;
            }

            problem = null;
            return true;
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

        /// <summary>
        /// Reports a build failure. Deliberately does not exit: the single exit lives at the end of
        /// <see cref="BuildDedicatedServer"/>, after the restore.
        /// </summary>
        private static void Fail(string message)
        {
            Debug.LogError($"[build] {message}");
        }
    }
}
