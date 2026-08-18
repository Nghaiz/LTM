using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;

namespace Ironfront.Net.Replication.Interest
{
    /// <summary>
    /// Decides which actors go into which client's snapshot, and how often.
    /// architecture.md section 7.3, phase-02 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing inside 500 m is ever culled (phase-02 task 1, option 2).</b> The alternative
    /// — dropping an actor out of the set entirely and re-spawning it on return — needs a
    /// despawn/respawn handshake per (viewer, target) pair and produces a whole class of
    /// pop-in bugs where a client holds a stale actor at its last known position forever.
    /// Keeping everything at Far costs 48 actors x 4 Hz, which is under 2 KB/s, and makes
    /// that bug class unwriteable. Beyond 500 m an actor really is culled; a sniper scope is
    /// the exception, handled by the view-cone clause.
    /// </para>
    /// <para>
    /// <b>The rate limit is keyed on the snapshot index, not the server tick.</b> The
    /// simulation runs at 30 Hz and snapshots go out at 20, so consecutive snapshots are one
    /// or two ticks apart, not exactly one. Feeding the tick into "send every Nth" would make
    /// Mid (every 2nd) fire on almost every snapshot and Far (every 5th) fire at roughly 8 Hz
    /// instead of 4 — the rates in the architecture table would all be wrong, and wrong in a
    /// direction that still looks like it works.
    /// </para>
    /// <para>
    /// Allocation-free in the steady state. Both dictionaries are pre-sized and only ever
    /// <see cref="Dictionary{TKey,TValue}.Clear"/>ed, and the composite keys are packed into
    /// a <see cref="uint"/> rather than a tuple so no comparer is ever consulted for a
    /// reference type.
    /// </para>
    /// </remarks>
    public sealed class InterestManager
    {
        /// <summary>Under this distance, every snapshot. architecture.md section 7.3.</summary>
        public const float NearRadius = 60f;

        /// <summary>Under this distance, 10 Hz.</summary>
        public const float MidRadius = 150f;

        /// <summary>Under this distance, 4 Hz.</summary>
        public const float FarRadius = 300f;

        /// <summary>
        /// The real cull distance. Between <see cref="FarRadius"/> and this, actors stay at
        /// <see cref="InterestLevel.Far"/> rather than being dropped — see the type remarks.
        /// </summary>
        public const float CullRadius = 500f;

        /// <summary>
        /// Half-angle of the "still visible past the cull radius" cone, in degrees. Sized for
        /// a scoped rifle: anything a player can actually resolve at 500 m is inside it.
        /// </summary>
        public const float ViewConeHalfAngleDegrees = 15f;

        /// <summary>
        /// Snapshots between sends, indexed by <see cref="InterestLevel"/>. Near every
        /// snapshot (20 Hz), Mid every 2nd (10 Hz), Far every 5th (4 Hz). Index 0 is Culled
        /// and unused — <see cref="ShouldSend"/> returns before reading it.
        /// </summary>
        private static readonly int[] SendEveryN = { 0, 5, 2, 1 };

        private readonly Dictionary<uint, uint> _lastSentSnapshot;
        private readonly Dictionary<ushort, InterestLevel> _maxHumanLevel;

        /// <summary>
        /// actorId -> the snapshot index at which it was first seen dead. Phase-03 task 5,
        /// optimization 5.
        /// </summary>
        private readonly Dictionary<ushort, uint> _deadSinceSnapshot;

        // Per-level candidate buckets, rebuilt each BuildView call. Fixed capacity and reused,
        // so the shedding pass is as allocation-free as the single pass it replaced.
        private readonly int[] _nearBucket = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _midBucket = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _farBucket = new int[ProtocolConstants.MAX_ACTORS];
        private int _nearCount;
        private int _midCount;
        private int _farCount;

