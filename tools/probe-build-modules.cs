// Why a Linux server build reported Succeeded, claimed 49 MB, and wrote nothing.
//
// BuildPipeline.BuildPlayer returned BuildResult.Succeeded with zero errors while the Editor log
// carried, from inside the same call:
//
//   UnityException: Build target 'StandaloneLinux64' not supported
//     at UnityEditor.PostprocessBuildPlayer.Postprocess (...)
//
// Postprocess is the step that installs the compiled player into the output directory, and it took
// 166 ms. So the player compiled and was never written, and the pipeline called that success.
//
// "Not supported" here does not mean the module is missing from disk. It means the build
// POST-PROCESSOR for the target could not be found, and that comes from the registry of platform
// support modules the Editor loaded at startup — not from the filesystem. BuildPipeline
// .IsBuildTargetSupported answers from the installed files and the licence, which is why it said
// True, and SwitchActiveBuildTarget succeeded for the same reason. The two states are
// indistinguishable through any public API, which is what made this silent.
//
// This probe reads the registry directly. GetBuildPostProcessor returning NULL for Linux while
// returning WinPlayerPostProcessor for Windows, with only UnityEditor.WindowsStandalone.Extensions
// in the domain, is the whole diagnosis: the module was installed after this Editor started.
// RegisterPlatformSupportModules is guarded ("already registered, not loading"), so there is no
// in-session recovery — the Editor has to restart.
var loaded = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetName().Name)
    .Where(n => n.IndexOf("Standalone", StringComparison.OrdinalIgnoreCase) >= 0
             || n.IndexOf("Linux", StringComparison.OrdinalIgnoreCase) >= 0)
    .OrderBy(n => n).ToList();
Out("=== platform extension assemblies in this domain (" + loaded.Count + ") ===");
foreach (string n in loaded) Out("  " + n);

Type mm = typeof(EditorApplication).Assembly.GetType("UnityEditor.Modules.ModuleManager");
var gp = mm.GetMethod("GetBuildPostProcessor",
    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
    null, new[] { typeof(BuildTarget) }, null);
var isLoaded = mm.GetMethod("IsPlatformSupportLoadedByBuildTarget",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

Out("");
Out("=== module registry, per target ===");
foreach (BuildTarget t in new[] { BuildTarget.StandaloneLinux64, BuildTarget.StandaloneWindows64 })
{
    object pp = gp.Invoke(null, new object[] { t });
    Out("  " + t
        + " supported(disk+licence)=" + BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, t)
        + " moduleLoaded=" + isLoaded.Invoke(null, new object[] { t })
        + " postProcessor=" + (pp == null ? "NULL" : pp.GetType().Name));
}

Out("");
Out("=== installed vs started ===");
string mod = @"E:\WINDOW\Unity Version\6000.3.21f1\Editor\Data\PlaybackEngines\LinuxStandaloneSupport";
Out("  LinuxStandaloneSupport exists=" + Directory.Exists(mod)
    + (Directory.Exists(mod)
        ? " created=" + Directory.GetCreationTime(mod).ToString("yyyy-MM-dd HH:mm:ss") : ""));
var proc = System.Diagnostics.Process.GetCurrentProcess();
Out("  this Editor started=" + proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss") + " pid=" + proc.Id);

Out("");
// Which player variation the build will need once the module is loaded. Both server variations ship
// in the module, so this decides which one, not whether one exists.
Out("Server scripting backend="
    + PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Server)
    + " development=" + EditorUserBuildSettings.development
    + "  => needs Variations/linux64_server_"
    + (EditorUserBuildSettings.development ? "development" : "nondevelopment")
    + (PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Server)
        == ScriptingImplementation.IL2CPP ? "_il2cpp" : "_mono"));
