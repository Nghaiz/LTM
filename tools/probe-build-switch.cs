// D1.2 step A of C: switch the Editor to Linux64 + Server, and nothing else.
//
// Split from the build itself deliberately. Switching the standalone target changes the platform
// defines (UNITY_STANDALONE_WIN -> UNITY_STANDALONE_LINUX, and UNITY_SERVER appears with the Server
// subtarget), so Unity recompiles the scripts and reloads the domain. A domain reload destroys this
// snippet's dynamic assembly mid-call — so if BuildPipeline.BuildPlayer were called after the switch
// in one snippet, it could be cut off partway. With the Editor already on Linux64 the switch inside
// EditorBuild.BuildDedicatedServer no-ops (it is guarded by an activeBuildTarget check), so step B
// runs the build with no reload in it at all.
//
// The marker is written BEFORE the switch for the same reason: the reload can prevent this method
// from ever reaching its own File.WriteAllText, and the MCP plugin re-dispatches on its 10 s timeout
// roughly ten times. Anything not idempotent gets run ten times.
string dir     = @"E:\WINDOW\Project\LTM\Ironfront_Reborn";
string markerF = Path.Combine(dir, "harness-build-target.txt");

var before = EditorUserBuildSettings.activeBuildTarget;
var beforeSub = EditorUserBuildSettings.standaloneBuildSubtarget;

if (before == BuildTarget.StandaloneLinux64
    && beforeSub == StandaloneBuildSubtarget.Server)
{
    Out("already on Linux64/Server — nothing to do");
}
else if (Application.isPlaying)
{
    Out("REFUSING: Editor is in play mode; a target switch here would tear down the play session");
}
else
{
    File.WriteAllText(markerF,
        "restore-to target=" + before + " subtarget=" + beforeSub + "\n");
    Out("recorded restore point: " + before + " / " + beforeSub + " -> " + markerF);

    bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(
        BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
    Out("SwitchActiveBuildTarget(Linux64)=" + ok);

    // Order matters: standaloneBuildSubtarget applies to whichever standalone platform is active,
    // which is the whole point of the D1.1 fix. Set it only after the platform has moved.
    if (ok)
    {
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
        Out("subtarget=" + EditorUserBuildSettings.standaloneBuildSubtarget);
    }

    Out("now active=" + EditorUserBuildSettings.activeBuildTarget
        + " sub=" + EditorUserBuildSettings.standaloneBuildSubtarget);
}
