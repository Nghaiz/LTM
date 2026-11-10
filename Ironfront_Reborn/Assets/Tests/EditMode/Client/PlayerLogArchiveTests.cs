using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Every session's log survives the next launch: Unity keeps one previous <c>Player.log</c>, and the
    /// owner lost the v4.6.5 playtest's logs to it (2026-11-10).
    /// </summary>
    public sealed class PlayerLogArchiveTests
    {
        private string _folder;

        [SetUp]
        public void MakeFolder()
        {
            _folder = Path.Combine(Path.GetTempPath(), "ironfront-log-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void RemoveFolder()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private string Current => Path.Combine(_folder, "Player.log");

        private string Previous => Path.Combine(_folder, "Player-prev.log");

        private string Sessions => Path.Combine(_folder, PlayerLogArchive.FolderName);

        private void WritePrevious(string text, DateTime ended)
        {
            File.WriteAllText(Previous, text);
            File.SetLastWriteTime(Previous, ended);
        }

        [Test]
        public void ThePreviousSessionIsKeptUnderWhenItEnded()
        {
            File.WriteAllText(Current, "this session");
            WritePrevious("the playtest", new DateTime(2026, 11, 9, 21, 43, 10));

            string kept = PlayerLogArchive.KeepPrevious(Current);

            Assert.AreEqual(Path.Combine(Sessions, "Player-2026-11-09_21-43-10.log"), kept);
            Assert.AreEqual("the playtest", File.ReadAllText(kept));
            Assert.IsTrue(File.Exists(Previous), "Unity's own Player-prev.log is copied, not moved");
        }

        /// <summary>The defect's shape: three launches in a row keep both earlier sessions, not one.</summary>
        [Test]
        public void ThreeLaunchesKeepEveryEarlierSession()
        {
            WritePrevious("match one", new DateTime(2026, 11, 9, 20, 0, 0));
            PlayerLogArchive.KeepPrevious(Current);
            WritePrevious("match two", new DateTime(2026, 11, 9, 21, 0, 0));
            PlayerLogArchive.KeepPrevious(Current);

            string[] kept = Directory.GetFiles(Sessions).Select(File.ReadAllText).OrderBy(t => t).ToArray();
            CollectionAssert.AreEqual(new[] { "match one", "match two" }, kept);
        }

        [Test]
        public void ASessionAlreadyKeptIsNotCopiedTwice()
        {
            WritePrevious("the playtest", new DateTime(2026, 11, 9, 21, 43, 10));
            PlayerLogArchive.KeepPrevious(Current);
            PlayerLogArchive.KeepPrevious(Current);

            Assert.AreEqual(1, Directory.GetFiles(Sessions).Length);
        }

        [Test]
        public void OnlyTheNewestAreKept()
        {
            for (int day = 1; day <= 5; day++)
            {
                WritePrevious("day " + day, new DateTime(2026, 11, day, 12, 0, 0));
                PlayerLogArchive.KeepPrevious(Current, keep: 3);
            }

            string[] names = Directory.GetFiles(Sessions).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(
                new[] { "Player-2026-11-03_12-00-00.log", "Player-2026-11-04_12-00-00.log", "Player-2026-11-05_12-00-00.log" },
                names);
        }

        [Test]
        public void NothingToKeepIsNotAnError()
        {
            Assert.IsNull(PlayerLogArchive.KeepPrevious(Current), "no Player-prev.log on a first launch");
            Assert.IsNull(PlayerLogArchive.KeepPrevious(string.Empty));
            Assert.IsNull(PlayerLogArchive.KeepPrevious("-"), "a server logging to stdout");

            File.WriteAllText(Previous, string.Empty);
            Assert.IsNull(PlayerLogArchive.KeepPrevious(Current), "an empty previous log is not a session");
        }
    }
}