        /// <summary>
        /// The widest one actor can encode to: every field present, seat info included.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Deliberately the worst case rather than the real one.</b> The actual width depends
        /// on the change mask, which <see cref="DeltaEncoder"/> computes later against a
        /// baseline this class has never seen. Projecting optimistically and being wrong means
        /// the encode overruns and the whole snapshot is discarded — the exact failure this
        /// shedding exists to remove.
        /// </para>
        /// <para>
        /// <b>This moved 20 → 23 when the seat field was finished, and the cost is real.</b> Any
        /// actor may now be seated, so the pessimistic width has to include the 3-byte seat
        /// field or the projection stops being pessimistic. The budget admits <b>50</b> actors
        /// where it used to admit 58 (<c>(1178 − 13) / 23 = 50</c>), and the vehicle snapshot
        /// riding in the same datagram takes that as low as 29 in the worst case. The 48-actor
        /// case the game actually ships still never sheds; the margin above it is gone, which
        /// is why <c>InterestManagementTests</c> pins the ceiling as a number rather than
        /// leaving a bandwidth regression to find it.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// Public because it is the number V4's budget split has to hit and the number the
        /// shedding tests pin. A private copy would leave both of those restating the formula,
        /// which is how the 20 in this class and the 20 in the spec drifted apart in the first
        /// place.
        /// </remarks>
        public static readonly int MaxEntrySize =
            SnapshotMessage.EntrySize(SnapshotField.Full);

        private static readonly float CosViewConeHalfAngle =
            (float)Math.Cos(ViewConeHalfAngleDegrees * Math.PI / 180.0);

        public InterestManager()
        {
            // 16 viewers x 64 targets is the worst case the protocol allows (trap 2: this is
            // the dictionary that leaks if despawns are not forgotten).
            _lastSentSnapshot = new Dictionary<uint, uint>(
                ProtocolConstants.MAX_PLAYERS * ProtocolConstants.MAX_ACTORS);
            _maxHumanLevel = new Dictionary<ushort, InterestLevel>(ProtocolConstants.MAX_ACTORS);
            _deadSinceSnapshot = new Dictionary<ushort, uint>(ProtocolConstants.MAX_ACTORS);
        }

        /// <summary>
        /// Which of the phase-03 task-5 optimizations are on. Defaults to the shipped set.
        /// </summary>
        /// <remarks>
        /// Only the two that are expressible in the frozen v1 wire format are read here —
        /// <see cref="ReplicationConfig.UseVelocityCulling"/> and
        /// <see cref="ReplicationConfig.DropStaleDeadActors"/>. Both change <i>what</i> goes in
        /// a snapshot, never how a field is laid out, so a client built against the frozen spec
        /// decodes either setting without knowing which is in force. See
        /// <see cref="ReplicationConfig"/> for the two flags that are deliberately not honoured
        /// anywhere on the shipped path.
        /// </remarks>
        public ReplicationConfig Config { get; set; } = ReplicationConfig.Shipped;

        /// <summary>Actor slots dropped because the actor had been dead too long to be worth sending.</summary>
        public long EntriesDroppedDead { get; private set; }

        /// <summary>Velocity fields suppressed because the actor was below Near.</summary>
        public long VelocityFieldsCulled { get; private set; }

        /// <summary>Actor slots examined since construction. Denominator for the saving figure.</summary>
        public long EntriesConsidered { get; private set; }

        /// <summary>Slots written with fresh values — the ones that actually cost bandwidth.</summary>
        public long EntriesRefreshed { get; private set; }

        /// <summary>Slots skipped because the rate limit was not due. Free — nothing is sent.</summary>
        public long EntriesHeld { get; private set; }

        /// <summary>Slots dropped entirely — beyond the cull radius and out of the view cone.</summary>
        public long EntriesCulled { get; private set; }

        /// <summary>
        /// Slots that were due but did not fit the datagram budget. Phase-05 task 4.
        /// </summary>
        /// <remarks>
        /// <b>Watch this, not just "a snapshot was produced".</b> Shedding turns an overflow
        /// from a dropped snapshot into a degraded one, which is strictly better and also
        /// strictly quieter — a bandwidth regression that used to announce itself with a
        /// <c>LogError</c> would otherwise hide behind "it always sends something". The phase-05
        /// risk table's threshold: non-zero at 48 actors on Dustbowl is a failure, not a pass.
        /// </remarks>
        public long EntriesShed { get; private set; }

