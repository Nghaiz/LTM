#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// What this machine knows about its player's achievements, as one versioned document: the
    /// practice achievements earned here, their progress numbers, the golden wrench, and the banners
    /// still waiting to play. <see cref="AchievementVault"/> keeps it on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner's rule of 2026-10-10: an update never costs a player an achievement.</b> So the
    /// document only ever grows: <see cref="MergeFrom"/> keeps every earned id from every copy (the
    /// earliest time wins), the larger of two progress numbers (or both halves of a bit mask), and
    /// ids this build does not know -- a newer build's, or a retired one's -- are kept, never dropped.
    /// What a build shows is the catalogue's business; what it keeps is everything.
    /// </para>
    /// <para>
    /// Engine-free so the merge rules run under <c>dotnet test</c>; written and read with
    /// <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>, which use no reflection and
    /// survive IL2CPP's stripping.
    /// </para>
    /// </remarks>
    public sealed class AchievementSaveData
    {
        /// <summary>The document's layout. A reader keeps what it does not understand rather than fail.</summary>
        public const int Schema = 1;

        /// <summary>Earned ids and when, Unix milliseconds; 0 when the time is not known.</summary>
        public Dictionary<string, long> Earned { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Practice progress by career key (<see cref="CareerStats.Key"/>).</summary>
        public Dictionary<string, long> Progress { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Banners not yet played, oldest first.</summary>
        public List<string> ToastQueue { get; } = new List<string>();

        /// <summary>ISEEGOLD was typed on this machine.</summary>
        public bool GoldenWrench { get; set; }

        /// <summary>The build that last wrote the document, for a reader's diagnostics.</summary>
        public string WrittenBy { get; set; } = string.Empty;

        /// <summary>Records <paramref name="id"/> as earned at <paramref name="at"/>; true when it is new.</summary>
        public bool AddEarned(string id, long at)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (Earned.TryGetValue(id, out long had))
            {
                if (at > 0 && (had == 0 || at < had)) Earned[id] = at;
                return false;
            }
            Earned[id] = at > 0 ? at : 0;
            return true;
        }

        /// <summary>The progress number for <paramref name="key"/>, 0 when there is none.</summary>
        public long GetProgress(string key) => Progress.TryGetValue(key, out long value) ? value : 0;

        /// <summary>
        /// Folds <paramref name="value"/> into <paramref name="key"/> by the merge rule; true when the
        /// stored number changed.
        /// </summary>
        public bool RaiseProgress(string key, long value)
        {
            if (string.IsNullOrEmpty(key)) return false;
            long had = GetProgress(key);
            long merged = MergeValue(key, had, value);
            if (merged == had) return false;
            Progress[key] = merged;
            return true;
        }

        /// <summary>
        /// Takes in everything <paramref name="other"/> knows: the union of earned ids (earliest
        /// known time), each progress number merged, banners neither copy has played yet, the
        /// wrench if either has it. Nothing already here is lost. True when anything changed.
        /// </summary>
        public bool MergeFrom(AchievementSaveData other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            bool changed = false;
            foreach (KeyValuePair<string, long> earned in other.Earned)
            {
                long before = Earned.TryGetValue(earned.Key, out long had) ? had : -1;
                AddEarned(earned.Key, earned.Value);
                if (Earned[earned.Key] != before) changed = true;
            }
            foreach (KeyValuePair<string, long> progress in other.Progress)
                changed |= RaiseProgress(progress.Key, progress.Value);
            foreach (string id in other.ToastQueue)
            {
                if (string.IsNullOrEmpty(id) || ToastQueue.Contains(id)) continue;
                ToastQueue.Add(id);
                changed = true;
            }
            if (other.GoldenWrench && !GoldenWrench)
            {
                GoldenWrench = true;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Two copies of one progress number: both halves of a bit mask (a map finished here, a side
        /// won there), else the larger. Stored numbers are totals, never deltas, so a sum would
        /// double-count; an unknown key is a newer build's and takes the larger.
        /// </summary>
        public static long MergeValue(string key, long a, long b)
        {
            if (CareerStats.TryParse(key, out CareerStat stat) && CareerStats.CombineOf(stat) == CareerCombine.Or)
                return a | b;
            return a > b ? a : b;
        }

        /// <summary>The document as JSON, keys sorted so two equal documents are equal text.</summary>
        public string ToJson(long savedAt)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("schema", Schema);
                json.WriteString("writtenBy", WrittenBy ?? string.Empty);
                json.WriteNumber("savedAt", savedAt);

                json.WriteStartObject("earned");
                foreach (string id in Sorted(Earned.Keys)) json.WriteNumber(id, Earned[id]);
                json.WriteEndObject();

                json.WriteStartObject("progress");
                foreach (string key in Sorted(Progress.Keys)) json.WriteNumber(key, Progress[key]);
                json.WriteEndObject();

                json.WriteBoolean("goldenWrench", GoldenWrench);

                json.WriteStartArray("toastQueue");
                foreach (string id in ToastQueue) json.WriteStringValue(id);
                json.WriteEndArray();

                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Reads a document. False, with the reason, when the text is not one (a torn write, a
        /// hand edit gone wrong); a field it does not know, or a newer schema's, is skipped, not fatal.
        /// </summary>
        public static bool TryParse(string text, out AchievementSaveData data, out string error)
        {
            data = new AchievementSaveData();
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "empty";
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "not an object";
                    return false;
                }

                if (root.TryGetProperty("writtenBy", out JsonElement writtenBy) && writtenBy.ValueKind == JsonValueKind.String)
                    data.WrittenBy = writtenBy.GetString() ?? string.Empty;

                if (root.TryGetProperty("earned", out JsonElement earned) && earned.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty entry in earned.EnumerateObject())
                        data.AddEarned(entry.Name, entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt64(out long at) ? at : 0);

                if (root.TryGetProperty("progress", out JsonElement progress) && progress.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty entry in progress.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt64(out long value))
                            data.RaiseProgress(entry.Name, value);

                if (root.TryGetProperty("goldenWrench", out JsonElement wrench) && wrench.ValueKind == JsonValueKind.True)
                    data.GoldenWrench = true;

                if (root.TryGetProperty("toastQueue", out JsonElement queue) && queue.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement id in queue.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()) && !data.ToastQueue.Contains(id.GetString()!))
                            data.ToastQueue.Add(id.GetString()!);

                return true;
            }
            catch (JsonException e)
            {
                error = e.Message;
                data = new AchievementSaveData();
                return false;
            }
        }

        private static List<string> Sorted(IEnumerable<string> keys)
        {
            var list = new List<string>(keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }
}
