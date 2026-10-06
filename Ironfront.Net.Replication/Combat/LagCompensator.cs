using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// Resolves a hitscan shot against the world as the shooter was seeing it, not as the
    /// server currently has it. protocol-spec.md section 7, phase-02 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is moved, so nothing has to be restored — trap 3 is structural here.</b> The
    /// task document's shape is: save every actor's transform, push the hitboxes into the
    /// past, raycast, and restore in a <c>finally</c>, with a warning that forgetting the
    /// <c>finally</c> leaves every hitbox stuck in the past permanently and produces bullets
    /// that hit empty air minutes later. That failure mode only exists because the rewound
    /// pose is written into shared mutable state. <see cref="HitboxHistory"/> stores
    /// world-space boxes, so the rewound pose is a value this method reads and the live world
    /// is never touched. There is no restore step to forget, and an exception thrown anywhere
    /// in here — including out of <see cref="Occlusion"/> — cannot corrupt anything.
    /// </para>
    /// <para>
    /// <b>Trap 5 — the shooter is never rewound</b>, on foot. A player predicts their own
    /// body, so the eye they aimed from is the server's present one; the ray's origin and
    /// direction come from the caller and are used as given. A PASSENGER is the exception:
    /// nobody predicts a vehicle but its driver, so a passenger's own seat is drawn as far in
    /// the past as everything else, and <see cref="TryMeasureOwnTravel"/> measures how far to
    /// move that origin back.
    /// </para>
    /// <para>
    /// <b>The interpolation constant is read, not assumed.</b>
    /// <see cref="ProtocolConstants.INTERP_BUFFER_MS"/> has to be the same number the client
    /// renders behind by. Hardcoding 150 here while the client used 100 would put every shot
    /// consistently to one side of a strafing target — the phase-02 risk table's
    /// "shots are systematically off to one side".
    /// </para>
    /// </remarks>
    public sealed class LagCompensator
    {
        private readonly HitboxHistory _history;

        public LagCompensator(HitboxHistory history)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
        }

        /// <summary>
        /// Optional line-of-sight test against world geometry, asked once per shot for the box
        /// that won; returns true when a wall is in the way. See <see cref="OcclusionQuery"/>
        /// for what the engine is told, including <b>the actor the point belongs to</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one thing an engine-free hit test genuinely cannot do is know where the walls
        /// are, so that single question is delegated and everything else stays testable. The
        /// Unity server assigns a <c>Physics.Linecast</c> against the world layer here once at
        /// bootstrap; leaving it null resolves shots with no occlusion, which is what the unit
        /// tests want and what a geometry-free test map gets.
        /// </para>
        /// <para>
        /// <b>The victim's actor id is in the query, and ledger row X-26 is why.</b> The
        /// endpoint of this query sits INSIDE the body that was hit, so the first thing a
        /// linecast meets there is that body's own collider. Measured on
        /// <c>artifacts/lane-b/x27-pinned-01..03</c>: all 34 occlusions across three runs were
        /// <c>collider=Bone_002 layer=8</c> at <c>frac=0.938..0.960</c> — a rig bone, at the
        /// endpoint, on a layer the <c>-2049</c> mask includes. Not one was terrain and not one
        /// was a building. The body was rejecting the shot that hit it, and it did so more the
        /// closer the pair got.
        /// </para>
        /// <para>
        /// <b>The shooter's actor id is too, for the same reason at the other end</b>
        /// (playtest 2026-09-28, bug 5). The origin sits inside the shooter's own rig, and a
        /// shot fired down a slope passes out through that body's own box on its way — so the
        /// shooter's body was cover for their own shots, exactly as the victim's was for the
        /// shot that hit it before X-26.
        /// </para>
        /// <para>
        /// Whose colliders those are is a question only the engine can answer, so this seam
        /// carries the ids and the Unity implementation decides. Nothing engine-free needs to
        /// know what a collider is, which is the whole point of the seam.
        /// </para>
        /// </remarks>
        public Func<OcclusionQuery, bool>? Occlusion { get; set; }

        /// <summary>Shots resolved. Denominator for the hit-rate experiment.</summary>
        public long ShotsResolved { get; private set; }

        /// <summary>Shots that found a target.</summary>
        public long ShotsHit { get; private set; }

        /// <summary>
        /// Times a target had no history frame at the rewind tick and its present pose was
        /// used instead. A high count means the relevance filter is dropping actors that are
        /// then being shot at.
        /// </summary>
        public long PresentFallbacks { get; private set; }

        /// <summary>Shots blocked by <see cref="Occlusion"/>.</summary>
        public long ShotsOccluded { get; private set; }

        /// <summary>
        /// The nearest box the last box-missing shot passed, and by how much. Ledger row X-24.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Last-write-wins, exactly like the Unity side's occlusion description, and for the
        /// same reason:</b> a shot resolves against every candidate but the log is one line per
        /// trigger frame. <see cref="NearestMissesMeasured"/> rises exactly when this is written,
        /// so a reader can date it — see <see cref="NearestMissFor"/>.
        /// </para>
        /// <para>
        /// Written only when the ray struck no box at all. A shot that found a box and was then
        /// rejected by <see cref="Occlusion"/> leaves this alone: it did not miss, it was blocked,
        /// and conflating the two is what made X-20 and X-24 one indistinguishable symptom for
        /// three runs.
        /// </para>
        /// </remarks>
        public HitboxMiss LastNearestMiss { get; private set; }

        /// <summary>How many times <see cref="LastNearestMiss"/> has been written.</summary>
        public long NearestMissesMeasured { get; private set; }

        /// <summary>
        /// How many ticks to rewind for a client at this round-trip time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The whole round trip, not half of it.</b> The world a shooter sees is already a
        /// one-way trip old when it arrives: <c>InterpolationClock</c> renders
        /// <see cref="ProtocolConstants.INTERP_BUFFER_MS"/> behind the NEWEST snapshot the client
        /// holds, and that snapshot left the server half a round trip ago. The fire message then
        /// takes the other half to get here, and waits for the next tick to be applied (half a
        /// tick on average). So the age of what was on screen when the trigger was pulled, seen
        /// from the tick that resolves the shot, is
        /// <c>rtt + INTERP_BUFFER_MS + MS_PER_TICK / 2</c>.
        /// </para>
        /// <para>
        /// This used <c>rtt / 2 + INTERP_BUFFER_MS</c> until 2026-09-29, and protocol-spec.md
        /// section 7.1's own sequence diagram shows the gap: the client "sees B at ~tick 297" and
        /// the formula rewinds to 301. Measured on the live Azure server from a client at
        /// ~100 ms: 1,384 inputs, the tick that applied each one minus the tick being rendered
        /// when it was generated, mean 7.1 ticks (an upper bound by about 0.75 tick, since a
        /// snapshot only brackets the tick that applied it) against 5 rewound. That missing
        /// ~60 ms is 0.3-0.4 m on a running soldier and more than a metre on a moving vehicle --
        /// shots at a drawn body missed and shots ahead of it landed (playtest 2026-09-28,
        /// bug 2).
        /// </para>
        /// <para>
        /// The clamp at <see cref="ProtocolConstants.MAX_REWIND_TICKS"/> is the anti-abuse limit
        /// from protocol-spec.md section 7.2, not a performance guard: a cheater who inflates
        /// their reported ping would otherwise be able to shoot arbitrarily far into the past.
        /// </para>
        /// </remarks>
        public static int RewindTicks(float smoothedRttMs)
        {
            if (float.IsNaN(smoothedRttMs) || smoothedRttMs <= 0f) smoothedRttMs = 0f;

            float rewindMs = smoothedRttMs
                             + ProtocolConstants.INTERP_BUFFER_MS
                             + ProtocolConstants.MS_PER_TICK * 0.5f;
            int ticks = (int)MathF.Round(rewindMs / ProtocolConstants.MS_PER_TICK);

            if (ticks < 0) ticks = 0;
            if (ticks > ProtocolConstants.MAX_REWIND_TICKS) ticks = ProtocolConstants.MAX_REWIND_TICKS;
            return ticks;
        }

        /// <summary>
        /// The tick a shot fired now by a client at this RTT should be resolved against.
        /// </summary>
        /// <remarks>
        /// Saturates at 0 rather than wrapping. In the opening ticks of a match
        /// <paramref name="currentTick"/> is smaller than the rewind, and an unsigned
        /// subtraction there produces a tick near four billion — which no history frame
        /// matches, so every opening shot would silently fall back to the present.
        /// </remarks>
        public static uint ResolveTargetTick(uint currentTick, float smoothedRttMs)
        {
            uint rewind = (uint)RewindTicks(smoothedRttMs);
            return currentTick > rewind ? currentTick - rewind : 0u;
        }

        /// <summary>
        /// How far the shooter's own body has moved since the tick a shot by a client at this
        /// RTT is judged against, read from its hitbox history: present minus past, to be
        /// SUBTRACTED from a present origin.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a shooter who sees their own body from an interpolated vehicle -- any seat but the
        /// driver's, whose vehicle alone is predicted -- the camera the trigger was pulled from
        /// rode the vehicle as it was <see cref="RewindTicks"/> ago, not as it is now. Firing from
        /// the present seat moved the muzzle by the vehicle's travel over the rewind: 2-3 m at
        /// jeep speed, which put a passenger's ray in FRONT of the driver sitting a metre ahead of
        /// them. Playtest 2026-09-28, bug 2: riding in a car a bot was driving, a magazine
        /// emptied into the driver did no damage.
        /// </para>
        /// <para>
        /// The body rides the seat rigidly, so its own displacement is the seat's. Translation
        /// only: the heading change over a rewind window is a few degrees, a decimetre at the
        /// eye. False, and no correction, when the shooter has no frame at that tick or is not
        /// among <paramref name="targets"/> -- the present origin is then the best available
        /// answer, as it is for a target.
        /// </para>
        /// <para>
        /// Asked once per trigger pull, not once per pellet: every pellet of a shotgun blast
        /// leaves the same muzzle.
        /// </para>
        /// </remarks>
        public bool TryMeasureOwnTravel(
            ReadOnlySpan<HitscanTarget> targets, ushort actorId,
            float smoothedRttMs, uint currentTick, out Vec3 travelled)
        {
            travelled = default;

            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly HitscanTarget self = ref targets[i];
                if (self.ActorId != actorId) continue;

                uint targetTick = ResolveTargetTick(currentTick, smoothedRttMs);
                if (!_history.TryGetFrame(actorId, targetTick, out HitboxHistory.Frame frame)) break;

                travelled = self.Present.Torso.Center - frame.Boxes.Torso.Center;
                OwnTravelMeasured++;
                return true;
            }

            OwnTravelUnmeasured++;
            return false;
        }

        /// <summary>Seated origins moved back to the tick the shooter was seeing.</summary>
        public long OwnTravelMeasured { get; private set; }

        /// <summary>Seated origins left at the present because the shooter had no history frame.</summary>
        public long OwnTravelUnmeasured { get; private set; }

        /// <summary>
        /// Fires one ray into the rewound world and returns the nearest actor it struck.
        /// </summary>
        /// <param name="targets">
        /// Candidate actors and their present poses. Reused caller-owned storage; nothing here
        /// retains it.
        /// </param>
        /// <param name="shooterActorId">Excluded from the sweep (trap 5).</param>
        /// <param name="origin">Muzzle position, at the server's present time.</param>
        /// <param name="direction">Fire direction. Normalized internally.</param>
        /// <param name="maxDistance">Weapon range in metres.</param>
        /// <param name="smoothedRttMs">The shooter's smoothed RTT. 0 for a bot.</param>
        /// <param name="currentTick">The server tick the shot is being processed on.</param>
        /// <param name="piercing">
        /// The round reaches crews in enclosed seats. Only the sniper's does, as in the original;
        /// see <see cref="HitscanTarget.InEnclosedSeat"/>.
        /// </param>
        /// <param name="shooterTravel">
        /// How far <paramref name="origin"/> was moved back from the shooter's present eye, from
        /// <see cref="TryMeasureOwnTravel"/>. Zero for a shooter on foot or driving. Only the
        /// occlusion query reads it: the ray itself already starts where it should.
        /// </param>
        public HitResult ResolveHitscan(
            ReadOnlySpan<HitscanTarget> targets,
            ushort shooterActorId,
            in Vec3 origin,
            in Vec3 direction,
            float maxDistance,
            float smoothedRttMs,
            uint currentTick,
            bool piercing = false,
            in Vec3 shooterTravel = default)
        {
            ShotsResolved++;

            uint targetTick = ResolveTargetTick(currentTick, smoothedRttMs);

            // Non-finite input is rejected before it can reach the slab test. `SqrMagnitude` is
            // NaN for a NaN direction and `NaN < 0.5f` is false, so the zero-direction guard
            // below waves it straight through — and Vec3.Normalized does not stop it either,
            // because `NaN < 1e-5f` is also false. See Aabb.Raycast for what that costs.
            if (!IsFinite(in origin) || !IsFinite(in direction)) return HitResult.Miss(targetTick);

            Vec3 ray = direction.Normalized;
            if (ray.SqrMagnitude < 0.5f) return HitResult.Miss(targetTick);   // zero direction

            bool found = FindNearest(
                targets, shooterActorId, in origin, in ray, maxDistance, targetTick, piercing,
                out float bestDistance, out ushort bestActor, out HitboxType bestType,
                out bool bestUsedFallback, out Vec3 bestTravel);

            if (!found)
            {
                MeasureNearestMiss(targets, shooterActorId, in origin, in ray, maxDistance, targetTick, piercing);
                return HitResult.Miss(targetTick);
            }

            Vec3 point = origin + ray * bestDistance;

            // Walls last: an occluded shot is a miss, and asking the engine about geometry is
            // the most expensive thing here, so it runs once for the winner rather than once
            // per candidate box.
            if (Occlusion != null
                && Occlusion(new OcclusionQuery(
                    in origin, in point, bestDistance, bestActor, shooterActorId,
                    in bestTravel, in shooterTravel)))
            {
                ShotsOccluded++;
                return HitResult.Miss(targetTick);
            }

            ShotsHit++;
            return new HitResult(
                true, bestActor, bestType, in point, bestDistance, targetTick, bestUsedFallback);
        }

        /// <summary>
        /// Records which box the shot came closest to, and by how much. Ledger row X-24.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A second pass, run only on a miss.</b> Folding it into the resolution loop would
        /// cost every HIT a closest-approach computation per box for a number nothing reads, and
        /// the resolution loop is the one this server runs per shot per candidate. A miss has
        /// already decided it has no answer, so the pass is free where it matters.
        /// </para>
        /// <para>
        /// <b>The same frame the resolver used, re-read rather than remembered.</b> Looking the
        /// history up again at the same tick cannot drift from what the raycast saw; carrying a
        /// per-candidate cache would introduce exactly the "the line printed one pose and the
        /// resolver used another" fork that cost three runs on X-19.
        /// </para>
        /// <para>
        /// <see cref="PresentFallbacks"/> is deliberately NOT incremented here. It counts
        /// resolution decisions; incrementing it from a diagnostic would double every fallback a
        /// missed shot took and quietly corrupt the one number that says whether the relevance
        /// filter is dropping actors people are shooting at.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Fires one round along a ballistic arc (<see cref="RoundBallistics"/>) into the rewound
        /// world and returns the nearest actor it struck: <see cref="ResolveHitscan"/> for a bullet
        /// that drops and slows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Owner request 2026-10-06</b>: every gun flies its own trajectory under gravity, by
        /// real physics. The arc is cut into chords <see cref="BallisticSegmentSeconds"/> of
        /// flight apart -- a chord of a falling path strays from it by g·dt²/8, 1.2 cm here, far
        /// inside any hitbox -- and each chord is swept like a hitscan ray against the same
        /// rewound poses, so the round meets the target where the shooter saw it, as a straight
        /// shot always has. The round's flight time is not added to the rewind: the shooter is
        /// favoured exactly as before, and holds over for the drop, not ahead for the travel.
        /// </para>
        /// <para>
        /// <b>Walls, only once something is hit.</b> A miss costs no engine query, as before; a
        /// hit is confirmed by asking the world about every chord up to it, so a round that came
        /// down on a ridge short of its target misses.
        /// </para>
        /// <para>
        /// <see cref="HitResult.Distance"/> is the length of the path flown, which is what the
        /// weapon's damage drop-off has always been measured on.
        /// </para>
        /// </remarks>
        /// <param name="launch">The direction the round leaves the muzzle, <see cref="RoundBallistics.LaunchDirection"/>.</param>
        /// <param name="range">The longest path, metres, the round may fly.</param>
        public HitResult ResolveBallistic(
            ReadOnlySpan<HitscanTarget> targets,
            ushort shooterActorId,
            in Vec3 muzzle,
            in Vec3 launch,
            in RoundBallistics round,
            float range,
            float smoothedRttMs,
            uint currentTick,
            bool piercing = false,
            in Vec3 shooterTravel = default)
        {
            if (!round.IsBallistic)
                return ResolveHitscan(
                    targets, shooterActorId, in muzzle, in launch, range, smoothedRttMs, currentTick,
                    piercing, in shooterTravel);

            ShotsResolved++;
            BallisticShots++;

            uint targetTick = ResolveTargetTick(currentTick, smoothedRttMs);

            if (!IsFinite(in muzzle) || !IsFinite(in launch)) return HitResult.Miss(targetTick);

            Vec3 direction = launch.Normalized;
            if (direction.SqrMagnitude < 0.5f) return HitResult.Miss(targetTick);

            Span<Vec3> points = stackalloc Vec3[MaxBallisticSegments + 1];
            points[0] = muzzle;
            int count = 1;
            float flown = 0f;

            for (int segment = 1; segment <= MaxBallisticSegments && flown < range; segment++)
            {
                Vec3 from = points[count - 1];
                Vec3 next = round.PositionAt(in muzzle, in direction, segment * BallisticSegmentSeconds);
                Vec3 chord = next - from;
                float length = chord.Magnitude;
                if (!(length > 1e-4f)) break;

                Vec3 along = chord * (1f / length);
                float allowed = range - flown;
                if (length > allowed)
                {
                    length = allowed;
                    next = from + along * length;
                }

                if (FindNearest(
                        targets, shooterActorId, in from, in along, length, targetTick, piercing,
                        out float distance, out ushort actor, out HitboxType type,
                        out bool usedFallback, out Vec3 victimTravel))
                {
                    Vec3 point = from + along * distance;

                    for (int i = 1; i < count; i++)
                    {
                        if (IsOccluded(in points[i - 1], in points[i], 0, shooterActorId, default, in shooterTravel))
                        {
                            ShotsOccluded++;
                            return HitResult.Miss(targetTick);
                        }
                    }

                    if (IsOccluded(in from, in point, actor, shooterActorId, in victimTravel, in shooterTravel))
                    {
                        ShotsOccluded++;
                        return HitResult.Miss(targetTick);
                    }

                    ShotsHit++;
                    return new HitResult(true, actor, type, in point, flown + distance, targetTick, usedFallback);
                }

                points[count++] = next;
                flown += length;
            }

            return HitResult.Miss(targetTick);
        }

        /// <summary>Seconds of flight between the chords <see cref="ResolveBallistic"/> sweeps.</summary>
        public const float BallisticSegmentSeconds = 0.1f;

        /// <summary>The most chords one round is swept in: 6.4 s of flight, past every gun's range.</summary>
        public const int MaxBallisticSegments = 64;

        /// <summary>Rounds resolved along a ballistic arc rather than a straight ray.</summary>
        public long BallisticShots { get; private set; }

        private bool IsOccluded(
            in Vec3 from, in Vec3 to, ushort victimActorId, ushort shooterActorId,
            in Vec3 victimTravel, in Vec3 shooterTravel)
        {
            if (Occlusion == null) return false;
            float length = (to - from).Magnitude;
            return Occlusion(new OcclusionQuery(
                in from, in to, length, victimActorId, shooterActorId, in victimTravel, in shooterTravel));
        }

        /// <summary>
        /// The nearest rewound hitbox <paramref name="ray"/> enters within
        /// <paramref name="maxDistance"/> of <paramref name="origin"/>, walls not considered.
        /// </summary>
        private bool FindNearest(
            ReadOnlySpan<HitscanTarget> targets,
            ushort shooterActorId,
            in Vec3 origin,
            in Vec3 ray,
            float maxDistance,
            uint targetTick,
            bool piercing,
            out float bestDistance,
            out ushort bestActor,
            out HitboxType bestType,
            out bool bestUsedFallback,
            out Vec3 bestTravel)
        {
            bool found = false;
            bestDistance = maxDistance;
            bestActor = 0;
            bestType = HitboxType.Body;
            bestUsedFallback = false;
            bestTravel = default;

            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly HitscanTarget target = ref targets[i];

                if (!target.IsAlive) continue;
                if (target.ActorId == shooterActorId) continue;   // trap 5

                // Not a target at all, rather than a hit for nothing: the hull around the seat is
                // what the round meets, and the occlusion test is what stops it there.
                if (target.InEnclosedSeat && !piercing) continue;

                HitboxSet boxes;
                bool usedFallback;
                Vec3 travel = default;

                if (_history.TryGetFrame(target.ActorId, targetTick, out HitboxHistory.Frame frame))
                {
                    boxes = frame.Boxes;
                    usedFallback = false;
                    travel = target.Present.Torso.Center - frame.Boxes.Torso.Center;
                }
                else
                {
                    // No frame: the actor was outside the relevance filter until moments ago,
                    // or the match just started. Resolving against the present is strictly
                    // better than declaring the target unhittable for up to a second.
                    boxes = target.Present;
                    usedFallback = true;
                    PresentFallbacks++;
                }

                for (int box = 0; box < HitboxSet.Count; box++)
                {
                    if (!boxes[box].Raycast(in origin, in ray, maxDistance, out float distance))
                        continue;

                    // Strictly nearer: on an exact tie the earlier box wins, and the boxes are
                    // ordered head-first, so a ray entering head and torso at the same depth
                    // resolves as the headshot it visually is.
                    if (found && distance >= bestDistance) continue;

                    found = true;
                    bestDistance = distance;
                    bestActor = target.ActorId;
                    bestType = HitboxSet.TypeOf(box);
                    bestUsedFallback = usedFallback;
                    bestTravel = travel;
                }
            }

            return found;
        }

        private void MeasureNearestMiss(
            ReadOnlySpan<HitscanTarget> targets, ushort shooterActorId,
            in Vec3 origin, in Vec3 ray, float maxDistance, uint targetTick, bool piercing)
        {
            float bestGap = float.PositiveInfinity;
            HitboxMiss best = HitboxMiss.None;

            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly HitscanTarget target = ref targets[i];

                if (!target.IsAlive) continue;
                if (target.ActorId == shooterActorId) continue;
                if (target.InEnclosedSeat && !piercing) continue;   // the resolver's own rule

                HitboxSet boxes = _history.TryGetFrame(target.ActorId, targetTick, out HitboxHistory.Frame frame)
                    ? frame.Boxes
                    : target.Present;

                for (int box = 0; box < HitboxSet.Count; box++)
                {
                    boxes[box].ClosestApproach(
                        in origin, in ray, maxDistance,
                        out float gap, out float vertical, out Vec3 point);

                    if (!(gap < bestGap)) continue;   // false for NaN and for infinity

                    bestGap = gap;
                    best = new HitboxMiss(
                        target.ActorId, box, HitboxSet.TypeOf(box), gap, vertical, in point);
                }
            }

            if (!best.Measured) return;

            LastNearestMiss = best;
            NearestMissesMeasured++;
        }

        /// <summary>
        /// The nearest-miss description belonging to THIS shot, or a stated absence.
        /// </summary>
        /// <remarks>
        /// <b><see cref="LastNearestMiss"/> is last-write-wins and is only written when a shot
        /// struck no box.</b> So a shot that hit, or one blocked by geometry, would otherwise
        /// print the previous shot's miss and the artifact would read as though a hit had missed.
        /// Comparing <see cref="NearestMissesMeasured"/> against its value at the previously
        /// logged shot says whether the description is this shot's or a leftover — the same
        /// dating <c>ServerTickLoop.OcclusionFor</c> does for occlusion, spelled the same way so
        /// a reader learns the pattern once.
        /// </remarks>
        public static string NearestMissFor(
            long measuredNow, long measuredAtLastLog, in HitboxMiss last)
        {
            if (measuredNow <= measuredAtLastLog) return "none-this-shot";

            return last.Describe();
        }

        /// <summary>True when every component is a real number.</summary>
        private static bool IsFinite(in Vec3 v)
            => !float.IsNaN(v.X) && !float.IsInfinity(v.X)
               && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y)
               && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);

        /// <summary>Hit rate over every shot resolved. The phase-02 experiment plots this against RTT.</summary>
        public double HitRatePercent => ShotsResolved == 0 ? 0.0 : 100.0 * ShotsHit / ShotsResolved;

        /// <summary>Zeroes the counters without touching the history.</summary>
        public void ResetStatistics()
        {
            ShotsResolved = 0;
            ShotsHit = 0;
            PresentFallbacks = 0;
            ShotsOccluded = 0;
            BallisticShots = 0;
            NearestMissesMeasured = 0;
            LastNearestMiss = HitboxMiss.None;
        }
    }
}