        /// <summary>Actors shed from the most recent <see cref="BuildView"/> call.</summary>
        public int LastViewShedCount { get; private set; }

        /// <summary>
        /// Percentage of actor slots that did not carry fresh state. This is the update-rate
        /// cut; the bandwidth figure phase-02 criterion 1 is graded on is measured in bytes by
        /// <c>Phase02MeasurementTests</c>, because the two are not the same number.
        /// </summary>
        public double RefreshSavingPercent => EntriesConsidered == 0
            ? 0.0
            : 100.0 * (EntriesConsidered - EntriesRefreshed) / EntriesConsidered;

        /// <summary>Live entries in the rate-limit table. Watch this for trap 2 leaks.</summary>
        public int TrackedPairCount => _lastSentSnapshot.Count;

        /// <summary>
        /// Starts a snapshot. Clears the per-snapshot "how interesting is this actor to any
        /// human" map that <see cref="BuildView"/> accumulates into.
        /// </summary>
        /// <remarks>
        /// Must be called once before the per-viewer <see cref="BuildView"/> calls, not once
        /// per viewer. Calling it per viewer would leave
        /// <see cref="MaxLevelAmongHumanPlayers"/> reporting only the last viewer's opinion,
        /// which silently strips hitbox history from every actor except the ones the final
        /// client in the list happens to be near.
        /// </remarks>
        public void BeginSnapshot() => _maxHumanLevel.Clear();

        /// <summary>
        /// Classifies <paramref name="target"/> from <paramref name="viewer"/>'s point of view.
        /// </summary>
        /// <remarks>
        /// Both arguments are quantized snapshot entries rather than raw floats, because that
        /// is what the server has at this point in the tick and re-deriving float positions
        /// would mean two sources of truth for where an actor is. The 6.25 cm position
        /// quantum is five thousand times finer than the smallest band edge, so it cannot
        /// change a classification that was not already on a knife edge.
        /// </remarks>
        public InterestLevel Evaluate(
            in ActorSnapshotEntry viewer, in ActorSnapshotEntry target)
        {
            if (viewer.ActorId == target.ActorId) return InterestLevel.Near;   // yourself

            Vec3 viewerPos = SnapshotBuilder.UnpackPosition(in viewer);
            Vec3 targetPos = SnapshotBuilder.UnpackPosition(in target);
            float d2 = (targetPos - viewerPos).SqrMagnitude;

            InterestLevel level;
            if (d2 < NearRadius * NearRadius) level = InterestLevel.Near;
            else if (d2 < MidRadius * MidRadius) level = InterestLevel.Mid;
            else if (d2 < CullRadius * CullRadius) level = InterestLevel.Far;
            else
            {
                // Past the cull radius a scoped rifle can still resolve a target, so the view
                // cone is the one thing that rescues an actor from being dropped. Inside the
                // cull radius the cone is irrelevant: everything there is already at least Far.
                return IsInViewCone(in viewer, in viewerPos, in targetPos)
                    ? InterestLevel.Far
                    : InterestLevel.Culled;
            }

            // Teammates are "at Mid or better" (architecture.md section 7.3) — a FLOOR, not a
            // cap. Returning Mid directly, before the distance ladder above, is the obvious way
            // to write it and it quietly demotes a teammate standing next to you from Near
            // (20 Hz) to Mid (10 Hz): the people whose movement you can see most precisely
            // would be the ones updating least often.
            if (viewer.Team == target.Team
                && level < InterestLevel.Mid
                && d2 < FarRadius * FarRadius)
                level = InterestLevel.Mid;

            return level;
        }

        /// <summary>
        /// Whether this (viewer, target) pair is due a send on <paramref name="snapshotIndex"/>.
        /// </summary>
        /// <remarks>
        /// Records the send as a side effect, so calling it twice for one pair in one snapshot
        /// answers true then false. That is deliberate — it makes double-sending an actor
        /// impossible rather than merely discouraged.
        /// </remarks>
        public bool ShouldSend(
            ushort viewerActorId, ushort targetActorId, InterestLevel level, uint snapshotIndex)
        {
            if (!IsDue(viewerActorId, targetActorId, level, snapshotIndex)) return false;

            RecordSend(viewerActorId, targetActorId, snapshotIndex);
            return true;
        }

