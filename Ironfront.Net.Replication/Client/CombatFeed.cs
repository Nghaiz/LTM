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

        /// <summary>A headshot that did not kill. Drawn gold, with a metal "dink".</summary>
        Headshot = 1,

        /// <summary>The hit that killed, on the body. Drawn red.</summary>
        Kill = 2,

        /// <summary>
        /// The hit that killed, on the head: the loudest of all (owner's run of 2026-10-10, phase
        /// P38: a headshot the player cannot tell apart from a body shot is one they doubt).
        /// </summary>
        HeadshotKill = 3,
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

        /// <summary>The loudest true thing wins: a kill outranks a headshot, and a headshot kill both.</summary>
        public static HitmarkerSeverity SeverityOf(bool killed, bool headshot)
        {
            if (killed) return headshot ? HitmarkerSeverity.HeadshotKill : HitmarkerSeverity.Kill;
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

    /// <summary>What a killfeed line reports: a death, or one of the match's other events.</summary>
    /// <remarks>
    /// The feed started as deaths only. The owner's report of 2026-09-30 asked it to follow the
    /// whole match -- the flags, who joined and left, the round -- so those arrive as lines of
    /// their own kind in the same feed, ordered with the deaths around them.
    /// </remarks>
    public enum KillfeedKind : byte
    {
        /// <summary>Somebody died: a kill, or a death nobody scored.</summary>
        Death = 0,

        /// <summary><see cref="KillfeedEntry.SubjectTeam"/> took a capture point.</summary>
        FlagCaptured = 1,

        /// <summary><see cref="KillfeedEntry.SubjectTeam"/> lost a capture point, which went neutral.</summary>
        FlagLost = 2,

        /// <summary>A player joined the match.</summary>
        PlayerJoined = 3,

        /// <summary>A player left the match.</summary>
        PlayerLeft = 4,

        /// <summary>A round went live.</summary>
        RoundStarted = 5,

        /// <summary>A round ended: <see cref="KillfeedEntry.SubjectTeam"/> won it, or nobody did.</summary>
        RoundEnded = 6,

        /// <summary>
        /// Lines that waited too long for a place on screen, folded into one "+N more" line so no
        /// event vanishes without the feed saying so.
        /// </summary>
        Overflow = 7,
    }

    /// <summary>One killfeed line, as data.</summary>
    /// <remarks>
    /// Built by <see cref="From(in DeathMessage, float)"/> or one of the other factories, then
    /// completed by the presenter with what only it can know -- the two sides
    /// (<see cref="WithTeams"/>), what the kill earned (<see cref="WithAccolades"/>), and whether
    /// it names the viewing player (<see cref="WithPriority"/>) -- and numbered and timed by
    /// <see cref="KillfeedModel"/>.
    /// </remarks>
    public readonly struct KillfeedEntry
    {
        public readonly ushort KillerActorId;
        public readonly ushort VictimActorId;
        public readonly CauseOfDeath Cause;

        /// <summary>The killer was the world — fall damage, drowning, a vehicle with no driver.</summary>
        public readonly bool KilledByEnvironment;

        /// <summary>The killing blow landed on the head. Drawn with the headshot icon.</summary>
        public readonly bool Headshot;

        /// <summary>Client clock at receipt, in seconds.</summary>
        public readonly float PostedAtSeconds;

        /// <summary>The killing weapon (<see cref="WeaponIds"/>), <c>NONE</c> when the server named none.</summary>
        public readonly byte WeaponId;

        /// <summary>The vehicle involved (<see cref="VehicleIds"/>), <c>NONE</c> when none was.</summary>
        public readonly byte VehicleType;

        /// <summary>How the vehicle was involved. <see cref="DeathDetail.None"/> from a 1.0 server.</summary>
        public readonly DeathDetail Detail;

        /// <summary>
        /// This line's place in the feed's order, 1 for the first. Stamped by
        /// <see cref="KillfeedModel.Push(in KillfeedEntry)"/>; 0 on an entry never pushed.
        /// </summary>
        /// <remarks>
        /// A drawing layer keys a row on this rather than on its index, because every index moves
        /// down one when a line arrives: keyed on the index, the whole feed would re-animate as if
        /// five kills had just happened. Unique per model, across its resets.
        /// </remarks>
        public readonly long Sequence;

        /// <summary>What this line reports.</summary>
        public readonly KillfeedKind Kind;

        /// <summary>The killer's side when the line was written, <see cref="TeamId.None"/> when unknown.</summary>
        /// <remarks>
        /// Resolved ONCE, when the death arrives, and kept: a side read off the snapshot as the
        /// line was drawn went grey whenever the actor was outside this client's interest radius,
        /// which was the "white killfeed" of the owner's report of 2026-09-30.
        /// </remarks>
        public readonly byte KillerTeam;

        /// <summary>The victim's side when the line was written, <see cref="TeamId.None"/> when unknown.</summary>
        public readonly byte VictimTeam;

        /// <summary>Metres from the killer to the victim, 0 when the server did not say.</summary>
        public readonly ushort DistanceMetres;

        /// <summary>What the kill earned the killer: a multi-kill, a streak, a revenge...</summary>
        public readonly KillfeedAccolades Accolades;

        /// <summary>The side a flag, roster or round line is about; <see cref="TeamId.None"/> for none.</summary>
        public readonly byte SubjectTeam;

        /// <summary>The capture point a flag line names, or the number of lines an overflow folds.</summary>
        public readonly int SubjectIndex;

        /// <summary>The player a roster line names.</summary>
        public readonly ushort SubjectActorId;

        /// <summary>
        /// The viewing player is in this line. It goes ahead of every line waiting for a place and
        /// is never folded away.
        /// </summary>
        public readonly bool Priority;

        /// <summary>Client clock when the line reached the screen; its hold is measured from here.</summary>
        public readonly float ShownAtSeconds;

        public KillfeedEntry(
            ushort killerActorId, ushort victimActorId, CauseOfDeath cause,
            bool killedByEnvironment, bool headshot, float postedAtSeconds,
            byte weaponId = WeaponIds.NONE, byte vehicleType = VehicleIds.NONE,
            DeathDetail detail = DeathDetail.None, long sequence = 0)
            : this(KillfeedKind.Death, killerActorId, victimActorId, cause, killedByEnvironment,
                   headshot, postedAtSeconds, weaponId, vehicleType, detail, sequence,
                   TeamId.None, TeamId.None, 0, default, TeamId.None, 0, 0, false, postedAtSeconds)
        {
        }

        private KillfeedEntry(
            KillfeedKind kind, ushort killerActorId, ushort victimActorId, CauseOfDeath cause,
            bool killedByEnvironment, bool headshot, float postedAtSeconds, byte weaponId,
            byte vehicleType, DeathDetail detail, long sequence, byte killerTeam, byte victimTeam,
            ushort distanceMetres, KillfeedAccolades accolades, byte subjectTeam, int subjectIndex,
            ushort subjectActorId, bool priority, float shownAtSeconds)
        {
            Kind = kind;
            KillerActorId = killerActorId;
            VictimActorId = victimActorId;
            Cause = cause;
            KilledByEnvironment = killedByEnvironment;
            Headshot = headshot;
            PostedAtSeconds = postedAtSeconds;
            WeaponId = weaponId;
            VehicleType = vehicleType;
            Detail = detail;
            Sequence = sequence;
            KillerTeam = killerTeam;
            VictimTeam = victimTeam;
            DistanceMetres = distanceMetres;
            Accolades = accolades;
            SubjectTeam = subjectTeam;
            SubjectIndex = subjectIndex;
            SubjectActorId = subjectActorId;
            Priority = priority;
            ShownAtSeconds = shownAtSeconds;
        }

        /// <summary>The victim killed themselves: their own grenade, their own crash.</summary>
        public bool Self => Kind == KillfeedKind.Death && !KilledByEnvironment && KillerActorId == VictimActorId;

        /// <summary>A kill somebody scored: not the world's, not the victim's own.</summary>
        public bool IsScoredKill
            => Kind == KillfeedKind.Death && !KilledByEnvironment && KillerActorId != VictimActorId;

        /// <summary>Builds a death line from the wire message.</summary>
        public static KillfeedEntry From(in DeathMessage message, float nowSeconds)
            => new KillfeedEntry(
                KillfeedKind.Death,
                message.KillerActorId,
                message.VictimActorId,
                message.Cause,
                message.KilledByEnvironment,
                (HitboxType)message.HitboxHit == HitboxType.Head,
                nowSeconds,
                message.WeaponId,
                message.VehicleType,
                message.Detail,
                0,
                TeamId.None,
                TeamId.None,
                message.DistanceMetres,
                default,
                TeamId.None,
                0,
                0,
                false,
                nowSeconds);

        /// <summary>A side took, or lost, a capture point.</summary>
        public static KillfeedEntry Flag(bool captured, byte team, int pointIndex, float nowSeconds)
            => Event(captured ? KillfeedKind.FlagCaptured : KillfeedKind.FlagLost, team, pointIndex, 0, nowSeconds);

        /// <summary>A player joined or left the match.</summary>
        public static KillfeedEntry Roster(bool joined, ushort actorId, byte team, float nowSeconds)
            => Event(joined ? KillfeedKind.PlayerJoined : KillfeedKind.PlayerLeft, team, 0, actorId, nowSeconds);

        /// <summary>A round went live, or ended with <paramref name="winner"/> (or nobody) winning it.</summary>
        public static KillfeedEntry Round(bool started, byte winner, float nowSeconds)
            => Event(started ? KillfeedKind.RoundStarted : KillfeedKind.RoundEnded, winner, 0, 0, nowSeconds);

        /// <summary>A line standing for <paramref name="count"/> lines that never found a place.</summary>
        public static KillfeedEntry Overflow(int count, float nowSeconds)
            => Event(KillfeedKind.Overflow, TeamId.None, count, 0, nowSeconds);

        private static KillfeedEntry Event(KillfeedKind kind, byte team, int index, ushort actorId, float nowSeconds)
            => new KillfeedEntry(
                kind, 0, 0, CauseOfDeath.Bullet, false, false, nowSeconds, WeaponIds.NONE,
                VehicleIds.NONE, DeathDetail.None, 0, TeamId.None, TeamId.None, 0, default,
                team, index, actorId, false, nowSeconds);

        /// <summary>This line, with the two sides resolved.</summary>
        public KillfeedEntry WithTeams(byte killerTeam, byte victimTeam)
            => new KillfeedEntry(
                Kind, KillerActorId, VictimActorId, Cause, KilledByEnvironment, Headshot,
                PostedAtSeconds, WeaponId, VehicleType, Detail, Sequence, killerTeam, victimTeam,
                DistanceMetres, Accolades, SubjectTeam, SubjectIndex, SubjectActorId, Priority,
                ShownAtSeconds);

        /// <summary>This line, with what the kill earned.</summary>
        public KillfeedEntry WithAccolades(in KillfeedAccolades accolades)
            => new KillfeedEntry(
                Kind, KillerActorId, VictimActorId, Cause, KilledByEnvironment, Headshot,
                PostedAtSeconds, WeaponId, VehicleType, Detail, Sequence, KillerTeam, VictimTeam,
                DistanceMetres, accolades, SubjectTeam, SubjectIndex, SubjectActorId, Priority,
                ShownAtSeconds);

        /// <summary>This line, marked as naming the viewing player (or not).</summary>
        public KillfeedEntry WithPriority(bool priority)
            => new KillfeedEntry(
                Kind, KillerActorId, VictimActorId, Cause, KilledByEnvironment, Headshot,
                PostedAtSeconds, WeaponId, VehicleType, Detail, Sequence, KillerTeam, VictimTeam,
                DistanceMetres, Accolades, SubjectTeam, SubjectIndex, SubjectActorId, priority,
                ShownAtSeconds);

        /// <summary>This entry, numbered.</summary>
        internal KillfeedEntry WithSequence(long sequence)
            => new KillfeedEntry(
                Kind, KillerActorId, VictimActorId, Cause, KilledByEnvironment, Headshot,
                PostedAtSeconds, WeaponId, VehicleType, Detail, sequence, KillerTeam, VictimTeam,
                DistanceMetres, Accolades, SubjectTeam, SubjectIndex, SubjectActorId, Priority,
                ShownAtSeconds);

        /// <summary>This entry, on screen from <paramref name="shownAtSeconds"/>.</summary>
        internal KillfeedEntry ShownAt(float shownAtSeconds)
            => new KillfeedEntry(
                Kind, KillerActorId, VictimActorId, Cause, KilledByEnvironment, Headshot,
                PostedAtSeconds, WeaponId, VehicleType, Detail, Sequence, KillerTeam, VictimTeam,
                DistanceMetres, Accolades, SubjectTeam, SubjectIndex, SubjectActorId, Priority,
                shownAtSeconds);

        /// <summary>An overflow line, now standing for <paramref name="count"/> lines.</summary>
        internal KillfeedEntry WithFoldedCount(int count)
            => new KillfeedEntry(
                Kind, KillerActorId, VictimActorId, Cause, KilledByEnvironment, Headshot,
                PostedAtSeconds, WeaponId, VehicleType, Detail, Sequence, KillerTeam, VictimTeam,
                DistanceMetres, Accolades, SubjectTeam, count, SubjectActorId, Priority,
                ShownAtSeconds);
    }

    /// <summary>
    /// The lines on screen, newest first, and the lines waiting for a place. phase-02 task 6,
    /// rebuilt for the owner's report of 2026-09-30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is dropped any more.</b> The first feed held five lines and pushed the oldest
    /// out the moment a sixth arrived, so a grenade that killed four while two others traded
    /// fire showed some of those deaths for a single frame, and the report read "the killfeed
    /// cannot keep up". A line that finds the feed full now WAITS. Every line gets
    /// <see cref="MinHoldSeconds"/> on screen however busy the match is, a waiting line takes
    /// the place of the oldest one that has had its minimum, and lines are let in one per
    /// <see cref="ReleaseIntervalSeconds"/> so a burst reads as a cascade rather than a jump.
    /// </para>
    /// <para>
    /// <b>A line that waits too long is folded, not lost.</b> After <see cref="MaxWaitSeconds"/>
    /// in the queue a line is old news; it is folded into one <see cref="KillfeedKind.Overflow"/>
    /// line, "+N more", which takes its place. A line naming the viewing player
    /// (<see cref="KillfeedEntry.Priority"/>) jumps the queue and is never folded.
    /// </para>
    /// <para>
    /// <b>Fixed arrays.</b> Lines arrive in bursts at the busiest second of a match, and a
    /// collection that grew and shifted per line would allocate exactly then.
    /// </para>
    /// <para>
    /// <b><see cref="Advance"/> is the caller's to run</b>, once a frame before reading: expiry
    /// and release need a clock, and this type has none.
    /// </para>
    /// </remarks>
    public sealed class KillfeedModel
    {
        /// <summary>Lines on screen at once.</summary>
        public const int DefaultCapacity = 6;

        /// <summary>How long a line stays up when nothing is waiting for its place.</summary>
        public const float DefaultHoldSeconds = 6f;

        /// <summary>The least time any line gets on screen, however busy the match.</summary>
        public const float MinHoldSeconds = 2.5f;

        /// <summary>Between two waiting lines let onto the screen.</summary>
        public const float ReleaseIntervalSeconds = 0.12f;

        /// <summary>How long a line may wait before it is folded into "+N more".</summary>
        public const float MaxWaitSeconds = 6f;

        /// <summary>Lines that can wait at once; past this the oldest is folded early.</summary>
        public const int DefaultQueueCapacity = 64;

        private readonly KillfeedEntry[] _entries;
        private int _count;

        /// <summary>Waiting lines, the next one out at index 0.</summary>
        private readonly KillfeedEntry[] _queue;
        private int _waiting;

        /// <summary>
        /// The last <see cref="KillfeedEntry.Sequence"/> handed out. Survives <see cref="Reset"/>
        /// on purpose: a row still fading from the last match must not share a number with the
        /// first kill of the next one.
        /// </summary>
        private long _lastSequence;

        private float _lastReleaseAt = float.NegativeInfinity;

        public KillfeedModel(int capacity = DefaultCapacity, int queueCapacity = DefaultQueueCapacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
            _entries = new KillfeedEntry[capacity];
            _queue = new KillfeedEntry[queueCapacity];
        }

        /// <summary>Seconds a line stays up when nothing is waiting.</summary>
        public float HoldSeconds { get; set; } = DefaultHoldSeconds;

        /// <summary>Lines on screen. Run <see cref="Advance"/> first for a live answer.</summary>
        public int Count => _count;

        /// <summary>Lines waiting for a place on screen.</summary>
        public int Waiting => _waiting;

        /// <summary>Max lines on screen.</summary>
        public int Capacity => _entries.Length;

        /// <summary>Deaths seen this connection, including ones already expired.</summary>
        public long TotalKills { get; private set; }

        /// <summary>Lines folded into an overflow line rather than shown on their own.</summary>
        public long FoldedLines { get; private set; }

        /// <summary>Bumped whenever the lines on screen change, so a HUD redraws only then.</summary>
        public long Revision { get; private set; }

        /// <summary>Index 0 is the newest line on screen.</summary>
        public KillfeedEntry this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
                return _entries[index];
            }
        }

        /// <summary>Posts a death, straight to the top when there is room.</summary>
        public void Push(in DeathMessage message, float nowSeconds)
            => Push(KillfeedEntry.From(in message, nowSeconds));

        /// <summary>
        /// Posts a line, numbering it (<see cref="KillfeedEntry.Sequence"/>): on screen at once
        /// when there is room and nothing is waiting, otherwise into the queue.
        /// </summary>
        public void Push(in KillfeedEntry entry)
        {
            KillfeedEntry numbered = entry.WithSequence(++_lastSequence);
            if (entry.Kind == KillfeedKind.Death) TotalKills++;

            if (_waiting == 0 && _count < _entries.Length)
            {
                Show(numbered.ShownAt(entry.PostedAtSeconds));
                return;
            }

            Enqueue(in numbered);
        }

        /// <summary>
        /// Expires the lines whose time is up, folds lines that waited too long, and lets the
        /// next waiting line in. Call once a frame, before reading.
        /// </summary>
        public void Advance(float nowSeconds)
        {
            ExpireShown(nowSeconds);
            FoldStale(nowSeconds);
            ReleaseWaiting(nowSeconds);
        }

        /// <summary>The name this used to go by; <see cref="Advance"/> does more than prune.</summary>
        public void Prune(float nowSeconds) => Advance(nowSeconds);

        /// <summary>Empties the feed and the queue. Call when leaving a match.</summary>
        public void Reset()
        {
            _count = 0;
            _waiting = 0;
            TotalKills = 0;
            FoldedLines = 0;
            _lastReleaseAt = float.NegativeInfinity;
            Revision++;
        }

        /// <summary>
        /// Drops every line on screen older than <see cref="HoldSeconds"/>.
        /// </summary>
        /// <remarks>
        /// <b>Compacts rather than truncating at the first expired entry.</b> Truncating is
        /// correct only while the timestamps fall down the feed, which holds for lines that
        /// arrive in order and stops holding for one that does not: a single out-of-order entry
        /// at the head would take every live line below it with it.
        /// </remarks>
        private void ExpireShown(float nowSeconds)
        {
            int kept = 0;

            for (int i = 0; i < _count; i++)
            {
                if (nowSeconds - _entries[i].ShownAtSeconds >= HoldSeconds) continue;
                if (kept != i) _entries[kept] = _entries[i];
                kept++;
            }

            if (kept != _count) Revision++;
            _count = kept;
        }

        /// <summary>Folds waiting lines older than <see cref="MaxWaitSeconds"/> into one overflow line.</summary>
        private void FoldStale(float nowSeconds)
        {
            int folded = 0;
            int kept = 0;

            for (int i = 0; i < _waiting; i++)
            {
                KillfeedEntry waiting = _queue[i];
                bool stale = !waiting.Priority
                             && waiting.Kind != KillfeedKind.Overflow
                             && nowSeconds - waiting.PostedAtSeconds >= MaxWaitSeconds;

                if (stale)
                {
                    folded++;
                    continue;
                }

                if (kept != i) _queue[kept] = waiting;
                kept++;
            }

            _waiting = kept;
            if (folded > 0) AddToOverflow(folded, nowSeconds);
        }

        /// <summary>Lets waiting lines in, one per interval, making room when the feed is full.</summary>
        private void ReleaseWaiting(float nowSeconds)
        {
            while (_waiting > 0)
            {
                if (nowSeconds - _lastReleaseAt < ReleaseIntervalSeconds) return;

                if (_count >= _entries.Length)
                {
                    int leaving = OldestPastMinimum(nowSeconds);
                    if (leaving < 0) return;
                    RemoveShownAt(leaving);
                }

                KillfeedEntry next = _queue[0];
                for (int i = 1; i < _waiting; i++) _queue[i - 1] = _queue[i];
                _waiting--;

                Show(next.ShownAt(nowSeconds));
                _lastReleaseAt = nowSeconds;
            }
        }

        /// <summary>
        /// The line on screen that has had its minimum and been up the longest; a line naming the
        /// viewing player only when no other line qualifies. -1 when none has had its minimum.
        /// </summary>
        private int OldestPastMinimum(float nowSeconds)
        {
            int best = -1;

            for (int i = 0; i < _count; i++)
            {
                KillfeedEntry shown = _entries[i];
                if (nowSeconds - shown.ShownAtSeconds < MinHoldSeconds) continue;

                if (best < 0)
                {
                    best = i;
                    continue;
                }

                // Ties go to the higher index, which is the older line: the list is newest first.
                KillfeedEntry current = _entries[best];
                bool better = current.Priority != shown.Priority
                    ? !shown.Priority
                    : shown.ShownAtSeconds <= current.ShownAtSeconds;

                if (better) best = i;
            }

            return best;
        }

        private void Show(in KillfeedEntry entry)
        {
            int keep = _count < _entries.Length ? _count : _entries.Length - 1;
            for (int i = keep; i > 0; i--) _entries[i] = _entries[i - 1];

            _entries[0] = entry;
            _count = keep + 1;
            Revision++;
        }

        private void RemoveShownAt(int index)
        {
            for (int i = index + 1; i < _count; i++) _entries[i - 1] = _entries[i];
            _count--;
            Revision++;
        }

        /// <summary>
        /// Queues a line: behind the other priority lines if it names the viewing player,
        /// otherwise at the back. A full queue folds its oldest ordinary lines to make room.
        /// </summary>
        private void Enqueue(in KillfeedEntry entry)
        {
            if (!MakeRoomToWait(entry.PostedAtSeconds))
            {
                // Everything waiting names the viewing player. Keep those; this line is the one
                // that cannot be kept, and it is counted rather than lost without a trace.
                FoldedLines++;
                return;
            }

            int at = _waiting;
            if (entry.Priority)
            {
                at = 0;
                while (at < _waiting && _queue[at].Priority) at++;
            }

            for (int i = _waiting; i > at; i--) _queue[i] = _queue[i - 1];
            _queue[at] = entry;
            _waiting++;
        }

        /// <summary>
        /// Folds the oldest ordinary waiting lines until one slot is free with the summary line
        /// in place. False when every waiting line names the viewing player.
        /// </summary>
        /// <remarks>
        /// A loop because the first fold may itself take the slot it freed: with no summary line
        /// waiting yet, the fold puts one in, and a second fold goes into that summary.
        /// </remarks>
        private bool MakeRoomToWait(float nowSeconds)
        {
            while (_waiting >= _queue.Length)
            {
                int ordinary = -1;
                for (int i = 0; i < _waiting; i++)
                {
                    if (_queue[i].Priority || _queue[i].Kind == KillfeedKind.Overflow) continue;
                    ordinary = i;
                    break;
                }

                if (ordinary < 0) return false;

                for (int j = ordinary + 1; j < _waiting; j++) _queue[j - 1] = _queue[j];
                _waiting--;
                AddToOverflow(1, nowSeconds);
            }

            return true;
        }

        /// <summary>
        /// Adds <paramref name="count"/> to the overflow line waiting in the queue, or puts one at
        /// the head behind any priority lines: the folded lines were the oldest waiting, so their
        /// summary goes next.
        /// </summary>
        private void AddToOverflow(int count, float nowSeconds)
        {
            FoldedLines += count;

            for (int i = 0; i < _waiting; i++)
            {
                if (_queue[i].Kind != KillfeedKind.Overflow) continue;
                _queue[i] = _queue[i].WithFoldedCount(_queue[i].SubjectIndex + count);
                return;
            }

            if (_waiting == _queue.Length) return;

            KillfeedEntry overflow = KillfeedEntry.Overflow(count, nowSeconds).WithSequence(++_lastSequence);

            int at = 0;
            while (at < _waiting && _queue[at].Priority) at++;

            for (int i = _waiting; i > at; i--) _queue[i] = _queue[i - 1];
            _queue[at] = overflow;
            _waiting++;
        }
    }
}
