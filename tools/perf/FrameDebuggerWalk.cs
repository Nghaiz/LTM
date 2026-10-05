// Walks every draw of one frame of a connected development player through Unity's Frame
// Debugger and writes tmp/perf/fd-walk.tsv: per event its draw-call count, instance count,
// vertices, shader, pass, mesh, batch-break cause and render target.
//
// Not compiled by any project: the Editor compiles it on demand through Unity MCP's
// script-execute. With the Editor open and a DEVELOPMENT player (build-player.ps1 -Development)
// in a match:
//   python tools/perf/unity_exec.py tools/perf/FrameDebuggerWalk.cs FdWalk Run
// then wait for the last line "# done" in tmp/perf/fd-walk.tsv and summarise it with
//   python tools/perf/fd_summary.py tmp/perf/fd-walk.tsv
//
// The player freezes on the captured frame while the walk runs (about a minute for 2,400
// events) and the game server may drop it back to the lobby. The walk switches the debugger off
// when it ends; `... FdWalk Stop` does the same at any time.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditorInternal;

public static class FdWalk
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string Output = @"tmp\perf\fd-walk.tsv";
    const string Causes = @"tmp\perf\fd-causes.txt";
    const int TimeoutTicks = 600;

    static Type util, dataType;
    static object data;
    static int index, end, waited;
    static StringBuilder rows;
    static string root;

    public static string Run()
    {
        // script-execute compiles every call into a fresh assembly, so a walker started earlier
        // is a different type with the same name: drop it, or two walkers fight over the limit.
        int dropped = DropWalkers();
        root = Directory.GetParent(UnityEngine.Application.dataPath).Parent.FullName;
        Directory.CreateDirectory(Path.Combine(root, @"tmp\perf"));
        var asm = typeof(EditorWindow).Assembly;
        util = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
        dataType = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");
        data = Activator.CreateInstance(dataType);

        int player = ProfilerDriver.connectedProfiler;
        if (!ProfilerDriver.GetConnectionIdentifier(player).Contains("Player"))
        {
            player = ProfilerDriver.GetAvailableProfilers().FirstOrDefault(id => ProfilerDriver.GetConnectionIdentifier(id).Contains("Player"));
            if (player == 0) return "no development player is visible to the Editor";
            ProfilerDriver.connectedProfiler = player;
        }
        util.GetMethod("SetEnabled", Any).Invoke(null, new object[] { true, player });
        File.WriteAllLines(Path.Combine(root, Causes), (string[])util.GetMethod("GetBatchBreakCauseStrings", Any).Invoke(null, null));
        index = 0; end = -1; waited = 0;
        rows = new StringBuilder("index\tdraws\tinstances\tverts\tshader\tpass\tlightmode\tmesh\tcause\ttarget\n");
        EditorApplication.update += Step;
        return $"walking {ProfilerDriver.GetConnectionIdentifier(player)}" + (dropped > 0 ? $" (dropped {dropped} earlier walker(s))" : "");
    }

    public static string Stop()
    {
        int dropped = DropWalkers();
        typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility")
            .GetMethod("SetEnabled", Any).Invoke(null, new object[] { false, ProfilerDriver.connectedProfiler });
        return $"frame debugger off; dropped {dropped} walker(s)";
    }

    static int DropWalkers()
    {
        if (EditorApplication.update == null) return 0;
        int dropped = 0;
        foreach (Delegate d in EditorApplication.update.GetInvocationList().Where(d => d.Method.DeclaringType?.Name == nameof(FdWalk)))
        {
            EditorApplication.update -= (EditorApplication.CallbackFunction)d;
            dropped++;
        }
        return dropped;
    }

    static void Step()
    {
        int count = (int)util.GetProperty("count", Any).GetValue(null);
        if (count == 0) { if (++waited > TimeoutTicks * 3) Finish("no events"); return; }
        if (end < 0) end = count;
        if (index >= end) { Finish("done"); return; }

        // A remote player sends one event's data at a time, for the event at the limit.
        util.GetProperty("limit", Any).SetValue(null, index + 1);
        bool ok = (bool)util.GetMethod("GetFrameEventData", Any).Invoke(null, new object[] { index, data });
        if (!ok || (int)dataType.GetField("m_FrameEventIndex", Any).GetValue(data) != index)
        {
            if (++waited > TimeoutTicks) { rows.Append(index).AppendLine("\tTIMEOUT"); index++; waited = 0; }
            return;
        }
        waited = 0;
        var mesh = dataType.GetField("m_Mesh", Any).GetValue(data) as UnityEngine.Mesh;
        rows.Append(index).Append('\t')
            .Append(F("m_DrawCallCount")).Append('\t').Append(F("m_InstanceCount")).Append('\t').Append(F("m_VertexCount")).Append('\t')
            .Append(F("m_RealShaderName")).Append('\t').Append(F("m_PassName")).Append('\t').Append(F("m_PassLightMode")).Append('\t')
            .Append(mesh != null ? mesh.name : "").Append('\t').Append(F("m_BatchBreakCause")).Append('\t')
            .Append(F("m_RenderTargetName")).Append('\n');
        if (++index % 100 == 0) File.WriteAllText(Path.Combine(root, Output), rows.ToString());
    }

    static string F(string field) => Convert.ToString(dataType.GetField(field, Any).GetValue(data))?.Replace('\t', ' ').Replace('\n', ' ');

    static void Finish(string why)
    {
        EditorApplication.update -= Step;
        util.GetMethod("SetEnabled", Any).Invoke(null, new object[] { false, ProfilerDriver.connectedProfiler });
        rows.AppendLine("# " + why);
        File.WriteAllText(Path.Combine(root, Output), rows.ToString());
    }
}
