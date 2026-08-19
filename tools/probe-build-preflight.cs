// D1.2 pre-flight. Answers, without side effects, whether the Linux dedicated-server build can be
// driven from inside the already-open Editor instead of from a second Unity (which cannot open the
// same project — build-server.ps1 needs the project lock this Editor holds).
Out("isPlaying=" + Application.isPlaying
    + " isCompiling=" + EditorApplication.isCompiling
    + " compileFailed=" + EditorUtility.scriptCompilationFailed
    + " isBatchMode=" + Application.isBatchMode);

Out("cwd=" + Directory.GetCurrentDirectory());
Out("interactive -buildOutput fallback resolves to: " + Path.GetFullPath("build/server"));
Out("dataPath=" + Application.dataPath);

Out("activeBuildTarget=" + EditorUserBuildSettings.activeBuildTarget
    + " subtarget=" + EditorUserBuildSettings.standaloneBuildSubtarget);

// The one fact that decides whether the module install took. Fail(...) in EditorBuild.cs guards the
// switch, but knowing this up front is cheaper than a failed build.
Out("Linux64 supported=" + BuildPipeline.IsBuildTargetSupported(
        BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64));
Out("Windows64 supported=" + BuildPipeline.IsBuildTargetSupported(
        BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64));

var scenes = EditorBuildSettings.scenes;
int enabled = 0;
foreach (var s in scenes) if (s.enabled && !string.IsNullOrEmpty(s.path)) enabled++;
Out("build settings: " + scenes.Length + " scene(s), " + enabled + " enabled");
foreach (var s in scenes)
    Out("  " + (s.enabled ? "[x] " : "[ ] ") + s.path);

// The playback engine folder for the target; absent means the module is not installed for this
// Editor version even if the API above lies.
string editorDir = Path.GetDirectoryName(EditorApplication.applicationPath);
string linuxSupport = Path.Combine(editorDir, "Data", "PlaybackEngines", "LinuxStandaloneSupport");
Out("editor=" + EditorApplication.applicationPath);
Out("LinuxStandaloneSupport exists=" + Directory.Exists(linuxSupport) + " -> " + linuxSupport);

// EditorBuild.BuildDedicatedServer must still be there and must still be the contract the ps1 calls.
Type eb = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => { try { return a.GetType("Ironfront.EditorBuild"); } catch { return null; } })
    .FirstOrDefault(t => t != null);
Out("Ironfront.EditorBuild found=" + (eb != null)
    + " method=" + (eb != null && eb.GetMethod("BuildDedicatedServer",
        BindingFlags.Public | BindingFlags.Static) != null));
