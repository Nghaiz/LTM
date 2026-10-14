using System;
using System.Collections.Generic;
using System.IO;
using Ironfront.MasterServer.Configuration;
using Ironfront.MasterServer.Diagnostics;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The master's log on disk: what fly's few-minute log buffer could not keep on 2026-10-14.
    /// </summary>
    /// <remarks>
    /// <see cref="LogFile"/> is process-wide, and other tests log while these run, so these assert
    /// that their own lines are present rather than that nothing else is.
    /// </remarks>
    [Collection(LogFileCollection.Name)]
    public sealed class LogFileTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ironfront-logfile-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            LogFile.Close();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public void TheLogAndTheEventStreamBothReachTheFile()
        {
            string path = Path.Combine(_directory, "master.log");
            LogFile.Open(path);
            bool wasEnabled = StructuredLog.Enabled;
            StructuredLog.Enabled = true;
            try
            {
                MasterLog.Error("log-file test: a line the console would have lost");
                StructuredLog.Event("error", new { code = 9001, msg = "log-file test event" });
            }
            finally
            {
                StructuredLog.Enabled = wasEnabled;
                LogFile.Close();
            }

            string written = ReadShared(path);
            Assert.Contains("[ERROR] log-file test: a line the console would have lost", written);
            Assert.Contains("\"type\":\"error\"", written);
            Assert.Contains("log-file test event", written);
        }

        [Fact]
        public void TheFileRotatesAtItsCapAndKeepsOnePrevious()
        {
            string path = Path.Combine(_directory, "master.log");
            LogFile.Open(path, maxBytes: 200);
            for (int i = 0; i < 10; i++) LogFile.Append($"line {i:D2} ......................................");
            LogFile.Close();

            Assert.True(File.Exists(path + ".1"), "nothing was rotated");
            Assert.True(new FileInfo(path).Length <= 200);
            Assert.True(new FileInfo(path + ".1").Length <= 200);
            Assert.Contains("line 09", ReadShared(path));
        }

        [Fact]
        public void AnEmptyPathLeavesItOff()
        {
            LogFile.Open("   ");

            Assert.False(LogFile.IsOpen);
        }

        [Fact]
        public void AFileThatCannotBeWrittenTurnsItselfOffWithoutThrowing()
        {
            string path = Path.Combine(_directory, "master.log");

            // The rotation target is a directory, so the move fails mid-write, as a full or
            // read-only volume would. Planted BEFORE the file opens: a line another test's
            // background thread logs meanwhile may be the write that rotates, and must fail too.
            Directory.CreateDirectory(path + ".1");
            LogFile.Open(path, maxBytes: 64);
            LogFile.Append(new string('x', 100));

            Assert.False(LogFile.IsOpen);
        }

        [Fact]
        public void ItIsSetByEnvironment()
        {
            MasterServerConfig config = MasterServerConfig.FromEnvironment(name => new Dictionary<string, string>
            {
                [MasterServerConfig.SharedSecretVariable] = Phase03ServerHarness.SharedSecret,
                [MasterServerConfig.LogFileVariable] = "/data/master.log",
            }.TryGetValue(name, out string? value) ? value : null);

            Assert.Equal("/data/master.log", config.LogFilePath);
        }

        private static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Runs the log-file tests one at a time: the file is process-wide.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class LogFileCollection
    {
        public const string Name = "LogFile";
    }
}
