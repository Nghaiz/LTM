using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>How loud a hitmarker should be. phase-02 task 6.</summary>
    /// <remarks>
    /// Ordered by loudness so a caller can compare rather than switch — the drawing layer
    /// picks a colour and a sound pitch per level, and neither of those decisions belongs
    /// here.
    /// </remarks>
    public enum HitmarkerSeverity : byte
    {
        /// <summary>A body or limb hit that did not kill.</summary>
        Normal = 0,

        /// <summary>A headshot that did not kill. Drawn red, higher-pitched tick.</summary>
        Headshot = 1,

        /// <summary>The hit that killed. Outranks a headshot when both are true.</summary>
        Kill = 2,
    }

    /// <summary>
    /// One confirmed hit, as data. "A hit landed on this actor at tick N, this hard."
    /// </summary>
    /// <remarks>
    /// Whether that becomes a white cross, a red cross or a sound is the drawing layer's
    /// problem — which is the point of the split, because everything above that line is
    /// testable and nothing below it is.
    /// </remarks>
    public readonly struct HitmarkerEvent
    {
        public readonly ushort TargetActorId;

        /// <summary>Already unpacked from the wire's x10 fixed point.</summary>
        public readonly float Damage;

        public readonly HitboxType Hitbox;
        public readonly HitmarkerSeverity Severity;

        /// <summary>The server tick the client was showing when this landed.</summary>
        public readonly uint AtTick;

        /// <summary>Client clock at receipt, in seconds. Drives the display timer.</summary>
        public readonly float AtSeconds;

        public HitmarkerEvent(
            ushort targetActorId, float damage, HitboxType hitbox,
            HitmarkerSeverity severity, uint atTick, float atSeconds)
        {
            TargetActorId = targetActorId;
            Damage = damage;
            Hitbox = hitbox;
            Severity = severity;
            AtTick = atTick;
            AtSeconds = atSeconds;
        }

        /// <summary>Builds one from the wire message.</summary>
        public static HitmarkerEvent From(in HitConfirmMessage message, uint atTick, float atSeconds)
            => new HitmarkerEvent(
                message.TargetActorId,
                message.Damage,
                message.HitboxType,
                SeverityOf(message.Killed, message.Headshot),
                atTick,
                atSeconds);

        /// <summary>A kill outranks a headshot: the loudest true thing wins.</summary>
        public static HitmarkerSeverity SeverityOf(bool killed, bool headshot)
        {
            if (killed) return HitmarkerSeverity.Kill;
            return headshot ? HitmarkerSeverity.Headshot : HitmarkerSeverity.Normal;
        }
    }

    /// <summary>
    /// Holds the newest hitmarker for as long as it should be on screen. phase-02 task 6.
    /// </summary>
    /// <remarks>
    /// <b>The newest hit always wins, including a quieter one.</b> Firing an automatic weapon
    /// into someone produces a hit every tenth of a second, and each is a fresh confirmation
    /// that the shot landed — holding the loudest one from half a second ago would freeze a
    /// kill marker over a target who is still alive. The severity is a property of the hit,
    /// not a high-water mark.
    /// </remarks>
    public sealed class HitmarkerModel
    {
        /// <summary>An X at screen centre for 150 ms. phase-02 task 6.</summary>
        public const float DefaultDisplaySeconds = 0.15f;

        private HitmarkerEvent _current;
        private bool _hasAny;

        /// <summary>How long one hitmarker stays up.</summary>
        public float DisplaySeconds { get; set; } = DefaultDisplaySeconds;

        /// <summary>The newest hit. Only meaningful when <see cref="IsVisible"/> is true.</summary>
        public HitmarkerEvent Current => _current;

        /// <summary>Hits confirmed this connection. The "am I hitting anything" counter.</summary>
        public long HitCount { get; private set; }

        /// <summary>Records a hit and restarts the display timer.</summary>
        public void Push(in HitConfirmMessage message, uint atTick, float nowSeconds)
            => Push(HitmarkerEvent.From(in message, atTick, nowSeconds));

        /// <summary>Records an already-built event. For callers that synthesise one.</summary>
        public void Push(in HitmarkerEvent hit)
        {
            _current = hit;
            _hasAny = true;
            HitCount++;
        }

        /// <summary>Whether a hitmarker should be drawn at <paramref name="nowSeconds"/>.</summary>
        public bool IsVisible(float nowSeconds)
            => _hasAny && nowSeconds - _current.AtSeconds < DisplaySeconds;

        /// <summary>Drops the marker and the counter.</summary>
        public void Reset()
        {
            _current = default;
            _hasAny = false;
            HitCount = 0;
        }
    }

    /// <summary>One killfeed line, as data.</summary>
    public readonly struct KillfeedEntry
    {
        public readonly ushort KillerActorId;
        public readonly ushort VictimActorId;
        public readonly CauseOfDeath Cause;

        /// <summary>The killer was the world — fall damage, drowning, a vehicle with no driver.</summary>
        public readonly bool KilledByEnvironment;

        /// <summary>The killing blow landed on the head. Drawn with the headshot icon.</summary>
        public readonly bool Headshot;

        /// <summary>Client clock at receipt, in seconds. Drives the hold timer.</summary>
        public readonly float PostedAtSeconds;

        public KillfeedEntry(
            ushort killerActorId, ushort victimActorId, CauseOfDeath cause,
            bool killedByEnvironment, bool headshot, float postedAtSeconds)
        {
            KillerActorId = killerActorId;
            VictimActorId = victimActorId;
            Cause = cause;
            KilledByEnvironment = killedByEnvironment;
            Headshot = headshot;
            PostedAtSeconds = postedAtSeconds;
        }

        /// <summary>Builds one from the wire message.</summary>
        public static KillfeedEntry From(in DeathMessage message, float nowSeconds)
            => new KillfeedEntry(
                message.KillerActorId,
                message.VictimActorId,
                message.Cause,
                message.KilledByEnvironment,
                (HitboxType)message.HitboxHit == HitboxType.Head,
                nowSeconds);
    }

    /// <summary>
    /// The last few kills, newest first, each expiring on its own clock. phase-02 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixed ring rather than a list: at most five lines are ever drawn, kills arrive in
    /// bursts at the end of a round, and a <c>List</c> that grew and shifted per kill would
    /// allocate during exactly the busiest second of the match.
    /// </para>
    /// <para>
    /// <b><see cref="Prune"/> is the caller's to run.</b> Expiry needs a clock, and this type
    /// has none — passing one into every read would put a float in the signature of
    /// <see cref="Count"/> and the indexer for no gain. Call <see cref="Prune"/> once a frame
    /// before reading; entries older than <see cref="HoldSeconds"/> drop out there.
    /// </para>
    /// </remarks>
    public sealed class KillfeedModel
    {
        /// <summary>Max lines on screen. phase-02 task 6.</summary>
        public const int DefaultCapacity = 5;

        /// <summary>How long one line is held. phase-02 task 6.</summary>
        public const float DefaultHoldSeconds = 5f;

        private readonly KillfeedEntry[] _entries;
        private int _count;

        public KillfeedModel(int capacity = DefaultCapacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new KillfeedEntry[capacity];
        }

        /// <summary>Seconds a line stays up.</summary>
        public float HoldSeconds { get; set; } = DefaultHoldSeconds;

        /// <summary>Lines currently held. Run <see cref="Prune"/> first for a live answer.</summary>
        public int Count => _count;

        /// <summary>Max lines this feed holds.</summary>
        public int Capacity => _entries.Length;

        /// <summary>Kills seen this connection, including ones already expired.</summary>
        public long TotalKills { get; private set; }

        /// <summary>Index 0 is the newest line.</summary>
        public KillfeedEntry this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
                return _entries[index];
            }
        }

        /// <summary>Posts a kill at the top, dropping the oldest line if the feed is full.</summary>
        public void Push(in DeathMessage message, float nowSeconds)
            => Push(KillfeedEntry.From(in message, nowSeconds));

        /// <summary>Posts an already-built entry.</summary>
        public void Push(in KillfeedEntry entry)
        {
            int keep = _count < _entries.Length ? _count : _entries.Length - 1;
            for (int i = keep; i > 0; i--) _entries[i] = _entries[i - 1];

            _entries[0] = entry;
            _count = keep + 1;
            TotalKills++;
        }

        /// <summary>
        /// Drops every line older than <see cref="HoldSeconds"/>. Call once a frame.
        /// </summary>
        /// <remarks>
        /// <b>Compacts rather than truncating at the first expired entry.</b> Truncating is
        /// correct only while the timestamps are non-increasing down the feed, which holds for
        /// every push that comes off the wire in order — and stops holding for one that does
        /// not. A single out-of-order entry at the head would then take every live line below it
        /// with it, emptying the killfeed at the moment it is busiest. Five iterations is not
        /// worth an ordering assumption that nothing enforces.
        /// </remarks>
        public void Prune(float nowSeconds)
        {
            int kept = 0;

            for (int i = 0; i < _count; i++)
            {
                if (nowSeconds - _entries[i].PostedAtSeconds >= HoldSeconds) continue;
                if (kept != i) _entries[kept] = _entries[i];
                kept++;
            }

            _count = kept;
        }

        /// <summary>Empties the feed. Call when leaving a match.</summary>
        public void Reset()
        {
            _count = 0;
            TotalKills = 0;
        }
    }
}
