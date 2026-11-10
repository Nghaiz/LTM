using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Keeps every play session's log: the previous session's <c>Player-prev.log</c> is copied into a
    /// <see cref="FolderName"/> folder beside it at startup, named by when that session ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> A release build logs only to Unity's <c>Player.log</c>, and Unity keeps
    /// one previous file: the second launch after a bad match overwrote the evidence (owner,
    /// 2026-11-10, after the v4.6.5 playtest: "every time you play it overwrites the old log, how is
    /// anyone supposed to debug that later"). <c>IronfrontLog</c> keeps sessions, but it is a
    /// diagnostic and is compiled out of the shipping client.
    /// </para>
    /// <para>
    /// <b>Copied, not moved,</b> so <c>Player.log</c> and <c>Player-prev.log</c> stay where Unity and
    /// every earlier instruction to players put them. Windows: <c>%USERPROFILE%\AppData\LocalLow\LTM10\IronfrontReborn</c>;
    /// macOS: <c>~/Library/Logs/LTM10/IronfrontReborn</c>; Linux: <c>~/.config/unity3d/LTM10/IronfrontReborn</c>.
    /// The macOS player writes its log only on a clean quit, so a session killed there leaves little
    /// to keep.
    /// </para>
    /// </remarks>
    public static class PlayerLogArchive
    {
        /// <summary>The folder, beside Unity's own log, that holds the kept sessions.</summary>
        public const string FolderName = "Sessions";

        /// <summary>Sessions kept; the oldest beyond this are deleted at startup.</summary>
        public const int KeepSessions = 100;

        private const string Prefix = "Player-";

        private const string Extension = ".log";

        private const string Stamp = "yyyy-MM-dd_HH-mm-ss";

        /// <summary>The name a session that ended at <paramref name="endedLocal"/> is kept under; names sort by time.</summary>
        public static string NameFor(DateTime endedLocal)
            => Prefix + endedLocal.ToString(Stamp, CultureInfo.InvariantCulture) + Extension;

        /// <summary>
        /// Copies the session before this one (<c>Player-prev.log</c> beside <paramref name="currentLog"/>)
        /// into <see cref="FolderName"/>, unless it is already there, and deletes the oldest kept beyond
        /// <paramref name="keep"/>.
        /// </summary>
        /// <returns>The kept copy's path, or null when there was no previous session to keep.</returns>
        public static string KeepPrevious(string currentLog, int keep = KeepSessions)
        {
            if (string.IsNullOrEmpty(currentLog) || !Path.IsPathRooted(currentLog)) return null;
            string folder = Path.GetDirectoryName(currentLog);
            if (string.IsNullOrEmpty(folder)) return null;

            string previous = Path.Combine(folder, Path.GetFileNameWithoutExtension(currentLog) + "-prev" + Path.GetExtension(currentLog));
            if (!File.Exists(previous) || new FileInfo(previous).Length == 0) return null;

            string sessions = Path.Combine(folder, FolderName);
            Directory.CreateDirectory(sessions);
            string kept = Path.Combine(sessions, NameFor(File.GetLastWriteTime(previous)));
            if (!File.Exists(kept))
            {
                File.Copy(previous, kept);
            }

            foreach (string old in ToDelete(Directory.GetFiles(sessions, Prefix + "*" + Extension), keep))
            {
                File.Delete(old);
            }
            return kept;
        }

        /// <summary>The kept sessions to delete so that only the newest <paramref name="keep"/> remain.</summary>
        public static IEnumerable<string> ToDelete(IEnumerable<string> kept, int keep)
            => kept
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Skip(Math.Max(0, keep));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            // The Editor's log is Editor.log, rotated by the Editor; a server started with -logFile -
            // writes to stdout and has no file to keep.
            if (Application.isEditor) return;
            string current = Application.consoleLogPath;
            try
            {
                string kept = KeepPrevious(current);
                Debug.Log("[log] this session: " + current
                    + (kept != null ? "; the previous one is kept as " + kept : "; no previous session to keep")
                    + " (newest " + KeepSessions + " kept in " + FolderName + ").");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[log] the previous session's log could not be kept beside " + current + ": " + e.Message);
            }
        }
    }
}
