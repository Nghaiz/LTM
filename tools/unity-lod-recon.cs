// Reconnaissance for the S5/A9 bot-LOD measurement. Answers, in play mode, whether the
// comparison is even measurable in this Editor before any prefab is touched:
//   - how many bots exist, and how many carry a BotLodGate
//   - whether the server tick loop is running and recording samples
//   - whether a human player is connected (BotLodScheduler gates on human interest, so with no
//     human every bot sits at InterestLevel.None and Scheduler-vs-AlwaysOn would compare two
//     different things than the shipping case does)
//   - which ProfilerRecorder markers are actually valid on this Unity version
using System.Text;
using Ironfront.Net.Unity.Server;
using Unity.Profiling;
using UnityEngine;

public static class ProbeLodRecon
{
    public static void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("isPlaying=" + Application.isPlaying
                      + " frame=" + Time.frameCount
                      + " realtime=" + Time.realtimeSinceStartup.ToString("F1") + "s");
        sb.AppendLine("targetFrameRate=" + Application.targetFrameRate
                      + " vSyncCount=" + QualitySettings.vSyncCount
                      + " fixedDeltaTime=" + Time.fixedDeltaTime.ToString("F4")
                      + " timeScale=" + Time.timeScale);

        // --- bots -------------------------------------------------------------------------------
        var actors = Object.FindObjectsByType<Actor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int ai = 0, human = 0, alive = 0;
        foreach (Actor a in actors)
        {
            if (a == null) continue;
            if (a.aiControlled) ai++; else human++;
            if (!a.dead) alive++;
        }
        sb.AppendLine("Actors total=" + actors.Length + " ai=" + ai + " human=" + human + " alive=" + alive);

        var gates = Object.FindObjectsByType<BotLodGate>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int allowed = 0;
        var modeCounts = new int[3];
        foreach (BotLodGate g in gates)
        {
            if (g == null) continue;
            if (g.AllowAiWork) allowed++;
            int m = (int)g.Mode;
            if (m >= 0 && m < 3) modeCounts[m]++;
        }
        sb.AppendLine("BotLodGate count=" + gates.Length
                      + " allowAiWork=" + allowed
                      + " modes[Scheduler=" + modeCounts[0] + " AlwaysOn=" + modeCounts[1]
                      + " AlwaysOff=" + modeCounts[2] + "]");

        var aiControllers = Object.FindObjectsByType<AiActorController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        sb.AppendLine("AiActorController count=" + aiControllers.Length);

        var netActors = Object.FindObjectsByType<NetServerActor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        sb.AppendLine("NetServerActor count=" + netActors.Length);
        sb.AppendLine("ServerActorRegistry.Count=" + ServerActorRegistry.Instance.Count);

        // --- server ------------------------------------------------------------------------------
        ServerTickLoop loop = ServerTickLoop.Current;
        if (loop == null)
        {
            sb.AppendLine("ServerTickLoop.Current = NULL  <-- no server, measurement not possible");
        }
        else
        {
            sb.AppendLine("ServerTickLoop currentTick=" + loop.CurrentTick + " players=" + loop.PlayerCount);
            sb.AppendLine("  TickTimes " + loop.Scheduler.TickTimes.Summary()
                          + " capacity=" + loop.Scheduler.TickTimes.Capacity);
            sb.AppendLine("  droppedTicks=" + loop.Scheduler.DroppedTicks
                          + " snapshotsDue=" + loop.Scheduler.SnapshotsDue
                          + " msPerTick=" + loop.Scheduler.MsPerTick.ToString("F2"));
            sb.AppendLine("  BotLod granted=" + loop.BotLod.TicksGranted
                          + " skipped=" + loop.BotLod.TicksSkipped
                          + " skippedPercent=" + loop.BotLod.SkippedPercent.ToString("F1"));
        }

        // --- profiler markers --------------------------------------------------------------------
        // Names are not guaranteed stable across Unity versions, so probe rather than assume.
        sb.AppendLine("--- ProfilerRecorder markers ---");
        foreach (string marker in new[]
                 {
                     "BehaviourUpdate", "FixedBehaviourUpdate", "PlayerLoop",
                     "Update.ScriptRunBehaviourUpdate", "FixedUpdate.ScriptRunBehaviourFixedUpdate",
                 })
        {
            ProfilerRecorder r = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, marker);
            sb.AppendLine("  Scripts/" + marker + " valid=" + r.Valid);
            r.Dispose();
        }
        foreach (string marker in new[] { "PlayerLoop", "Main Thread" })
        {
            ProfilerRecorder r = ProfilerRecorder.StartNew(ProfilerCategory.Internal, marker);
            sb.AppendLine("  Internal/" + marker + " valid=" + r.Valid);
            r.Dispose();
        }

        System.IO.File.WriteAllText("lod-recon.txt", sb.ToString());
    }
}
