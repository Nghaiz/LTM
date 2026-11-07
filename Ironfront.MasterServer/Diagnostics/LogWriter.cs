using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// The one thread that writes log lines to the console and the log file. <see cref="MasterLog"/>
    /// and <see cref="StructuredLog"/> only queue them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the lines leave the caller's thread.</b> A console write blocks when nobody drains the
    /// pipe behind it, and the master logs from its single logic thread. On 2026-11-07 a Windows CI
    /// runner fell about 30 s behind reading a test host's stdout. The logic thread sat inside
    /// <c>Console.Out.WriteLine</c> for 21 s, between arming a room's countdown and telling the
    /// room, and the countdown never expired (#669). A docker log driver that falls behind would
    /// do the same to every player at once. Now only this thread can be held up by a slow stdout
    /// or a slow disk.
    /// </para>
    /// <para>
    /// <b>Bounded, and it drops rather than waits.</b> A queue without a limit trades the stall for
    /// memory, and a full queue that blocks brings the stall back. Past <see cref="Capacity"/>
    /// lines are dropped and counted, and the count is written as soon as the writer catches up.
    /// </para>
    /// <para>
    /// <b>The process must flush before it ends.</b> This is a background thread, so lines still
    /// queued when <c>Main</c> returns are lost unless something waits for them:
    /// <see cref="MasterLog.Flush"/> in <c>Program.Main</c>, on process exit, and in
    /// <see cref="LogFile.Close"/>.
    /// </para>
    /// </remarks>
    internal static class LogWriter
    {
        /// <summary>Lines held while the console is slow: about 2 MB at a 120-byte line.</summary>
        internal const int Capacity = 16_384;

        private static readonly TimeSpan ExitFlushTimeout = TimeSpan.FromSeconds(2);
        private static readonly BlockingCollection<Entry> Queue = new(new ConcurrentQueue<Entry>(), Capacity);
        private static readonly Thread Writer = StartWriter();
        private static long Enqueued;
        private static long Written;
        private static long Dropped;

        /// <summary>
        /// Queues <paramref name="line"/> for the console and the log file, and returns at once.
        /// </summary>
        /// <param name="console">Where the line goes on the console side. Null means whatever
        /// <see cref="Console.Out"/> is when the line is written.</param>
        /// <param name="line">The finished line, without a newline.</param>
        internal static void Enqueue(TextWriter? console, string line)
        {
            if (Queue.TryAdd(new Entry(console, line))) Interlocked.Increment(ref Enqueued);
            else Interlocked.Increment(ref Dropped);
        }

        /// <summary>
        /// Waits until every line queued before the call has been written, or until
        /// <paramref name="timeout"/> has passed.
        /// </summary>
        /// <returns>False if the timeout passed first.</returns>
        internal static bool Flush(TimeSpan timeout)
        {
            // The writer cannot wait for itself. Nothing calls this from there today; a future
            // caller on that thread gets a no-op instead of a deadlock.
            if (Thread.CurrentThread == Writer) return true;

            long target = Interlocked.Read(ref Enqueued);
            var clock = Stopwatch.StartNew();
            while (Interlocked.Read(ref Written) < target)
            {
                if (clock.Elapsed >= timeout) return false;
                Thread.Sleep(1);
            }

            return true;
        }

        private static Thread StartWriter()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "ironfront-log-writer" };
            thread.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(ExitFlushTimeout);
            return thread;
        }

        private static void Run()
        {
            foreach (Entry entry in Queue.GetConsumingEnumerable())
            {
                Write(entry.Console ?? Console.Out, entry.Line);

                long dropped = Interlocked.Exchange(ref Dropped, 0);
                if (dropped > 0)
                {
                    Write(Console.Out, MasterLog.Format(
                        "WARN ", $"{dropped} log line(s) dropped: the console or the log file fell behind"));
                }

                // Last, so a Flush that returns has seen the drop notice written too.
                Interlocked.Increment(ref Written);
            }
        }

        private static void Write(TextWriter console, string line)
        {
            try
            {
                console.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A closed or broken stdout must not end this thread: the file still gets the line,
                // and the file is told why the console did not.
                LogFile.Append(MasterLog.Format("ERROR", $"console write failed: {ex.Message}"));
            }

            LogFile.Append(line);
        }

        private readonly record struct Entry(TextWriter? Console, string Line);
    }
}
