using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>The four verbs check 11 names, in the order it names them.</summary>
    /// <remarks>
    /// <b>Named rather than counted.</b> B-11 graded PARTIAL for two months on a run that
    /// counted 6,078 tick records and could not say whether any of them contained a seat, a
    /// shot or a corpse — so a total is exactly the evidence that was already there and did not
    /// answer the question. What the check asks for is which verbs happened, so that is what
    /// this records.
    /// </remarks>
    public enum HarnessVerb
    {
        /// <summary>An actor sat in a vehicle and that vehicle then moved.</summary>
        Drive = 0,

        /// <summary>Health came off something — dealt by this client, or observed on the wire.</summary>
        Damage = 1,

        /// <summary>A vehicle carried <see cref="VehicleStateFlags.Burning"/>.</summary>
        Burn = 2,

        /// <summary>An actor died.</summary>
        Death = 3,
    }

    /// <summary>
    /// The first occurrence of each verb, with the tick it was seen at and what saw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tick is the client's last decoded server tick, and the field name says so.</b>
    /// <c>DeathMessage</c> and <c>HitConfirmMessage</c> carry no tick of their own — they are
    /// reliable events on channel 2, delivered beside a snapshot stream rather than inside it —
    /// so the honest stamp is "the newest tick this client had decoded when the event arrived",
    /// which is within one snapshot interval of the truth and is not the same number. Calling
    /// it <c>serverTick</c> would invite a reader to line it up against the server's own tick
    /// JSONL to the tick, which it does not support.
    /// </para>
    /// <para>
    /// <b>First occurrence, not every occurrence.</b> The check asks whether the sequence can
    /// be provoked at all; a run that fires a verb ten thousand times answers the same question
    /// as one that fires it once, and a per-occurrence log of <c>Damage</c> at 8 clients × 30 Hz
    /// is a file nobody opens. The count is kept beside it so a single freak observation is
    /// distinguishable from a verb the run actually lived in.
    /// </para>
    /// </remarks>
    public sealed class VerbLog
    {
        /// <summary>One verb's first sighting.</summary>
        public sealed class Entry
        {
            /// <summary>Newest tick this client had decoded when the verb was seen.</summary>
            public uint AtDecodedTick { get; init; }

            /// <summary>Harness clock at the sighting, in milliseconds since the run started.</summary>
            public double AtMs { get; init; }

            /// <summary>
            /// The client index that saw it first.
            /// </summary>
            /// <remarks>
            /// <b>A sighting that cannot name its observer cannot be correlated with anything.</b>
            /// Interest management sends different clients different entities, so a verb is always
            /// seen by a particular client holding a particular view — and every question asked of
            /// a verb afterwards is per-client: which body was it, which seat was it in, did that
            /// client's input reach anything. Without this the answer is an inference from
            /// whichever client's counters look closest, which is how a plausible attribution
            /// becomes a recorded fact.
            /// </remarks>
            public int ObservedByClient { get; init; }

            /// <summary>How it was seen — the evidence, not the conclusion.</summary>
            public string Evidence { get; init; } = string.Empty;

            /// <summary>How many times the verb was seen after the first.</summary>
            public long Count { get; set; }
        }

        private readonly Dictionary<HarnessVerb, Entry> _first =
            new Dictionary<HarnessVerb, Entry>();

        /// <summary>The verbs seen, keyed by verb. Absent means never seen.</summary>
        public IReadOnlyDictionary<HarnessVerb, Entry> First => _first;

        /// <summary>Whether every one of the four verbs was seen at least once.</summary>
        public bool AllFour =>
            _first.ContainsKey(HarnessVerb.Drive)
            && _first.ContainsKey(HarnessVerb.Damage)
            && _first.ContainsKey(HarnessVerb.Burn)
            && _first.ContainsKey(HarnessVerb.Death);

        /// <summary>The verbs NOT seen, in check 11's own order. Empty when all four fired.</summary>
        /// <remarks>
        /// Returned as the missing set rather than as a boolean, because acceptance criterion 1
        /// grades on all four <b>or names the one still missing</b> — and a caller handed only
        /// <see cref="AllFour"/> would have to re-derive the name to write that sentence.
        /// </remarks>
        public IReadOnlyList<HarnessVerb> Missing
        {
            get
            {
                var missing = new List<HarnessVerb>(4);
                foreach (HarnessVerb verb in
                         new[] { HarnessVerb.Drive, HarnessVerb.Damage,
                                 HarnessVerb.Burn, HarnessVerb.Death })
                {
                    if (!_first.ContainsKey(verb)) missing.Add(verb);
                }

                return missing;
            }
        }

        /// <summary>
        /// Records one sighting. The first wins the stamp; every later one only counts.
        /// </summary>
        /// <param name="evidence">
        /// What was actually observed, in the observer's own terms — "S_HIT_CONFIRM target=43"
        /// rather than "damage happened". A verb line that cannot be traced back to a decoded
        /// message is a claim, and this whole class exists because B-11 had one of those.
        /// </param>
        public void Record(
            HarnessVerb verb, int clientIndex, uint atDecodedTick, double atMs, string evidence)
        {
            if (_first.TryGetValue(verb, out Entry? existing))
            {
                existing.Count++;
                return;
            }

            _first[verb] = new Entry
            {
                ObservedByClient = clientIndex,
                AtDecodedTick = atDecodedTick,
                AtMs = atMs,
                Evidence = evidence ?? string.Empty,
                Count = 1,
            };
        }

        /// <summary>Folds another client's log into this one, keeping the earliest sighting.</summary>
        /// <remarks>
        /// <b>Earliest by decoded tick, and ties broken by the harness clock.</b> Two clients
        /// decode the same tick at different wall-clock moments, so tick alone leaves an
        /// arbitrary winner — and a run-level line that names a different client's sighting on
        /// each re-run of the same seed is not the reproducibility this harness is for.
        /// </remarks>
        public void MergeFrom(VerbLog other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            foreach (KeyValuePair<HarnessVerb, Entry> pair in other._first)
            {
                if (!_first.TryGetValue(pair.Key, out Entry? mine))
                {
                    _first[pair.Key] = new Entry
                    {
                        ObservedByClient = pair.Value.ObservedByClient,
                        AtDecodedTick = pair.Value.AtDecodedTick,
                        AtMs = pair.Value.AtMs,
                        Evidence = pair.Value.Evidence,
                        Count = pair.Value.Count,
                    };
                    continue;
                }

                bool theirsIsEarlier =
                    pair.Value.AtDecodedTick < mine.AtDecodedTick
                    || (pair.Value.AtDecodedTick == mine.AtDecodedTick
                        && pair.Value.AtMs < mine.AtMs);

                if (!theirsIsEarlier)
                {
                    mine.Count += pair.Value.Count;
                    continue;
                }

                _first[pair.Key] = new Entry
                {
                    ObservedByClient = pair.Value.ObservedByClient,
                    AtDecodedTick = pair.Value.AtDecodedTick,
                    AtMs = pair.Value.AtMs,
                    Evidence = pair.Value.Evidence,
                    Count = mine.Count + pair.Value.Count,
                };
            }
        }
    }
}
