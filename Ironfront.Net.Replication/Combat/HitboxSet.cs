using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// One actor's four hitboxes in world space: head, torso, arms, legs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four named fields rather than a <c>Aabb[]</c>. The array form is what phase-02 task 2
    /// sketches, and it forces the <c>AllocBounds</c> dance the task document then has to warn
    /// about — 48 actors x 30 ticks of arrays that must be allocated exactly once or the
    /// server produces 5,760 array allocations a second. A fixed struct has nothing to
    /// allocate, so the warning has nothing to be about.
    /// </para>
    /// <para>
    /// Cost per stored frame is 4 x 24 bytes of box plus the frame header, and the whole
    /// history at 48 actors x 30 ticks lands near 160 KB — matching the estimate in the task
    /// document.
    /// </para>
    /// </remarks>
    public readonly struct HitboxSet
    {
        /// <summary>Boxes per actor. Fixed by this struct's shape.</summary>
        public const int Count = 4;

        public readonly Aabb Head;
        public readonly Aabb Torso;
        public readonly Aabb Arms;
        public readonly Aabb Legs;

        public HitboxSet(in Aabb head, in Aabb torso, in Aabb arms, in Aabb legs)
        {
            Head = head;
            Torso = torso;
            Arms = arms;
            Legs = legs;
        }

        /// <summary>Box by index, in the order head, torso, arms, legs.</summary>
        public Aabb this[int index] => index switch
        {
            0 => Head,
            1 => Torso,
            2 => Arms,
            _ => Legs,
        };

        /// <summary>
        /// The damage class of box <paramref name="index"/>.
        /// </summary>
        /// <remarks>
        /// Arms and legs both map to <see cref="HitboxType.Limb"/>: the wire enum
        /// (protocol-spec.md section 4.5) has three values, not four, so a client cannot tell
        /// an arm from a leg and the server must not pretend otherwise.
        /// </remarks>
        public static HitboxType TypeOf(int index) => index switch
        {
            0 => HitboxType.Head,
            1 => HitboxType.Body,
            _ => HitboxType.Limb,
        };

        /// <summary>
        /// A plausible humanoid hitbox set standing at <paramref name="feetPosition"/>.
        /// </summary>
        /// <remarks>
        /// Exists so tests and the bootstrap have one definition of "roughly a person" instead
        /// of each inventing their own. Real actors override it with boxes read from the client track's
        /// rig; nothing in the resolution path depends on these numbers being the real ones.
        /// </remarks>
        /// <summary>
        /// Height above the feet of <see cref="Humanoid"/>'s torso box centre, in metres, and the
        /// point a scripted shooter aims at.
        /// </summary>
        /// <remarks>
        /// Named because a second party needs it: a scripted shooter has to pick a point ON a
        /// body to aim at, and the only aim point with margin on every side is this one — the
        /// torso is 0.73 m tall, so its nearest edge is 0.365 m away. Ledger X-25 is what
        /// happens without it: the harness aimed at feet + <c>EYE_HEIGHT</c> (1.6 m), which is
        /// 0.02 m inside the head box's lower edge, and every lane-B combat shot was a coin
        /// toss against the 1.550..1.580 gap X-24 names.
        ///
        /// It is a constant here rather than a literal at the aim site so the two cannot drift:
        /// move the torso box and the shooter follows it.
        ///
        /// <b>Derived from the box's own edges since X-24</b>, where it read <c>1.20f</c> and the
        /// box's centre read 1.20 m by coincidence of two independently-authored numbers. Raising
        /// the torso's top edge to close the seam moved the centre to 1.215 m, and a literal here
        /// would have left the shooter aiming 1.5 cm off the centre it claims to name — a drift
        /// of exactly the kind this constant exists to prevent. <c>ScriptedAimTests</c> pins the
        /// two as equal.
        /// </remarks>
        public const float HumanoidTorsoCenterHeight =
            (HumanoidTorsoTopHeight + HumanoidTorsoBottomHeight) * 0.5f;

        /// <summary>Height above the feet of the head box's centre, in metres.</summary>
        public const float HumanoidHeadCenterHeight = 1.70f;

        /// <summary>Full height of the head box, in metres.</summary>
        /// <remarks>
        /// The head is the one box with a damage multiplier, so its size and its position are a
        /// balance decision and nothing else may move them as a side effect. X-24's fix raised
        /// the torso to meet this box; it did not touch this box. See
        /// <see cref="HumanoidTorsoTopHeight"/>.
        /// </remarks>
        public const float HumanoidHeadHeight = 0.24f;

        /// <summary>Height above the feet where the head box begins: 1.58 m.</summary>
        public const float HumanoidHeadBottomHeight =
            HumanoidHeadCenterHeight - HumanoidHeadHeight * 0.5f;

        /// <summary>Height above the feet where the torso box begins, in metres.</summary>
        public const float HumanoidTorsoBottomHeight = 0.85f;

        /// <summary>
        /// Height above the feet where the torso box ends — <b>defined as the head's lower
        /// edge</b>, not as a number of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ledger row X-24.</b> The torso used to be authored as (centre 1.20, height 0.70)
        /// and the head as (centre 1.70, height 0.24), which put the torso's top at 1.550 m and
        /// the head's bottom at 1.580 m — <b>3 cm of a standing body, at chest-to-chin height,
        /// covered by nothing.</b> A ray through it struck no box, so
        /// <see cref="LagCompensator.ResolveHitscan"/> returned a miss BEFORE the occlusion test
        /// ever ran, and a human aiming at the same band got the same nothing.
        /// </para>
        /// <para>
        /// <b>Why the torso moved and not the head.</b> Lowering the head's lower edge by 3 cm
        /// would have made the multiplier box 12.5% taller and moved where a headshot begins —
        /// a balance change to the most sensitive box in the set. Raising the torso puts the
        /// neck into <see cref="HitboxType.Body"/>, which is where a player aiming at a neck
        /// expects a body hit, and leaves headshot geometry exactly where it was. A fifth neck
        /// box was the third option and is worse than both: the wire enum has three values
        /// (protocol-spec.md section 4.5), so a neck would have to be reported as one of these
        /// anyway.
        /// </para>
        /// <para>
        /// <b>Derived rather than written down</b>, so the seam cannot reopen: move the head and
        /// the torso follows it. A future set that re-authors the head from a real rig gets the
        /// coverage for free.
        /// </para>
        /// </remarks>
        public const float HumanoidTorsoTopHeight = HumanoidHeadBottomHeight;

        public static HitboxSet Humanoid(in Vec3 feetPosition, float scale = 1f)
        {
            // Copied out of the `in` parameter: a local function cannot capture one.
            float x = feetPosition.X, baseY = feetPosition.Y, z = feetPosition.Z;

            Vec3 At(float y) => new Vec3(x, baseY + y * scale, z);

            const float torsoHeight = HumanoidTorsoTopHeight - HumanoidTorsoBottomHeight;

            return new HitboxSet(
                head: Aabb.FromSize(
                    At(HumanoidHeadCenterHeight),
                    new Vec3(0.24f, HumanoidHeadHeight, 0.24f) * scale),
                torso: Aabb.FromSize(At(HumanoidTorsoCenterHeight), new Vec3(0.50f, torsoHeight, 0.32f) * scale),
                arms: Aabb.FromSize(At(1.25f), new Vec3(0.80f, 0.60f, 0.26f) * scale),
                legs: Aabb.FromSize(At(0.45f), new Vec3(0.40f, 0.90f, 0.30f) * scale));
        }

        /// <summary>Moves every box by <paramref name="offset"/>. Used to place a rewound pose.</summary>
        public HitboxSet Translated(in Vec3 offset)
            => new HitboxSet(
                new Aabb(Head.Center + offset, Head.Extents),
                new Aabb(Torso.Center + offset, Torso.Extents),
                new Aabb(Arms.Center + offset, Arms.Extents),
                new Aabb(Legs.Center + offset, Legs.Extents));
    }
}
