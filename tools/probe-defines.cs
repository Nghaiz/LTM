// The 429 build errors are all one thing, and none of them are ours.
//
// The MCP package's DependencyResolver writes UNITY_MCP_READY into EVERY platform's
// scriptingDefineSymbols, Server and Standalone included. That define is the defineConstraints gate
// on com.IvanMurzak.Unity.MCP.Runtime.asmdef, and that asmdef has includePlatforms: [] — so with the
// define present it compiles into the PLAYER. Its precompiledReferences (McpPlugin.dll,
// ReflectorNet.dll, R3.dll, Microsoft.Extensions.*, SignalR) all carry
// defineConstraints: [UNITY_EDITOR] in their own .meta, so in a player build the assembly compiles
// with none of its references present. Hence 1284 error lines from one assembly.
//
// The fix has to live in the build method: strip that define from the player's define set for the
// duration of BuildPlayer. Confirm the API shape and which NamedBuildTarget actually holds it before
// writing that.
Out("=== NamedBuildTarget define sets ===");
var targets = new[]
{
    UnityEditor.Build.NamedBuildTarget.Standalone,
    UnityEditor.Build.NamedBuildTarget.Server,
};
foreach (var t in targets)
{
    string[] defs;
    PlayerSettings.GetScriptingDefineSymbols(t, out defs);
    Out("  " + t.TargetName + ": " + (defs == null ? "<null>" : string.Join(";", defs)));
}

// Which one does the pipeline pick for Linux64 + Server? If this does not say Server, the strip has
// to be applied to whatever it does say.
var named = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Standalone);
Out("FromBuildTargetGroup(Standalone).TargetName=" + named.TargetName);
Out("activeBuildTarget=" + EditorUserBuildSettings.activeBuildTarget
    + " subtarget=" + EditorUserBuildSettings.standaloneBuildSubtarget);

// Sanity: is the runtime assembly really in the player set right now?
var player = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
    UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies);
foreach (var a in player)
    if (a.name.IndexOf("Mcp", StringComparison.OrdinalIgnoreCase) >= 0
        || a.name.IndexOf("IvanMurzak", StringComparison.OrdinalIgnoreCase) >= 0)
        Out("  PLAYER ASSEMBLY: " + a.name);
