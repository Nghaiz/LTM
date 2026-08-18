// D1.2 step C of C: put the Editor back where step A found it.
//
// The restore point is read from Ironfront_Reborn/harness-build-target.txt, which step A wrote
// BEFORE switching — the switch changes the platform defines, which recompiles the scripts, and the
// domain reload that follows can stop a snippet reaching its own tail.
//
// EditorBuild.BuildDedicatedServer restores the target itself on an interactive run, so this is only
// needed when the build was driven step-by-step through MCP (as it was here, because
// tools/build-server.ps1 launches a second Unity and two Editors cannot open one project).
string marker = @"E:\WINDOW\Project\LTM\Ironfront_Reborn\harness-build-target.txt";
Out("marker=" + marker + " exists=" + File.Exists(marker));
if (File.Exists(marker)) Out("  " + File.ReadAllText(marker).Trim());

// The strip is persisted by BuildPlayer partway through the build, so ProjectSettings.asset can be
// left holding an empty Server define set even when the in-memory restore ran. Check the file, not
// just the API.
string[] defs;
PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Server, out defs);
Out("Server defines (in memory)=" + (defs == null ? "<null>" : string.Join(";", defs)));

Out("current=" + EditorUserBuildSettings.activeBuildTarget
    + "/" + EditorUserBuildSettings.standaloneBuildSubtarget);

if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64
    && EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Player)
{
    Out("already restored — nothing to do");
}
else
{
    // Platform first, then subtarget: standaloneBuildSubtarget applies to whichever standalone
    // platform is active, so setting it before the switch writes it against Linux.
    Out("SwitchActiveBuildTarget(StandaloneWindows64)="
        + EditorUserBuildSettings.SwitchActiveBuildTarget(
            BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64));
    EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
    Out("subtarget set to Player; a reimport and domain reload follow");
}

// Flush the project settings so the on-disk file agrees with the Editor. Without this the working
// tree keeps a ProjectSettings.asset diff that nobody wrote on purpose.
AssetDatabase.SaveAssets();
Out("AssetDatabase.SaveAssets() issued");
