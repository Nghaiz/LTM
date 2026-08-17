// D1.2 step B of C: run Ironfront.EditorBuild.BuildDedicatedServer inside the Editor that already
// holds the project lock, because tools/build-server.ps1 launches a second Unity on the same project
// and two Editors cannot open one.
//
// Step A already put the Editor on Linux64/Server, so the switch inside BuildDedicatedServer is
// skipped by its own activeBuildTarget guard and its finally restores Linux64/Server onto
// Linux64/Server — a no-op. That is what keeps a domain reload out of the middle of the build.
// Step C puts the Editor back on Windows64/Player from the marker step A wrote.
//
// The lock file is a DONE marker, not a mutex that gets released: the MCP plugin re-dispatches this
// snippet on its 10 s timeout about ten times, and a released lock would let the second dispatch
// start a second build the moment the first finished. Delete it from a shell to re-run on purpose.
//
// The build runs synchronously here, and the MCP call will time out while it does. That is fine:
// unity-run.py treats the result file as the source of truth precisely because the bridge's 10 s
// timeout is shorter than any real work. The first attempt deferred to EditorApplication.delayCall
// so the call could return, and the delegate was destroyed before it fired — step A's target switch
// changes the platform defines, Unity recompiled the scripts, and the domain reload that followed
// took the dynamic assembly holding that closure with it. Synchronous cannot be interrupted that
// way, and with the Editor already on Linux64/Server the build itself triggers no recompile.
string dir    = @"E:\WINDOW\Project\LTM\Ironfront_Reborn";
string lockF  = Path.Combine(dir, "harness-build.lock");
string rptF   = Path.Combine(dir, "harness-build.txt");

if (File.Exists(lockF))
{
    Out("build already started or finished in this session (" + lockF + ") — not starting another");
    Out("delete that file to re-run deliberately");
}
else if (Application.isPlaying)
{
    Out("REFUSING: Editor is in play mode");
}
else if (EditorApplication.isCompiling)
{
    Out("REFUSING: scripts are still compiling — a domain reload is pending");
}
else if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneLinux64
         || EditorUserBuildSettings.standaloneBuildSubtarget != StandaloneBuildSubtarget.Server)
{
    Out("REFUSING: expected Linux64/Server, found "
        + EditorUserBuildSettings.activeBuildTarget + "/"
        + EditorUserBuildSettings.standaloneBuildSubtarget + " — run step A first");
}
else
{
    File.WriteAllText(lockF, "started\n");
    if (File.Exists(rptF)) File.Delete(rptF);

    var log = new StringBuilder();
    try
    {
        log.AppendLine("target=" + EditorUserBuildSettings.activeBuildTarget
            + " subtarget=" + EditorUserBuildSettings.standaloneBuildSubtarget);
        log.AppendLine("calling Ironfront.EditorBuild.BuildDedicatedServer()");
        log.AppendLine();

        Ironfront.EditorBuild.BuildDedicatedServer();

        // BuildDedicatedServer keeps its BuildReport to itself and logs a summary, so the
        // evidence is the output tree. Enumerate it here rather than trusting the log.
        string outDir = Path.GetFullPath("build/server");
        log.AppendLine("output dir=" + outDir + " exists=" + Directory.Exists(outDir));

        if (Directory.Exists(outDir))
        {
            var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
            long total = 0;
            foreach (string f in files) total += new FileInfo(f).Length;
            log.AppendLine("files=" + files.Length + " totalBytes=" + total);

            string exe = Path.Combine(outDir, "Ironfront.Server.x86_64");
            log.AppendLine("Ironfront.Server.x86_64 exists=" + File.Exists(exe)
                + (File.Exists(exe) ? " bytes=" + new FileInfo(exe).Length : ""));

            log.AppendLine();
            log.AppendLine("top level:");
            foreach (string e in Directory.GetFileSystemEntries(outDir))
                log.AppendLine("  " + Path.GetFileName(e)
                    + (Directory.Exists(e) ? "/" : " (" + new FileInfo(e).Length + " b)"));
        }

        log.AppendLine();
        log.AppendLine("DONE");
    }
    catch (Exception ex)
    {
        log.AppendLine("EXCEPTION " + ex);
        log.AppendLine("DONE");
    }

    File.WriteAllText(rptF, log.ToString());
    Out("build finished; see Ironfront_Reborn/harness-build.txt");
}