        /// <summary>
        /// Whether this pair is due, <b>without</b> recording the send.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="ShouldSend"/> for the shedding path (phase-05 task 4), and
        /// the split is the whole reason shedding does not starve anyone. An actor that is
        /// dropped for lack of byte budget must not have consumed its rate slot: recording a
        /// send that never happened would make a Far actor wait a further five snapshots — a
        /// quarter of a second — every time it lost the budget race, and the actors most likely
        /// to lose it are the same ones every snapshot.
        /// </remarks>
        public bool IsDue(
            ushort viewerActorId, ushort targetActorId, InterestLevel level, uint snapshotIndex)
        {
            if (level == InterestLevel.Culled) return false;

            int everyN = SendEveryN[(int)level];
            uint key = PackPair(viewerActorId, targetActorId);

            if (!_lastSentSnapshot.TryGetValue(key, out uint last)) return true;

            // Signed distance, not `snapshotIndex - last`: an unsigned subtraction turns
            // "the recorded index is somehow ahead of us" into a two-billion gap that
            // passes every threshold, which would pin the pair to sending every snapshot.
            return SequenceMath.Distance32(snapshotIndex, last) >= everyN;
        }

        /// <summary>Marks this pair as sent on <paramref name="snapshotIndex"/>.</summary>
        public void RecordSend(ushort viewerActorId, ushort targetActorId, uint snapshotIndex)
            => _lastSentSnapshot[PackPair(viewerActorId, targetActorId)] = snapshotIndex;

        /// <summary>
        /// Fills <paramref name="destination"/> with the actors <paramref name="viewerActorId"/>
        /// should receive this snapshot.
        /// </summary>
        /// <param name="spawnGate">
        /// Optional trap-8 guard. When supplied, an actor whose S_SPAWN_ACTOR has not yet been
        /// sent to this viewer is held back, because a snapshot naming an actor the client has
        /// never been told about arrives as an id it cannot resolve.
        /// </param>
        /// <returns>False when the viewer is not in <paramref name="world"/> at all.</returns>
        public bool BuildView(
            ushort viewerActorId,
            WorldSnapshot world,
            uint snapshotIndex,
            WorldSnapshot destination,
            SpawnAckTracker? spawnGate = null)
            => BuildViewCore(
                viewerActorId, world, snapshotIndex, destination, spawnGate,
                byteBudget: 0, session: null);

        /// <summary>
        /// Fills <paramref name="destination"/> for one client, shedding actors rather than
        /// overflowing <paramref name="byteBudget"/>. Phase-05 task 4.
        /// </summary>
        /// <param name="byteBudget">
        /// The largest snapshot body that still fits one datagram — normally
        /// <c>ServerPayloadWriter.MaxSnapshotBodySize</c>. Zero or negative means unlimited,
        /// which is the pre-phase-05 behaviour.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Why shed here rather than fragment.</b> The transport does fragment, and it forces
        /// <c>PacketFlags.Reliable</c> on every fragment — which would turn snapshots
        /// reliable-ordered and introduce head-of-line blocking on the one channel whose entire
        /// design premise is that a late snapshot is worthless. Dropping the whole snapshot,
        /// which is what the loop did before this, is worse still: at 64 actors the client
        /// received nothing at all.
        /// </para>
        /// <para>
        /// <b>Deferring an actor is already a supported concept.</b> Rate-limited actors are
        /// omitted exactly this way, and the delta baseline is the client's <i>acked</i>
        /// snapshot rather than the previous one, so an omitted actor is picked up by a later
        /// delta with no special handling and no despawn/respawn handshake.
        /// </para>
        /// </remarks>
        public bool BuildView(
            ClientSession session,
            WorldSnapshot world,
            uint snapshotIndex,
            WorldSnapshot destination,
            SpawnAckTracker? spawnGate,
            int byteBudget)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            return BuildViewCore(
                session.ActorId, world, snapshotIndex, destination, spawnGate, byteBudget,
                session);
        }

