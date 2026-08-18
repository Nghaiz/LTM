// Bot LOD comparison, checklist S5 / A9. Body for tools/unity-run.py.
//
// Three arms, INTERLEAVED in short blocks rather than run back to back. The first attempt ran
// Scheduler then AlwaysOn in sequence and reported Scheduler as the SLOWER arm, which is
// backwards -- the Editor was still settling from the script-execute compile during arm 0 and the
// A* caches were colder. Interleaving cancels any drift that is a function of wall-clock rather
// than of the arm.
//
// AI cost is summed over the nine real deep-profile markers, not the invented
// "AiActorController.Update()" name: Recorder.Get returns isValid == true for a sampler that does
// not exist, so the first run's AI column read 0.000 for both arms and meant nothing. Eight of the
// nine AiWorkAllowed guards are in coroutines, whose time is not in BehaviourUpdate at all.
string dir   = @"E:\WINDOW\Project\LTM\Ironfront_Reborn";
string lockF = Path.Combine(dir, "harness-profile.lock");
string rptF  = Path.Combine(dir, "harness-profile.txt");

if (File.Exists(lockF))
{
    Out("a run is already in flight; not stacking another hook");
}
else if (Ironfront.Net.Unity.Server.ServerTickLoop.Current == null)
{
    Out("no ServerTickLoop.Current -- nothing to measure");
}
else
{
    File.WriteAllText(lockF, "1");
    if (File.Exists(rptF)) File.Delete(rptF);

    UnityEngine.Profiling.Profiler.enabled = true;

    const string P = "Assembly-CSharp.dll!::AiActorController.";
    string[] aiMarkers = {
        P + "Update() [Invoke]",
        P + "AiBlocked() [Coroutine: MoveNext] [Invoke]",
        P + "AiOrders() [Coroutine: MoveNext] [Invoke]",
        P + "AiScan() [Coroutine: MoveNext] [Invoke]",
        P + "AiTarget() [Coroutine: MoveNext] [Invoke]",
        P + "AiTrack() [Coroutine: MoveNext] [Invoke]",
        P + "AiTrackClosestActors() [Coroutine: MoveNext] [Invoke]",
        P + "AiVehicle() [Coroutine: MoveNext] [Invoke]",
        P + "AiWeapon() [Coroutine: MoveNext] [Invoke]",
    };

    var aiRec = new UnityEngine.Profiling.Recorder[aiMarkers.Length];
    for (int i = 0; i < aiMarkers.Length; i++)
    {
        aiRec[i] = UnityEngine.Profiling.Recorder.Get(aiMarkers[i]);
        aiRec[i].enabled = true;
    }
    var recBeh = UnityEngine.Profiling.Recorder.Get("BehaviourUpdate");
    recBeh.enabled = true;

    string[] armName = { "Scheduler (shipping)", "AlwaysOn (LOD off)", "AlwaysOff (floor)" };
    string[] armMode = { "Scheduler", "AlwaysOn", "AlwaysOff" };
    const int ARMS = 3, SETTLE = 8, BLOCK = 32, ROUNDS = 15;

    var aiMs   = new List<double>[ARMS];
    var behMs  = new List<double>[ARMS];
    var tickMs = new List<double>[ARMS];
    var aiCalls = new List<double>[ARMS];
    for (int a = 0; a < ARMS; a++)
    {
        aiMs[a] = new List<double>();
        behMs[a] = new List<double>();
        tickMs[a] = new List<double>();
        aiCalls[a] = new List<double>();
    }
    var granted = new long[ARMS];
    var skipped = new long[ARMS];

    int arm = 0, round = 0, frameInBlock = 0;
    long g0 = 0, s0 = 0;
    uint lastTick = 0;
    var log = new StringBuilder();

    Func<object, string, object> refl = (o, n) =>
    {
        if (o == null) return null;
        Type t = o.GetType();
        var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) return p.GetValue(o);
        var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f != null ? f.GetValue(o) : null;
    };

    Func<List<double>, List<double>> sortedCopy = (raw) =>
    {
        var s = new List<double>(raw);
        s.Sort();
        return s;
    };

    Func<List<double>, double, double> pct = (sorted, q) =>
        sorted.Count == 0 ? 0.0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];

    Func<List<double>, string> stat = (raw) =>
    {
        if (raw.Count == 0) return "no samples";
        var s = new List<double>(raw);
        s.Sort();
        double sum = 0;
        for (int i = 0; i < s.Count; i++) sum += s[i];
        return "n=" + s.Count
             + " mean=" + (sum / s.Count).ToString("F3")
             + " p50=" + pct(s, 0.50).ToString("F3")
             + " p95=" + pct(s, 0.95).ToString("F3")
             + " p99=" + pct(s, 0.99).ToString("F3")
             + " max=" + s[s.Count - 1].ToString("F3");
    };

    EditorApplication.CallbackFunction hook = null;
    hook = () =>
    {
        try
        {
            var loop = Ironfront.Net.Unity.Server.ServerTickLoop.Current;
            if (loop == null) return;

            object bl = refl(loop, "BotLod");
            object sch = refl(loop, "Scheduler");

            if (frameInBlock == 0)
            {
                NetVerificationHarness.SetBotLod(armMode[arm]);
                g0 = Convert.ToInt64(refl(bl, "TicksGranted"));
                s0 = Convert.ToInt64(refl(bl, "TicksSkipped"));
                lastTick = 0;
            }

            frameInBlock++;

            // The gate decides in its own Update at order -100 and the coroutines notice on their
            // next wake, so the frames right after a switch are a mixture of both arms.
            if (frameInBlock <= SETTLE) return;

            double ns = 0;
            double calls = 0;
            for (int i = 0; i < aiRec.Length; i++)
            {
                ns += aiRec[i].elapsedNanoseconds;
                calls += aiRec[i].sampleBlockCount;
            }
            aiMs[arm].Add(ns / 1e6);
            aiCalls[arm].Add(calls);
            behMs[arm].Add(recBeh.elapsedNanoseconds / 1e6);

            // One sample per NEW server tick: Update runs at the render rate and the tick is
            // 30 Hz, so sampling per frame enters the same tick twice and flattens the tail.
            uint t = loop.CurrentTick;
            if (t != lastTick)
            {
                lastTick = t;
                object st = refl(sch, "TickTimes");
                object last = refl(st, "LastMs");
                if (last == null) last = refl(st, "Last");
                if (last != null) tickMs[arm].Add(Convert.ToDouble(last));
            }

            if (frameInBlock < SETTLE + BLOCK) return;

            granted[arm] += Convert.ToInt64(refl(bl, "TicksGranted")) - g0;
            skipped[arm] += Convert.ToInt64(refl(bl, "TicksSkipped")) - s0;

            frameInBlock = 0;
            arm++;
            if (arm < ARMS) return;

            arm = 0;
            round++;
            if (round < ROUNDS) return;

            // ---- done: report ----
            var gates = UnityEngine.Object.FindObjectsByType(
                typeof(Ironfront.Net.Unity.Server.BotLodGate),
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var ctrl = UnityEngine.Object.FindObjectsByType(
                typeof(AiActorController), FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            log.AppendLine("gates=" + gates.Length + " activeAiControllers=" + ctrl.Length
                + " actorsRegistered=" + Ironfront.Net.Unity.Server.ServerActorRegistry.Instance.Count
                + " players=" + refl(loop, "PlayerCount"));
            log.AppendLine("design: " + ARMS + " arms interleaved, " + ROUNDS + " rounds x ("
                + SETTLE + " settle + " + BLOCK + " sampled) frames per arm");
            log.AppendLine("aiCost = sum over 9 deep-profile markers (Update + 8 coroutine MoveNext)");
            log.AppendLine();

            for (int a = 0; a < ARMS; a++)
            {
                log.AppendLine("=== " + armMode[a] + " : " + armName[a] + " ===");
                log.AppendLine("  AI ms/frame          : " + stat(aiMs[a]));
                log.AppendLine("  AI marker calls/frame: " + stat(aiCalls[a]));
                log.AppendLine("  all script Update ms : " + stat(behMs[a]));
                log.AppendLine("  server tick ms       : " + stat(tickMs[a]));
                long tot = granted[a] + skipped[a];
                log.AppendLine("  botLod granted=" + granted[a] + " skipped=" + skipped[a]
                    + " skipped%=" + (tot == 0 ? "n/a (gate returns before ShouldTick in this mode)"
                                              : (100.0 * skipped[a] / tot).ToString("F1")));
                log.AppendLine();
            }

            Func<List<double>, double> mean = (raw) =>
            {
                if (raw.Count == 0) return 0;
                double s = 0;
                for (int i = 0; i < raw.Count; i++) s += raw[i];
                return s / raw.Count;
            };
            double onAi = mean(aiMs[1]), schedAi = mean(aiMs[0]), offAi = mean(aiMs[2]);
            log.AppendLine("AI cost above the AlwaysOff floor (mean ms/frame):");
            log.AppendLine("  AlwaysOn  " + (onAi - offAi).ToString("F3")
                + "   Scheduler " + (schedAi - offAi).ToString("F3")
                + "   saving " + (onAi <= offAi ? "n/a"
                    : (100.0 * (onAi - schedAi) / (onAi - offAi)).ToString("F1") + "%"));
            log.AppendLine("p99 AI ms/frame: AlwaysOn " + pct(sortedCopy(aiMs[1]), 0.99).ToString("F3")
                + " | Scheduler " + pct(sortedCopy(aiMs[0]), 0.99).ToString("F3")
                + " | AlwaysOff " + pct(sortedCopy(aiMs[2]), 0.99).ToString("F3"));
            log.AppendLine("p99 server tick ms: AlwaysOn " + pct(sortedCopy(tickMs[1]), 0.99).ToString("F3")
                + " | Scheduler " + pct(sortedCopy(tickMs[0]), 0.99).ToString("F3")
                + " | AlwaysOff " + pct(sortedCopy(tickMs[2]), 0.99).ToString("F3"));

            for (int i = 0; i < aiRec.Length; i++) aiRec[i].enabled = false;
            recBeh.enabled = false;
            UnityEngine.Profiling.Profiler.enabled = false;
            log.AppendLine();
            log.AppendLine(NetVerificationHarness.SetBotLod("Scheduler"));
            log.AppendLine("DONE");

            EditorApplication.update -= hook;
            File.WriteAllText(rptF, log.ToString());
            if (File.Exists(lockF)) File.Delete(lockF);
        }
        catch (Exception ex)
        {
            EditorApplication.update -= hook;
            try { NetVerificationHarness.SetBotLod("Scheduler"); }
            catch (Exception restore) { Debug.LogWarning("[probe] restore failed: " + restore.Message); }
            File.WriteAllText(rptF, "HOOK EXCEPTION " + ex);
            if (File.Exists(lockF)) File.Delete(lockF);
        }
    };

    EditorApplication.update += hook;
    Out("installed: " + ARMS + " arms x " + ROUNDS + " rounds x " + (SETTLE + BLOCK) + " frames = "
        + (ARMS * ROUNDS * (SETTLE + BLOCK)) + " frames total");
    Out("9 AI markers wired; report lands in Ironfront_Reborn/harness-profile.txt");
}
