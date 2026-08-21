// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
//
// Nothing outside Assets/Scripts/Net/Diagnostics/ names a type from this folder: the ten
// mentions elsewhere are doc-comments, checked 2026-08-21. So this guard needs no companion
// guard at any call site, and a strip cannot leave a dangling reference behind it.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Ironfront.Net.Diagnostics
{
    /// <summary>
    /// Mirrors everything that reaches the Unity console into a timestamped file on disk, and
    /// prints an assembly census at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Nothing in this project wrote a log anywhere. The console is
    /// the only sink, the console is cleared on every Play, and a build has no console at all
    /// — so "no logs" was the accurate description of the state of the project rather than a
    /// symptom of a broken logger. Debugging a networked game from console scrollback is not
    /// possible: the interesting frame is always the one that scrolled past.
    /// </para>
    /// <para>
    /// <b>No wiring required, on purpose.</b> This starts from
    /// <see cref="RuntimeInitializeOnLoadMethodAttribute"/> rather than a component in a
    /// scene. A diagnostic that has to be attached to a GameObject to work is a diagnostic
    /// that is missing exactly when someone needs it — and "the component was not on the
    /// prefab" is the single most likely reason a report never appeared.
    /// </para>
    /// <para>
    /// <b>Where the file goes.</b> <see cref="Application.persistentDataPath"/>, never inside
    /// the repository: logs are not source, and a log directory under <c>Assets/</c> makes
    /// Unity import every line of it as an asset. The absolute path is printed to the console
    /// on the first line of every session so it can be copied out.
    /// </para>
    /// </remarks>
    public static class IronfrontLog
    {
        /// <summary>Sessions retained on disk. Older files are deleted at startup.</summary>
        private const int KeepNewestSessions = 10;

        /// <summary>
        /// Assemblies whose presence is worth reporting: ours, plus the four BCL shims
        /// <c>tools/build-libs.ps1</c> copies into <c>Plugins/</c>. Unity 6's .NET Standard
        /// profile already provides the latter, so a second copy in <c>Plugins/</c> is both
        /// redundant and a candidate cause of type-identity failures.
        /// </summary>
        private static readonly string[] WatchedAssemblies =
        {
            "Ironfront.Net.Protocol",
            "Ironfront.Net.Replication",
            "Ironfront.Net.Transport",
            "System.Memory",
            "System.Buffers",
            "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe",
        };

        private static readonly object Gate = new object();
        private static StreamWriter _writer;

        /// <summary>Absolute path of this session's log file, or null if none could be opened.</summary>
        public static string CurrentFile { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            if (_writer != null) return;
            if (!TryOpen()) return;

            // logMessageReceivedThreaded, not logMessageReceived: a socket receive loop that
            // logs from a worker thread is exactly the case this has to survive, and the
            // single-threaded event silently drops those messages.
            Application.logMessageReceivedThreaded += OnLog;
            Application.quitting += Shutdown;

            AttachTransportLog();

            WriteLine("INFO", $"session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} · Unity {Application.unityVersion} · {Application.platform}");
            WriteLine("INFO", $"timeScale={Time.timeScale} fixedDeltaTime={Time.fixedDeltaTime:F5} (project setting; see NetPredictionClock for why the netcode does not use it)");
            ReportAssemblies();

            Debug.Log($"[IronfrontLog] this session is being written to {CurrentFile}");
        }

        /// <summary>
        /// Gives <see cref="Ironfront.Net.Transport.NetLog"/> somewhere to write, for every
        /// build — client, dedicated server and Editor alike.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>These sinks were null in every shipped build.</b> The transport formats its
        /// warnings and hands them to an <c>Action&lt;string&gt;</c> that nothing ever
        /// assigned, so the two lines that name a reliable-channel death — <c>reliable
        /// sequence N abandoned after M resends</c> and <c>reliable sequence slot collision at
        /// N</c> — were composed and discarded on every run since the transport was written.
        /// <c>Connection.Update</c>'s own remark says it ends the connection <i>"loudly instead
        /// of continuing quietly"</i>; the loud half reached nobody, and a dropped client
        /// presented to everyone as a bare <c>TransportError</c> with no cause attached.
        /// </para>
        /// <para>
        /// The lane-B harness attached its own sink and that is how the cause was finally
        /// read — but a diagnostic only the test harness can see is not a diagnostic for the
        /// dedicated server, which is the process that will be running when it matters. It
        /// belongs here, beside the census, for the reason this whole file gives: anything
        /// that must be wired up by hand is missing exactly when someone needs it.
        /// </para>
        /// </remarks>
        private static void AttachTransportLog()
        {
            Ironfront.Net.Transport.NetLog.Warning ??= message => Debug.LogWarning($"[transport] {message}");
            Ironfront.Net.Transport.NetLog.Error ??= message => Debug.LogError($"[transport:error] {message}");
        }

        private static bool TryOpen()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Logs");
                Directory.CreateDirectory(dir);
                Prune(dir);

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                CurrentFile = Path.Combine(dir, $"ironfront-{stamp}.log");

                // FileShare.ReadWrite so the file can be tailed while the game is running,
                // and AutoFlush because the crash or the hang is precisely the session whose
                // last few lines matter — a buffered writer loses them.
                _writer = new StreamWriter(
                    new FileStream(CurrentFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                return true;
            }
            catch (Exception e)
            {
                // Never let a diagnostic take the game down. The console still works.
                Debug.LogWarning($"[IronfrontLog] could not open a log file ({e.GetType().Name}: {e.Message}). Console logging is unaffected.");
                CurrentFile = null;
                _writer = null;
                return false;
            }
        }

        private static void Prune(string dir)
        {
            try
            {
                FileInfo[] old = new DirectoryInfo(dir)
                    .GetFiles("ironfront-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(KeepNewestSessions)
                    .ToArray();

                foreach (FileInfo f in old) f.Delete();
            }
            catch (Exception)
            {
                // A locked or unreadable old log is not a reason to refuse to start a new one.
            }
        }

        /// <summary>
        /// Lists which watched assemblies actually loaded and from where, and flags any that
        /// loaded twice.
        /// </summary>
        /// <remarks>
        /// A duplicated <c>System.Memory</c> does not produce an error that names it. It
        /// produces a <c>TypeLoadException</c>, or a <c>Span&lt;byte&gt;</c> that is not
        /// assignable to a <c>Span&lt;byte&gt;</c>, and hours spent looking at the wrong file.
        /// Naming it on line three of every log is cheaper than diagnosing it once.
        /// </remarks>
        private static void ReportAssemblies()
        {
            Assembly[] loaded;
            try
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception e)
            {
                WriteLine("WARN", $"assembly census unavailable: {e.Message}");
                return;
            }

            var byName = new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly a in loaded)
            {
                string name = a.GetName().Name;
                if (Array.IndexOf(WatchedAssemblies, name) < 0) continue;

                if (!byName.TryGetValue(name, out List<Assembly> bucket))
                    byName[name] = bucket = new List<Assembly>();

                bucket.Add(a);
            }

            foreach (string watched in WatchedAssemblies)
            {
                if (!byName.TryGetValue(watched, out List<Assembly> found))
                {
                    WriteLine("INFO", $"assembly {watched}: not loaded");
                    continue;
                }

                foreach (Assembly a in found)
                    WriteLine("INFO", $"assembly {watched}: {a.GetName().Version} from {Location(a)}");

                if (found.Count > 1)
                {
                    string message =
                        $"[IronfrontLog] {watched} is loaded {found.Count} times. Unity 6's .NET Standard " +
                        "profile already provides the BCL shims, so the copies under Assets/Plugins are " +
                        "redundant; two copies of one assembly produce TypeLoadException and type-identity " +
                        "failures that never name the assembly involved. Delete the Plugins copy.";

                    // Debug.LogError, not WriteLine: the handler is already subscribed at this
                    // point, so this reaches the file anyway — and it also reaches the Console,
                    // which is where someone staring at a TypeLoadException is looking.
                    Debug.LogError(message);
                }
            }
        }

        private static string Location(Assembly a)
        {
            try
            {
                return string.IsNullOrEmpty(a.Location) ? "<dynamic or embedded>" : a.Location;
            }
            catch (Exception)
            {
                // IL2CPP and some sandboxes throw rather than returning an empty string.
                return "<unavailable>";
            }
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            bool wantStack = type == LogType.Exception || type == LogType.Error || type == LogType.Assert;
            WriteLine(type.ToString().ToUpperInvariant(), message, wantStack ? stackTrace : null);
        }

        private static void WriteLine(string level, string message, string stackTrace = null)
        {
            // Never call Debug.Log from in here: this runs inside the log callback and would
            // re-enter itself.
            lock (Gate)
            {
                if (_writer == null) return;

                try
                {
                    _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    _writer.Write(" [");
                    _writer.Write(level);
                    _writer.Write("] ");
                    _writer.WriteLine(message);

                    if (!string.IsNullOrEmpty(stackTrace))
                        _writer.WriteLine(stackTrace.TrimEnd());
                }
                catch (Exception)
                {
                    // Disk full, file deleted underneath us, handle closed during teardown.
                    // Drop the line rather than throwing out of a log callback.
                }
            }
        }

        private static void Shutdown()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.quitting -= Shutdown;

            lock (Gate)
            {
                if (_writer == null) return;

                try
                {
                    _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [INFO] session ended");
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch (Exception)
                {
                    // Teardown is not a place to raise.
                }

                _writer = null;
            }
        }
    }
}
#endif
