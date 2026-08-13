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
        /// of each inventing their own. Real actors override it with boxes read from Dev A's
        /// rig; nothing in the resolution path depends on these numbers being the real ones.
        /// </remarks>
        public static HitboxSet Humanoid(in Vec3 feetPosition, float scale = 1f)
        {
            // Copied out of the `in` parameter: a local function cannot capture one.
            float x = feetPosition.X, baseY = feetPosition.Y, z = feetPosition.Z;

            Vec3 At(float y) => new Vec3(x, baseY + y * scale, z);

            return new HitboxSet(
                head: Aabb.FromSize(At(1.70f), new Vec3(0.24f, 0.24f, 0.24f) * scale),
                torso: Aabb.FromSize(At(1.20f), new Vec3(0.50f, 0.70f, 0.32f) * scale),
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
