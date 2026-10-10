#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ironfront.Net.Protocol.Achievements;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// This machine's achievements on disk (<see cref="AchievementSaveData"/>), kept the way indie
    /// games without a platform store keep a save: a file in the user's data folder, never in the
    /// game's own folder, written whole, with the previous copy kept as a backup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner's rule of 2026-10-10: updating the game never costs an achievement.</b> The file lives
    /// in <see cref="Application.persistentDataPath"/> (Windows: <c>AppData/LocalLow/LTM10/IronfrontReborn</c>),
    /// which unpacking a new release over or beside the old one never touches. Every load MERGES
    /// every copy there is -- the file, its backup and the PlayerPrefs keys builds up to v4.6.0
    /// wrote -- so no source can make the others forget anything, and every save writes the
    /// PlayerPrefs keys back too, so an older build started afterwards still sees what was earned.
    /// </para>
    /// <para>
    /// <b>Written whole, never torn.</b> A save goes to <c>achievements.json.tmp</c> first and then
    /// replaces the file, which first becomes <c>achievements.json.bak</c>. A crash leaves either the
    /// old file or the new one, and an unreadable file falls back to the backup.
    /// </para>
    /// <para>
    /// <b>Saved when it matters.</b> An unlock, a round's end and quitting save at once; progress
    /// numbers that change many times a round (a kill raises a best) are saved by
    /// <see cref="Tick"/> at most every few seconds.
    /// </para>
    /// <para>Main thread only, like the PlayerPrefs it replaces.</para>
    /// </remarks>
    public static class AchievementVault
    {
        /// <summary>
        /// The file's name. The Editor keeps its own (<c>achievements-editor.json</c>): its
        /// persistentDataPath is the installed game's, and a Play session or a test must never
        /// write a real player's achievements.
        /// </summary>
        public static string FileName => Application.isEditor ? "achievements-editor.json" : "achievements.json";

        // The keys builds up to v4.6.0 kept in PlayerPrefs; still written, so a downgrade sees them.
        public const string LegacyEarnedKey = "ironfront.achievements.earned";
        public const string LegacyQueueKey = "ironfront.achievements.toast-queue";
        public const string LegacyGuideTabsKey = "ironfront.achievements.guide-tabs";
        public const string LegacyProgressPrefix = "ironfront.achievements.v2.";
        public const string LegacyGoldenWrenchKey = "ironfront.secrets.golden-wrench";

        /// <summary>Longest a changed progress number waits for <see cref="Tick"/> to save it.</summary>
        private const float SaveEverySeconds = 5f;

        private static AchievementSaveData? _data;
        private static bool _dirty;
        private static float _nextSaveAt;
        private static string? _folderOverride;

        /// <summary>Whether the PlayerPrefs keys of builds up to v4.6.0 are read and written; off in tests.</summary>
        private static bool _useLegacy = true;

        /// <summary>What this machine knows, loaded on first use.</summary>
        public static AchievementSaveData Data
        {
            get
            {
                if (_data == null) Load();
                return _data!;
            }
        }

        /// <summary>The file's path.</summary>
        public static string FilePath => Path.Combine(Folder, FileName);

        /// <summary>
        /// Points the vault at another folder, for tests; null restores the default. PlayerPrefs are
        /// left alone unless <paramref name="legacy"/> asks for the v4.6.0 keys to be read and written.
        /// </summary>
        public static void UseFolderForTests(string? folder, bool legacy = false)
        {
            _folderOverride = folder;
            _useLegacy = folder == null || legacy;
            _data = null;
            _dirty = false;
        }

        private static string Folder => _folderOverride ?? Application.persistentDataPath;

        /// <summary>Marks <paramref name="id"/> earned now and saves at once; true when it is new.</summary>
        public static bool AddEarned(string id)
        {
            if (!Data.AddEarned(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return false;
            Save();
            return true;
        }

        /// <summary>Raises a progress number by its merge rule; saved by <see cref="Tick"/> soon after.</summary>
        public static void RaiseProgress(CareerStat stat, long value)
        {
            if (Data.RaiseProgress(CareerStats.Key(stat), value)) MarkDirty();
        }

        /// <summary>A progress number, 0 when there is none.</summary>
        public static long GetProgress(CareerStat stat) => Data.GetProgress(CareerStats.Key(stat));

        /// <summary>Replaces the banner queue and saves at once (a banner must survive a quit).</summary>
        public static void SetToastQueue(IEnumerable<string> ids)
        {
            var next = new List<string>();
            foreach (string id in ids)
                if (!string.IsNullOrEmpty(id) && !next.Contains(id)) next.Add(id);
            if (SameList(Data.ToastQueue, next)) return;
            Data.ToastQueue.Clear();
            Data.ToastQueue.AddRange(next);
            Save();
        }

        /// <summary>The golden wrench was found; saved at once.</summary>
        public static void SetGoldenWrench()
        {
            if (Data.GoldenWrench) return;
            Data.GoldenWrench = true;
            Save();
        }

        /// <summary>Something changed that may wait for the next <see cref="Tick"/>.</summary>
        public static void MarkDirty()
        {
            if (_dirty) return;
            _dirty = true;
            _nextSaveAt = Time.realtimeSinceStartup + SaveEverySeconds;
        }

        /// <summary>Saves a pending change once its few seconds are up. Call every frame or so.</summary>
        public static void Tick()
        {
            if (_dirty && Time.realtimeSinceStartup >= _nextSaveAt) Save();
        }

        /// <summary>Saves now if anything is pending (a round ended, the game is quitting).</summary>
        public static void Flush()
        {
            if (_dirty) Save();
        }

        /// <summary>Reads every copy there is and merges them; writes the result back when it grew.</summary>
        public static void Load()
        {
            var data = new AchievementSaveData();
            bool fromFile = TryRead(FilePath, out AchievementSaveData file);
            if (fromFile) data.MergeFrom(file);
            if (TryRead(FilePath + ".bak", out AchievementSaveData backup)) data.MergeFrom(backup);

            bool legacyAdded = _useLegacy && data.MergeFrom(ReadLegacy());

            _data = data;
            // A first run after the update (no file yet) or a downgrade that earned something writes
            // the merged copy out at once.
            if (!fromFile || legacyAdded) Save();
        }

        /// <summary>Writes the document whole, keeping the previous one as the backup, and mirrors PlayerPrefs.</summary>
        public static void Save()
        {
            AchievementSaveData data = Data;
            _dirty = false;
            data.WrittenBy = Application.version + " " + BuildStamp.Describe();
            string json = data.ToJson(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                Directory.CreateDirectory(Folder);
                string path = FilePath;
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, path + ".bak");
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                        File.Delete(path);
                        File.Move(temp, path);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Not silent: the PlayerPrefs mirror below still holds everything, and the next
                // save tries the file again.
                Debug.LogWarning("[achievements] could not write " + FileName + " (" + e.Message + "); kept in the game's preferences.");
            }
            if (_useLegacy) WriteLegacy(data);
        }

        private static bool TryRead(string path, out AchievementSaveData data)
        {
            data = new AchievementSaveData();
            if (!File.Exists(path)) return false;
            try
            {
                if (AchievementSaveData.TryParse(File.ReadAllText(path), out data, out string error)) return true;
                Debug.LogWarning("[achievements] " + Path.GetFileName(path) + " is unreadable (" + error + "); the other copies are used.");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[achievements] could not read " + Path.GetFileName(path) + " (" + e.Message + ").");
            }
            data = new AchievementSaveData();
            return false;
        }

        /// <summary>What builds up to v4.6.0 kept in PlayerPrefs, as a document.</summary>
        private static AchievementSaveData ReadLegacy()
        {
            var data = new AchievementSaveData();
            foreach (string id in PlayerPrefs.GetString(LegacyEarnedKey, string.Empty).Split(','))
                if (id.Length > 0) data.AddEarned(id, 0);
            foreach (string id in PlayerPrefs.GetString(LegacyQueueKey, string.Empty).Split(','))
                if (id.Length > 0 && !data.ToastQueue.Contains(id)) data.ToastQueue.Add(id);

            int guide = PlayerPrefs.GetInt(LegacyGuideTabsKey, 0);
            if (guide != 0) data.RaiseProgress(CareerStats.Key(CareerStat.PrGuidePages), guide);
            foreach (CareerStat stat in PracticeStats())
            {
                string text = PlayerPrefs.GetString(LegacyProgressPrefix + CareerStats.Key(stat), "0");
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value != 0)
                    data.RaiseProgress(CareerStats.Key(stat), value);
            }
            if (PlayerPrefs.GetInt(LegacyGoldenWrenchKey, 0) == 1) data.GoldenWrench = true;
            return data;
        }

        private static void WriteLegacy(AchievementSaveData data)
        {
            PlayerPrefs.SetString(LegacyEarnedKey, string.Join(",", data.Earned.Keys));
            PlayerPrefs.SetString(LegacyQueueKey, string.Join(",", data.ToastQueue));
            int guide = (int)data.GetProgress(CareerStats.Key(CareerStat.PrGuidePages));
            if (guide != 0) PlayerPrefs.SetInt(LegacyGuideTabsKey, guide);
            foreach (CareerStat stat in PracticeStats())
            {
                long value = data.GetProgress(CareerStats.Key(stat));
                if (value != 0) PlayerPrefs.SetString(LegacyProgressPrefix + CareerStats.Key(stat), value.ToString(CultureInfo.InvariantCulture));
            }
            if (data.GoldenWrench) PlayerPrefs.SetInt(LegacyGoldenWrenchKey, 1);
            PlayerPrefs.Save();
        }

        /// <summary>Every practice progress stat, guide pages excepted (they had their own key).</summary>
        private static IEnumerable<CareerStat> PracticeStats()
        {
            foreach (CareerStat stat in (CareerStat[])Enum.GetValues(typeof(CareerStat)))
                if (CareerStats.IsPractice(stat) && stat != CareerStat.PrGuidePages) yield return stat;
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _data = null;
            _dirty = false;
            _nextSaveAt = 0f;
            _folderOverride = null;
            _useLegacy = true;
            Application.quitting -= Flush;
            Application.quitting += Flush;
        }
    }
}