        private bool BuildViewCore(
            ushort viewerActorId,
            WorldSnapshot world,
            uint snapshotIndex,
            WorldSnapshot destination,
            SpawnAckTracker? spawnGate,
            int byteBudget,
            ClientSession? session)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            int viewerIndex = world.IndexOf(viewerActorId);
            if (viewerIndex < 0) return false;

            ActorSnapshotEntry viewer = world.Actors[viewerIndex];

            destination.Clear();
            destination.ServerTick = world.ServerTick;

            LastViewShedCount = 0;

            // ---- Pass 1: classify, and bucket by interest level.
            //
            // Split from the emit pass so that shedding can honour D6 — lowest level first —
            // which a single pass in world order cannot: it would shed whichever actors
            // happened to be last in the registry, regardless of how close they are.
            _nearCount = 0;
            _midCount = 0;
            _farCount = 0;

            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry target = ref world.Actors[i];

                EntriesConsidered++;

                // Before the cull test, deliberately. An actor that dies while out of range
                // would otherwise never get a death time recorded, and would then be held in
                // full for three seconds the moment it came back into range — a corpse
                // reappearing at 20 Hz is the opposite of the optimization.
                TrackLiveness(in target, snapshotIndex);

                InterestLevel level = Evaluate(in viewer, in target);
                RecordHumanInterest(target.ActorId, level);

                if (level == InterestLevel.Culled)
                {
                    EntriesCulled++;
                    continue;
                }

                // Dropped BEFORE the spawn gate and the rate limit, because a body nobody is
                // going to move again should not consume either. Corpses are never synchronised
                // (AD-4) — the client runs its own ragdoll off S_DEATH — so every snapshot after
                // the first few is describing a pose that has not changed and will not.
                if (IsStaleDead(target.ActorId, snapshotIndex))
                {
                    EntriesDroppedDead++;
                    continue;
                }

                // Checked BEFORE the rate limit, so a gated actor does not burn its slot while
                // it waits. Consuming the slot here would mean an actor whose spawn goes out
                // one snapshot late then waits a further four before its first real update.
                if (spawnGate != null
                    && target.ActorId != viewerActorId
                    && !spawnGate.HasSpawnBeenSent(viewerActorId, target.ActorId))
                    continue;

                // The viewer is emitted first and unconditionally, so it is not bucketed —
                // otherwise a rotation could put it behind actors that then exhaust the budget,
                // and a client that cannot see itself is the one failure this must never have.
                if (target.ActorId == viewerActorId) continue;

