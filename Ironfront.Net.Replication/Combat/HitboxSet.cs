using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The pose a hitbox set is shaped for: the pose every client draws the actor in, from the
    /// same snapshot fields.
    /// </summary>
    /// <remarks>
    /// Playtest 2026-09-28, bug 5. The remote proxy's animator is driven by <c>crouched</c>,
    /// <c>sprinting</c>, <c>seated</c> and <c>moving</c> (<c>RemoteActorView.Apply</c>), and each
    /// of those moves the head a measured distance: 12 cm lower at a run, 20 cm lower and 21 cm
    /// forward at a sprint, 17 cm HIGHER crouch-walking than crouched still, 57 cm lower seated.
    /// A server that shaped every body as a standing idle scored headshots on moving targets as
    /// body hits and missed the upper half of anyone crouch-walking outright.
    /// </remarks>
    public enum HumanoidPose : byte
    {
        Standing = 0,
        Moving = 1,
        Sprinting = 2,
        Crouched = 3,
        CrouchMoving = 4,
        Seated = 5,
    }

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
        // character every client draws -- Ai Character Optimizations.prefab, whose rig Remote
        // Actor Proxy.prefab shares bone for bone (their Head bones agree to the centimetre in
        // every pose) -- by running its Animator in each pose and reading the world bounds of
        // its two "Hitbox"-layer colliders: Bone_004, the head (the original game's x4 box), and
        // Bone_002, the body (x1). Moving poses are the mean over two seconds of the cycle, with
        // the cycle's own sway added to the half-extents: the server does not know which frame
        // of the stride a client is drawing, only which stride.
        //
        // The standing set they replace was written down rather than measured. It put the head
        // box at 1.58..1.82 m over a head the player sees at 1.31..1.72 m, so a shot through the
        // face landed in the torso box for 35 instead of 140. HitboxGeometryTests (EditMode)
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

        /// <summary>Crouched and still: the head box's centre above the feet.</summary>
        public const float HumanoidCrouchedHeadCenterHeight = 1.11f;

        /// <summary>Crouched and still: the head box's full height.</summary>
        public const float HumanoidCrouchedHeadHeight = 0.48f;

        /// <summary>Crouched and still: the head box's width and depth.</summary>
        public const float HumanoidCrouchedHeadWidth = 0.52f;

        /// <summary>Crouched and still: how far forward of the feet the head sits.</summary>
        /// <remarks>The crouch pose leans over the rifle; the head is not above the feet.</remarks>
        public const float HumanoidCrouchedHeadForward = 0.13f;

        /// <summary>Crouched and still: the head's offset to the body's right, from the same pose.</summary>
        public const float HumanoidCrouchedHeadRight = 0.13f;

        /// <summary>Crouched: the body box's width and depth, knees and shoulders included.</summary>
        public const float HumanoidCrouchedBodyWidth = 0.80f;

        /// <summary>Crouched and still: where the head box begins, and so where the body box ends.</summary>
        public const float HumanoidCrouchedHeadBottomHeight =
            HumanoidCrouchedHeadCenterHeight - HumanoidCrouchedHeadHeight * 0.5f;

        /// <summary>
        /// Seated: the head box's centre above the seat, in the chair pose every client draws a
        /// seated actor in.
        /// </summary>
        public const float HumanoidSeatedHeadCenterHeight = 0.95f;

        /// <summary>
        /// Moving: how far the head leads the direction of travel. Measured 0.10 m at a run
        /// forward or back, 0.06 m strafing.
        /// </summary>
        public const float HumanoidMovingHeadLead = 0.08f;

        /// <summary>
        /// One pose's measurements, in the actor's frame: x to its right, z forward, y up from
        /// the feet (from the seat, seated).
        /// </summary>
        private readonly struct Shape
        {
            public readonly float HeadX, HeadY, HeadZ;
            public readonly float HeadHalfX, HeadHalfY, HeadHalfZ;
            public readonly float Lead;
            public readonly float TorsoBottom, TorsoZ, TorsoHalfX, TorsoHalfZ;

            public Shape(
                float headX, float headY, float headZ,
                float headHalfX, float headHalfY, float headHalfZ,
                float lead,
                float torsoBottom, float torsoZ, float torsoHalfX, float torsoHalfZ)
            {
                HeadX = headX; HeadY = headY; HeadZ = headZ;
                HeadHalfX = headHalfX; HeadHalfY = headHalfY; HeadHalfZ = headHalfZ;
                Lead = lead;
                TorsoBottom = torsoBottom; TorsoZ = torsoZ; TorsoHalfX = torsoHalfX; TorsoHalfZ = torsoHalfZ;
            }

            public float HeadBottom => HeadY - HeadHalfY;
        }

        // Indexed by HumanoidPose. Head centres are the measured means; half-extents are the
        // measured bounds plus one standard deviation of the stride's sway.
        private static readonly Shape[] Shapes =
        {
            // Standing still: head 1.31..1.73, the body box 0.70 x 0.55 across.
            new Shape(0f, HumanoidHeadCenterHeight, 0f,
                HumanoidHeadWidth * 0.5f, HumanoidHeadHeight * 0.5f, HumanoidHeadWidth * 0.5f,
                0f, HumanoidTorsoBottomHeight, -0.03f, HumanoidBodyWidth * 0.5f, HumanoidBodyDepth * 0.5f),

            // Moving (walk or run, any direction): head 1.38..1.43, leading the travel.
            new Shape(0f, 1.40f, 0f, 0.26f, 0.24f, 0.26f,
                HumanoidMovingHeadLead, HumanoidTorsoBottomHeight, -0.05f, 0.35f, 0.30f),

            // Sprinting: head 1.32, 0.21 m forward and 0.10 m right, over the rifle.
            new Shape(0.10f, 1.32f, 0.21f, 0.30f, 0.28f, 0.31f,
                0f, HumanoidTorsoBottomHeight, -0.12f, 0.38f, 0.35f),

            // Crouched still: head 1.11, forward and right over the rifle.
            new Shape(HumanoidCrouchedHeadRight, HumanoidCrouchedHeadCenterHeight, HumanoidCrouchedHeadForward,
                HumanoidCrouchedHeadWidth * 0.5f, HumanoidCrouchedHeadHeight * 0.5f, HumanoidCrouchedHeadWidth * 0.5f,
                0f, HumanoidTorsoBottomHeight, 0f, HumanoidCrouchedBodyWidth * 0.5f, HumanoidCrouchedBodyWidth * 0.5f),

            // Crouch-walking: head 1.28 -- the stride is far more upright than the crouch.
            new Shape(0.07f, 1.28f, 0.13f, 0.29f, 0.27f, 0.31f,
                0f, HumanoidTorsoBottomHeight, 0f, HumanoidCrouchedBodyWidth * 0.5f, HumanoidCrouchedBodyWidth * 0.5f),

            // Seated in the chair pose: head 0.74..1.16 above the seat; the body box runs from the
            // chin down past the seat to -0.62, where the dangling legs end.
            new Shape(0.01f, HumanoidSeatedHeadCenterHeight, 0.155f, 0.17f, 0.21f, 0.165f,
                0f, -0.62f, 0.135f, 0.44f, 0.415f),
        };

        /// <summary>
        /// The pose a client draws for these snapshot facts, in the order its animator resolves
        /// them: a seat first, then the crouch, then the sprint, then whether the body moves.
        /// </summary>
        /// <param name="horizontalSpeed">The snapshot velocity's horizontal length, m/s.</param>
        /// <param name="sprinting">The snapshot's <c>IsSprinting</c> bit.</param>
        /// <remarks>
        /// Moving is <see cref="RemoteLocomotionSolver.MovingSpeed"/>, the threshold the client's own
        /// locomotion solver drives the animator's <c>moving</c> gate with -- one number, so the
        /// server cannot call a body still that every client draws walking.
        /// </remarks>
        public static HumanoidPose PoseFor(bool seated, bool crouching, float horizontalSpeed, bool sprinting)
        {
            if (seated) return HumanoidPose.Seated;

            bool moving = horizontalSpeed > RemoteLocomotionSolver.MovingSpeed;
            if (crouching) return moving ? HumanoidPose.CrouchMoving : HumanoidPose.Crouched;
            if (sprinting) return HumanoidPose.Sprinting;

            return moving ? HumanoidPose.Moving : HumanoidPose.Standing;
        }

        /// <summary>
        /// A standing humanoid at <paramref name="feetPosition"/>, facing +Z.
        /// </summary>
        /// <remarks>
        /// For the callers that only need "a person" -- tests and diagnostic probes. The server's
        /// own capture goes through the overload that takes the pose, because the boxes are
        /// measured from a body that has one.
        /// </remarks>
        public static HitboxSet Humanoid(in Vec3 feetPosition, float scale = 1f)
            => Humanoid(in feetPosition, 0f, HumanoidPose.Standing, 0f, 0f, scale);

        /// <summary>
        /// The character standing or crouched and still at <paramref name="feetPosition"/>, facing
        /// <paramref name="yawDegrees"/>.
        /// </summary>
        public static HitboxSet Humanoid(
            in Vec3 feetPosition, float yawDegrees, bool crouching, float scale = 1f)
            => Humanoid(in feetPosition, yawDegrees,
                crouching ? HumanoidPose.Crouched : HumanoidPose.Standing, 0f, 0f, scale);

        /// <summary>
        /// The character's hitbox set in <paramref name="pose"/> at <paramref name="feetPosition"/>
        /// (the seat, seated), facing <paramref name="yawDegrees"/> (Unity's convention: 0 faces
        /// +Z, 90 faces +X).
        /// </summary>
        /// <param name="velocityX">World velocity along X; only a moving pose reads it.</param>
        /// <param name="velocityZ">World velocity along Z; only a moving pose reads it.</param>
        /// <remarks>
        /// <para>
        /// <b>Turned with the body.</b> Each box is authored in the actor's own frame and stored as
        /// the axis-aligned box around the turned one — what <c>Collider.bounds</c> reports for
        /// the same box in the scene.
        /// </para>
        /// <para>
        /// <b>Four boxes, one damage class for the body.</b> The original's body box runs from
        /// the boots to the chin at x1, so the legs box sits inside it and never wins a ray: a ray
        /// reaches the enclosing box first, and a tie goes to the earlier index. The arms box is
        /// wider than the body and no deeper, so only the few centimetres beyond the shoulders
        /// resolve as a limb. The body always ends where the head begins (ledger X-24).
        /// </para>
        /// </remarks>
        public static HitboxSet Humanoid(
            in Vec3 feetPosition, float yawDegrees, HumanoidPose pose,
            float velocityX, float velocityZ, float scale = 1f)
        {
            float radians = yawDegrees * (float)(Math.PI / 180.0);
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);

            int index = (int)pose;
            Shape s = Shapes[index < Shapes.Length ? index : 0];

            float headX = s.HeadX;
            float headZ = s.HeadZ;

            if (s.Lead > 0f)
            {
                // The head leads the travel. The world velocity into the actor's frame, the
                // inverse of the turn Box applies: local = (x cos - z sin, x sin + z cos).
                float localX = velocityX * cos - velocityZ * sin;
                float localZ = velocityX * sin + velocityZ * cos;
                float length = MathF.Sqrt(localX * localX + localZ * localZ);
                if (length > 1e-4f)
                {
                    headX += s.Lead * localX / length;
                    headZ += s.Lead * localZ / length;
                }
            }

            float headBottom = s.HeadBottom;
            float torsoHeight = headBottom - s.TorsoBottom;
            float torsoWidth = s.TorsoHalfX * 2f;
            float torsoDepth = s.TorsoHalfZ * 2f;

            // Legs inside the body: never taller than it, so no sliver pokes above its top.
            float legsHeight = MathF.Min(0.94f, torsoHeight);

            return new HitboxSet(
                head: Box(in feetPosition, cos, sin, scale,
                    headX, s.HeadY, headZ,
                    s.HeadHalfX * 2f, s.HeadHalfY * 2f, s.HeadHalfZ * 2f),
                torso: Box(in feetPosition, cos, sin, scale,
                    0f, s.TorsoBottom + torsoHeight * 0.5f, s.TorsoZ,
                    torsoWidth, torsoHeight, torsoDepth),
                arms: Box(in feetPosition, cos, sin, scale,
                    0f, headBottom - 0.18f, s.TorsoZ,
                    torsoWidth + 0.14f, 0.36f, torsoDepth - 0.05f),
                legs: Box(in feetPosition, cos, sin, scale,
                    0f, s.TorsoBottom + legsHeight * 0.5f, s.TorsoZ,
                    torsoWidth * 0.63f, legsHeight, torsoDepth * 0.65f));
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
