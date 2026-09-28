using System;
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

        // ------------------------------------------------------------------ geometry
        //
        // MEASURED, NOT AUTHORED (playtest 2026-09-28, bug 5). The numbers below are read off the
        // character every client draws -- Ai Character Optimizations.prefab, the rig Remote Actor
        // Proxy.prefab renders -- by running its Animator in the standing and crouched idles and
        // reading the world bounds of its two "Hitbox"-layer colliders: Bone_004, the head (the
        // original game's x4 box), and Bone_002, the body (x1). Those two boxes are what the
        // original game's own bullets hit while a soldier is alive.
        //
        // The set they replace was written down rather than measured. It put the head box at
        // 1.58..1.82 m with a 0.24 m footprint over a head the player sees at 1.31..1.72 m and
        // 0.34 m wide, so a shot through the face landed in the torso box for 35 instead of 140;
        // and a torso 0.50 x 0.32 m across, fixed to the world axes, let the shots at a soldier's
        // flank miss the 0.70 x 0.55 m body the original counts. HitboxGeometryTests (EditMode)
        // re-measures the prefab and fails when these drift from it.

        /// <summary>Height above the feet of the head box's centre, standing, in metres.</summary>
        public const float HumanoidHeadCenterHeight = 1.52f;

        /// <summary>Full height of the head box, standing.</summary>
        /// <remarks>
        /// The head is the one box with a damage multiplier, so its size and its position are a
        /// balance decision: they are the original character's, measured, and nothing else may
        /// move them as a side effect.
        /// </remarks>
        public const float HumanoidHeadHeight = 0.42f;

        /// <summary>Width and depth of the head box. Square, so turning the body cannot resize it.</summary>
        public const float HumanoidHeadWidth = 0.34f;

        /// <summary>Height above the feet where the head box begins: 1.31 m.</summary>
        public const float HumanoidHeadBottomHeight =
            HumanoidHeadCenterHeight - HumanoidHeadHeight * 0.5f;

        /// <summary>Height above the feet where the body box begins, just below them.</summary>
        /// <remarks>The original body box starts at -0.04 m, so a shot at a boot still lands.</remarks>
        public const float HumanoidTorsoBottomHeight = -0.05f;

        /// <summary>
        /// Height above the feet where the body box ends — <b>defined as the head's lower
        /// edge</b>, not as a number of its own, so no band of a standing body between the two is
        /// covered by nothing (ledger X-24).
        /// </summary>
        public const float HumanoidTorsoTopHeight = HumanoidHeadBottomHeight;

        /// <summary>
        /// Height above the feet of the body box's centre, and the point a scripted shooter aims
        /// at.
        /// </summary>
        /// <remarks>
        /// Named because a second party needs it: a scripted shooter has to pick a point ON a body
        /// to aim at, and the centre is the one with margin on every side (ledger X-25). Derived
        /// from the box's own edges so the two cannot drift; <c>ScriptedAimTests</c> pins them
        /// equal.
        /// </remarks>
        public const float HumanoidTorsoCenterHeight =
            (HumanoidTorsoTopHeight + HumanoidTorsoBottomHeight) * 0.5f;

        /// <summary>The body box across the shoulders, in the actor's own frame.</summary>
        public const float HumanoidBodyWidth = 0.70f;

        /// <summary>The body box front to back, in the actor's own frame.</summary>
        public const float HumanoidBodyDepth = 0.55f;

        /// <summary>Crouched: the head box's centre above the feet, from the crouch idle.</summary>
        public const float HumanoidCrouchedHeadCenterHeight = 1.11f;

        /// <summary>Crouched: the head box's full height.</summary>
        public const float HumanoidCrouchedHeadHeight = 0.44f;

        /// <summary>Crouched: the head box's width and depth.</summary>
        public const float HumanoidCrouchedHeadWidth = 0.40f;

        /// <summary>Crouched: how far forward of the feet the head sits.</summary>
        /// <remarks>The crouch pose leans over the rifle; the head is not above the feet.</remarks>
        public const float HumanoidCrouchedHeadForward = 0.12f;

        /// <summary>Crouched: the head's offset to the body's right, from the same pose.</summary>
        public const float HumanoidCrouchedHeadRight = 0.05f;

        /// <summary>Crouched: the body box's width and depth, knees and shoulders included.</summary>
        public const float HumanoidCrouchedBodyWidth = 0.80f;

        /// <summary>Crouched: where the head box begins, and so where the body box ends.</summary>
        public const float HumanoidCrouchedHeadBottomHeight =
            HumanoidCrouchedHeadCenterHeight - HumanoidCrouchedHeadHeight * 0.5f;

        /// <summary>
        /// A standing humanoid at <paramref name="feetPosition"/>, facing +Z.
        /// </summary>
        /// <remarks>
        /// For the callers that only need "a person" -- tests and diagnostic probes. The server's
        /// own capture goes through the overload that takes the heading and the stance, because
        /// the boxes are measured from a body that has both.
        /// </remarks>
        public static HitboxSet Humanoid(in Vec3 feetPosition, float scale = 1f)
            => Humanoid(in feetPosition, 0f, crouching: false, scale);

        /// <summary>
        /// The character's hitbox set at <paramref name="feetPosition"/>, facing
        /// <paramref name="yawDegrees"/> (Unity's convention: 0 faces +Z, 90 faces +X), standing
        /// or crouched.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Turned with the body.</b> Each box is authored in the actor's own frame and stored as
        /// the axis-aligned box around the turned one — what <c>Collider.bounds</c> reports for
        /// the same box in the scene. A body is 0.70 m across the shoulders and 0.55 m front to
        /// back, and a footprint fixed to the world axes was wrong for one of those two views
        /// whatever it was set to.
        /// </para>
        /// <para>
        /// <b>Four boxes, one damage class for the body.</b> The original's body box runs from
        /// the boots to the chin at x1, so the legs box sits inside it and never wins a ray: a ray
        /// reaches the enclosing box first, and a tie goes to the earlier index. The arms box is
        /// wider than the body and no deeper, so only the few centimetres beyond the shoulders
        /// resolve as a limb; a shot at the chest from in front is a body hit.
        /// </para>
        /// </remarks>
        public static HitboxSet Humanoid(
            in Vec3 feetPosition, float yawDegrees, bool crouching, float scale = 1f)
        {
            float radians = yawDegrees * (float)(Math.PI / 180.0);
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);

            if (!crouching)
            {
                return new HitboxSet(
                    head: Box(in feetPosition, cos, sin, scale,
                        0f, HumanoidHeadCenterHeight, 0f,
                        HumanoidHeadWidth, HumanoidHeadHeight, HumanoidHeadWidth),
                    torso: Box(in feetPosition, cos, sin, scale,
                        0f, HumanoidTorsoCenterHeight, -0.03f,
                        HumanoidBodyWidth, HumanoidTorsoTopHeight - HumanoidTorsoBottomHeight,
                        HumanoidBodyDepth),
                    arms: Box(in feetPosition, cos, sin, scale,
                        0f, 1.15f, -0.03f,
                        0.84f, 0.36f, 0.50f),
                    legs: Box(in feetPosition, cos, sin, scale,
                        0f, 0.42f, -0.03f,
                        0.44f, 0.94f, 0.36f));
            }

            return new HitboxSet(
                head: Box(in feetPosition, cos, sin, scale,
                    HumanoidCrouchedHeadRight, HumanoidCrouchedHeadCenterHeight,
                    HumanoidCrouchedHeadForward,
                    HumanoidCrouchedHeadWidth, HumanoidCrouchedHeadHeight, HumanoidCrouchedHeadWidth),
                torso: Box(in feetPosition, cos, sin, scale,
                    0f, (HumanoidCrouchedHeadBottomHeight + HumanoidTorsoBottomHeight) * 0.5f, 0f,
                    HumanoidCrouchedBodyWidth,
                    HumanoidCrouchedHeadBottomHeight - HumanoidTorsoBottomHeight,
                    HumanoidCrouchedBodyWidth),
                arms: Box(in feetPosition, cos, sin, scale,
                    0f, 0.75f, 0f,
                    0.90f, 0.36f, 0.60f),
                legs: Box(in feetPosition, cos, sin, scale,
                    0f, 0.25f, 0f,
                    0.50f, 0.60f, 0.60f));
        }

        /// <summary>
        /// One box authored in the actor's frame (<paramref name="x"/> to the right,
        /// <paramref name="z"/> forward, <paramref name="y"/> up from the feet), turned by the
        /// heading and returned as the world-space axis-aligned box around it.
        /// </summary>
        private static Aabb Box(
            in Vec3 feet, float cos, float sin, float scale,
            float x, float y, float z, float width, float height, float depth)
        {
            float halfWidth = width * 0.5f * scale;
            float halfDepth = depth * 0.5f * scale;

            // Unity's heading: a local (x, z) turns to (x cos + z sin, -x sin + z cos).
            float worldX = (x * cos + z * sin) * scale;
            float worldZ = (-x * sin + z * cos) * scale;

            float extentX = MathF.Abs(cos) * halfWidth + MathF.Abs(sin) * halfDepth;
            float extentZ = MathF.Abs(sin) * halfWidth + MathF.Abs(cos) * halfDepth;

            return new Aabb(
                new Vec3(feet.X + worldX, feet.Y + y * scale, feet.Z + worldZ),
                new Vec3(extentX, height * 0.5f * scale, extentZ));
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