                switch (level)
                {
                    case InterestLevel.Near: _nearBucket[_nearCount++] = i; break;
                    case InterestLevel.Mid:  _midBucket[_midCount++]   = i; break;
                    default:                 _farBucket[_farCount++]   = i; break;
                }
            }

            // ---- Pass 2: emit, highest interest first, until the budget runs out.
            int remaining = byteBudget > 0
                ? byteBudget - SnapshotHeader.Size
                : int.MaxValue;

            Emit(viewerActorId, world, viewerIndex, InterestLevel.Near, snapshotIndex,
                 destination, ref remaining);

            int cursor = session?.ShedCursor ?? 0;

            int admitted = EmitBucket(
                viewerActorId, world, _nearBucket, _nearCount, InterestLevel.Near,
                snapshotIndex, destination, cursor, ref remaining);

            admitted += EmitBucket(
                viewerActorId, world, _midBucket, _midCount, InterestLevel.Mid,
                snapshotIndex, destination, cursor, ref remaining);

            admitted += EmitBucket(
                viewerActorId, world, _farBucket, _farCount, InterestLevel.Far,
                snapshotIndex, destination, cursor, ref remaining);

            // The cursor only moves when something was actually shed, and it moves by however
            // many actors got through. That slides the admission window forward each snapshot,
            // so the actors that lost this round are at the front of the next one — which is
            // what turns "some actors are dropped" into "every actor arrives within a bounded
            // number of snapshots" (D6). Advancing unconditionally would rotate a view that
            // fits comfortably, re-ordering entries for no reason.
            if (session != null && LastViewShedCount > 0)
                session.ShedCursor = cursor + (admitted > 0 ? admitted : 1);

            return true;
        }

        /// <summary>
        /// Emits one bucket, starting <paramref name="cursor"/> entries in and wrapping.
        /// </summary>
        /// <returns>How many actors were admitted.</returns>
        private int EmitBucket(
            ushort viewerActorId, WorldSnapshot world, int[] bucket, int count,
            InterestLevel level, uint snapshotIndex, WorldSnapshot destination,
            int cursor, ref int remaining)
        {
            if (count == 0) return 0;

            int start = ((cursor % count) + count) % count;   // negative-safe
            int admitted = 0;

            for (int k = 0; k < count; k++)
            {
                int index = bucket[(start + k) % count];

                // Budget checked BEFORE due-ness, and that ordering is the anti-starvation
                // property. Asking ShouldSend first would record a send for an actor that is
                // then shed, so it would wait a further full period on top of losing this
                // round. Every entry costs the same worst-case width, so once one does not fit
                // none will — everything left in this bucket, and in every lower one, is shed.
                if (remaining < MaxEntrySize)
                {
                    LastViewShedCount += count - k;
                    EntriesShed += count - k;
                    return admitted;
                }

                if (Emit(viewerActorId, world, index, level, snapshotIndex, destination,
                         ref remaining))
                    admitted++;
            }

            return admitted;
        }

        /// <summary>Emits one actor if it is due. Returns whether it was written.</summary>
        private bool Emit(
            ushort viewerActorId, WorldSnapshot world, int index, InterestLevel level,
            uint snapshotIndex, WorldSnapshot destination, ref int remaining)
        {
            ref ActorSnapshotEntry target = ref world.Actors[index];

            // Not due: omitted from this snapshot entirely. The client keeps the last
            // position it was told, which is what a reduced update rate means.
            //
            // The tempting alternative is to keep the actor in the view at its previously
            // sent values, so the change mask comes out empty and it costs 3 bytes instead
            // of being absent from the baseline and needing a full 20-byte re-send later.
            // That reasoning is wrong, and measurably so. The baseline is the client's
            // ACKED snapshot, roughly two behind, not the previous one — so a held entry
            // usually still differs from the baseline and encodes a full delta anyway.
            // Holding therefore pays 12 bytes every snapshot where omitting pays 12 bytes
            // every second or fifth. Measured over 30 s at 48 actors: omitting cut
            // bandwidth 25.5%, holding cut it 11.0%.
            if (!IsDue(viewerActorId, target.ActorId, level, snapshotIndex))
            {
                EntriesHeld++;
                return false;
            }

            // A copy, because the outgoing entry may differ from the world's. Mutating
            // world.Actors[index] in place would apply one viewer's culling decision to every
            // other viewer in the same snapshot — a client standing next to an actor would
            // lose its velocity because somebody else was far from it.
            ActorSnapshotEntry outgoing = target;

            if (Config.UseVelocityCulling && level < InterestLevel.Near
                && (outgoing.VelX != 0 || outgoing.VelY != 0 || outgoing.VelZ != 0))
            {
                // Zeroed rather than omitted: the change mask is computed against the
                // client's acked baseline, which also went through this filter, so writing
                // the same zero every snapshot clears the Velocity bit for free. Omission
                // is not expressible here at all — the encoder derives the mask, it is not
                // handed one.
                outgoing.VelX = 0;
                outgoing.VelY = 0;
                outgoing.VelZ = 0;
                VelocityFieldsCulled++;
            }

            if (!destination.Add(in outgoing)) return false;   // full; MAX_ACTORS is the fence

            RecordSend(viewerActorId, target.ActorId, snapshotIndex);
            remaining -= MaxEntrySize;
            EntriesRefreshed++;
            return true;
        }

        /// <summary>
        /// The highest interest any human viewer had in this actor during the current snapshot.
        /// </summary>
        /// <remarks>
        /// This is the input to the two optimizations that pay for interest management twice
        /// over: the hitbox-history relevance filter (protocol-spec.md section 7.3, risk R6)
        /// and the bot AI LOD scheduler. Both ask the same question — "could a real player
        /// plausibly interact with this actor right now" — so both read it from here rather
        /// than recomputing distances. They do NOT use the same threshold: see
        /// <see cref="ShootableThreshold"/> and <see cref="BotLodScheduler.FullRateThreshold"/>.
        /// </remarks>
        public InterestLevel MaxLevelAmongHumanPlayers(ushort actorId)
            => _maxHumanLevel.TryGetValue(actorId, out InterestLevel level)
                ? level
                : InterestLevel.Culled;

        /// <summary>
        /// The interest level at or above which an actor needs hitbox history kept for it
        /// (protocol-spec.md section 7.3's mandatory R6 optimization).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Far, not Mid — and the difference is a gameplay bug, not a tuning preference.</b>
        /// Both the phase-02 task document and protocol-spec.md section 7.3 say to keep history
        /// for actors in the "Near/Mid zone" of a real player. Mid ends at
        /// <see cref="MidRadius"/> = 150 m. A rifle's range is 300 m. So a target between 150 m
        /// and 300 m is inside every weapon that can reach it and outside the filter that
        /// decides whether it can be rewound — it silently falls back to its present pose, and
        /// the high-ping player who is supposed to be compensated simply misses. There is no
        /// error and no log; the shot just does not land.
        /// </para>
        /// <para>
        /// Far reaches <see cref="CullRadius"/> = 500 m, past any weapon in scope, so the
        /// filter now covers everything that could actually be shot — which is what the spec
        /// asks for in words even though its own zone table does not deliver it. The
        /// optimization survives: actors beyond 500 m from every human, and actors no human is
        /// near at all, are still skipped.
        /// </para>
        /// </remarks>
        public const InterestLevel ShootableThreshold = InterestLevel.Far;

        /// <summary>
        /// Whether hitbox history should be captured for this actor on the current snapshot.
        /// </summary>
        /// <remarks>
        /// The R6 filter, in one place. It lived at the call site while nothing called it, and
        /// "the filter is the caller's business" is only a good separation while there is
        /// exactly one caller getting it right.
        /// </remarks>
        public bool IsShootable(ushort actorId)
            => MaxLevelAmongHumanPlayers(actorId) >= ShootableThreshold;

        /// <summary>
        /// Drops every rate-limit entry mentioning this actor, as viewer or as target.
        /// </summary>
        /// <remarks>
        /// <b>Trap 2.</b> The pair table holds up to 16 x 64 entries, which is fine; what is
        /// not fine is that ids keep being allocated as players and bots come and go, so
        /// without this the table grows for the whole match and never shrinks. Call it from
        /// the despawn path, not from a periodic sweep — a sweep needs a liveness oracle this
        /// class has no business owning.
        /// </remarks>
        /// <summary>
        /// Notes when an actor was first seen dead, and forgets it the moment it revives.
        /// </summary>
        /// <remarks>
        /// Called once per (viewer, target) pair and therefore several times per actor per
        /// snapshot, so it is written to be idempotent within a snapshot: the first viewer
        /// records the index and the rest find it already there. Recording per snapshot instead
        /// would need a separate pass over the world, for a dictionary write that costs less
        /// than the pass would.
        /// </remarks>
        private void TrackLiveness(in ActorSnapshotEntry entry, uint snapshotIndex)
        {
            if (!Config.DropStaleDeadActors) return;

            bool alive = (entry.StateFlags & ActorStateFlags.IsAlive) != 0;

            if (alive)
            {
                // Respawn. Must clear, or the actor is dropped from every snapshot the instant
                // it comes back — invisible player, no error anywhere.
                if (_deadSinceSnapshot.Count != 0) _deadSinceSnapshot.Remove(entry.ActorId);
                return;
            }

            if (!_deadSinceSnapshot.ContainsKey(entry.ActorId))
                _deadSinceSnapshot[entry.ActorId] = snapshotIndex;
        }

        /// <summary>
        /// Whether this actor has been dead long enough to stop sending.
        /// </summary>
        private bool IsStaleDead(ushort actorId, uint snapshotIndex)
        {
            if (!Config.DropStaleDeadActors) return false;
            if (!_deadSinceSnapshot.TryGetValue(actorId, out uint deadSince)) return false;

            int held = (int)(ProtocolConstants.SNAPSHOT_RATE * Config.DeadActorHoldSeconds);

            // Signed distance for the same reason ShouldSend uses it: an unsigned subtraction
            // turns a recorded index that is somehow ahead of us into a two-billion gap that
            // passes every threshold, which here would drop a freshly killed actor instantly.
            return SequenceMath.Distance32(snapshotIndex, deadSince) >= held;
        }

        public void Forget(ushort actorId)
        {
            _maxHumanLevel.Remove(actorId);
            _deadSinceSnapshot.Remove(actorId);

            // Materialised into a list first: removing from a Dictionary while enumerating it
            // is undefined. This runs once per despawn, not per tick.
            List<uint>? doomed = null;
            foreach (KeyValuePair<uint, uint> pair in _lastSentSnapshot)
            {
                ushort viewer = (ushort)(pair.Key >> 16);
                ushort target = (ushort)(pair.Key & 0xFFFF);
                if (viewer != actorId && target != actorId) continue;

                doomed ??= new List<uint>();
                doomed.Add(pair.Key);
            }

            if (doomed == null) return;
            for (int i = 0; i < doomed.Count; i++) _lastSentSnapshot.Remove(doomed[i]);
        }

        /// <summary>Forgets every pair and every accumulated statistic.</summary>
        public void Reset()
        {
            _lastSentSnapshot.Clear();
            _maxHumanLevel.Clear();
            _deadSinceSnapshot.Clear();
            EntriesConsidered = 0;
            EntriesRefreshed = 0;
            EntriesHeld = 0;
            EntriesCulled = 0;
            EntriesDroppedDead = 0;
            EntriesShed = 0;
            LastViewShedCount = 0;
            VelocityFieldsCulled = 0;
        }

        /// <summary>
        /// Records that some human viewer sees this actor at <paramref name="level"/>, keeping
        /// the highest seen this snapshot.
        /// </summary>
        private void RecordHumanInterest(ushort actorId, InterestLevel level)
        {
            if (_maxHumanLevel.TryGetValue(actorId, out InterestLevel existing)
                && existing >= level)
                return;

            _maxHumanLevel[actorId] = level;
        }

        private static bool IsInViewCone(
            in ActorSnapshotEntry viewer, in Vec3 viewerPos, in Vec3 targetPos)
        {
            Vec3 toTarget = (targetPos - viewerPos).Normalized;
            if (toTarget.SqrMagnitude < 1e-6f) return true;   // co-located; not a cone question

            // Yaw only. Pitch would make the cone a true circle, but a scope's vertical travel
            // is small next to a 15-degree half-angle and including it costs a second
            // trig pair per pair per snapshot for no classification it would change.
            float yawRadians = (float)(Quantize.UnpackYaw(viewer.Yaw) * Math.PI / 180.0);
            var forward = new Vec3((float)Math.Sin(yawRadians), 0f, (float)Math.Cos(yawRadians));

            var flatToTarget = new Vec3(toTarget.X, 0f, toTarget.Z);
            flatToTarget = flatToTarget.Normalized;

            float dot = forward.X * flatToTarget.X + forward.Z * flatToTarget.Z;
            return dot >= CosViewConeHalfAngle;
        }

        /// <summary>
        /// Packs a (viewer, target) pair into one key.
        /// </summary>
        /// <remarks>
        /// A <c>(ushort, ushort)</c> tuple key would work and would be slower: the default
        /// comparer for a ValueTuple goes through <see cref="System.Collections.Generic.EqualityComparer{T}"/>
        /// per component. A packed uint hashes in one instruction and cannot allocate.
        /// </remarks>
        private static uint PackPair(ushort viewer, ushort target)
            => ((uint)viewer << 16) | target;
    }
}
