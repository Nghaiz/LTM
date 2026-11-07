using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Diagnostics;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A console that stops draining must not stop the master (#669).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a Windows CI runner the test host's stdout fell about 30 s behind. The master's logic
    /// thread sat in <c>Console.Out.WriteLine</c> for 21 s, right after arming a room's countdown,
    /// and the countdown never expired. These tests wedge stdout the same way and require the
    /// logging call to come back anyway.
    /// </para>
    /// <para>
    /// In the log-file collection because <see cref="Console.SetOut"/> is process-wide: no other
    /// test may be logging while stdout is wedged.
    /// </para>
    /// </remarks>
    [Collection(LogFileCollection.Name)]
    public sealed class LogWriterTests
    {
        private static readonly TimeSpan CallerBudget = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task ALogCallReturnsWhileNobodyIsReadingTheConsole()
        {
            string marker = Guid.NewGuid().ToString("N");
            using var wedge = new WedgedWriter();
            TextWriter original = Console.Out;
            Console.SetOut(wedge);
            bool returned;
            try
            {
                // The first line takes the writer into the wedge; the others queue behind it.
                Task logging = Task.Run(() =>
                {
                    MasterLog.Error($"{marker} first");
                    MasterLog.Error($"{marker} second");
                    MasterLog.Error($"{marker} third");
                });
                returned = await Task.WhenAny(logging, Task.Delay(CallerBudget)) == logging;
            }
            finally
            {
                wedge.Release();
                MasterLog.Flush(FlushBudget);
                Console.SetOut(original);
            }

            Assert.True(
                returned,
                "MasterLog.Error waited for a console nobody was reading. The master logs from its one "
                + "logic thread, so every player would have waited with it (#669).");

            string text = wedge.Text;
            int first = text.IndexOf($"{marker} first", StringComparison.Ordinal);
            int second = text.IndexOf($"{marker} second", StringComparison.Ordinal);
            int third = text.IndexOf($"{marker} third", StringComparison.Ordinal);
            Assert.True(
                first >= 0 && first < second && second < third,
                $"the lines must all arrive, in the order they were logged, once the console drains: {text}");
        }

        [Fact]
        public async Task LinesPastTheQueueAreDroppedAndTheDropIsReported()
        {
            string marker = Guid.NewGuid().ToString("N");
            using var wedge = new WedgedWriter();
            TextWriter original = Console.Out;
            Console.SetOut(wedge);
            bool returned;
            try
            {
                // More lines than the queue holds, while nothing can be written.
                Task logging = Task.Run(() =>
                {
                    for (int i = 0; i < LogWriter.Capacity + 100; i++) MasterLog.Error($"{marker} {i}");
                });
                returned = await Task.WhenAny(logging, Task.Delay(CallerBudget)) == logging;
            }
            finally
            {
                wedge.Release();
                MasterLog.Flush(FlushBudget);
                Console.SetOut(original);
            }

            Assert.True(returned, "a full log queue made the caller wait; it must drop the line instead (#669)");
            Assert.Contains(
                "log line(s) dropped: the console or the log file fell behind",
                wedge.Text,
                StringComparison.Ordinal);
        }

        /// <summary>A console that holds every write until <see cref="Release"/>.</summary>
        private sealed class WedgedWriter : TextWriter
        {
            // A cap, so a regression fails the test instead of hanging the run.
            private static readonly TimeSpan MaxWedge = TimeSpan.FromSeconds(30);

            private readonly ManualResetEventSlim _open = new ManualResetEventSlim(false);
            private readonly StringBuilder _text = new StringBuilder();

            public override Encoding Encoding => Encoding.UTF8;

            public string Text
            {
                get { lock (_text) return _text.ToString(); }
            }

            public void Release() => _open.Set();

            public override void Write(char value)
            {
                _open.Wait(MaxWedge);
                lock (_text) _text.Append(value);
            }

            public override void Write(string? value)
            {
                _open.Wait(MaxWedge);
                lock (_text) _text.Append(value);
            }

            public override void WriteLine(string? value)
            {
                _open.Wait(MaxWedge);
                lock (_text) _text.Append(value).Append('\n');
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _open.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
