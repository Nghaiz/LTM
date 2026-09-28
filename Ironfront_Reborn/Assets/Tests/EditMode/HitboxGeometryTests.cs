using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The server's hitboxes and eye heights are where the character every client draws has its
    /// head, body and camera.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Playtest 2026-09-28, bug 5: "a clean headshot does not kill".</b> The server's head box
    /// sat 27 cm above the drawn head, so a shot through the face scored as a body hit, and the
    /// eye height was 43 cm off when crouched. Both sets of numbers had been written down rather
    /// than measured; this fixture measures them and fails the moment either side moves.
    /// </para>
    /// <para>
    /// The character is run through its own Animator (standing and crouched idles), and the two
    /// "Hitbox"-layer colliders the original game's bullets hit are read back: Bone_004, the head
    /// (x4), and Bone_002, the body (x1).
    /// </para>
    /// </remarks>
    public sealed class HitboxGeometryTests
    {
        private const string CharacterPath = "Assets/Prefab/Ai Character Optimizations.prefab";
        private const string PlayerPath = "Assets/Prefab/Player Fps Actor.prefab";
        private const int HitboxLayer = 8;

        /// <summary>How far a server box may sit from the drawn one before a shot mis-scores.</summary>
        private const float Tolerance = 0.05f;

        [Test]
        public void TheStandingHeadBoxIsTheDrawnHead()
        {
            (Bounds head, _) = Measure(crouched: false);
            Aabb server = HitboxSet.Humanoid(Vec3.Zero, 0f, crouching: false).Head;

            Assert.AreEqual(head.center.y, server.Center.Y, Tolerance, "head centre height");
            Assert.AreEqual(head.min.y, server.Min.Y, Tolerance, "head lower edge");
            Assert.AreEqual(head.max.y, server.Max.Y, Tolerance, "head upper edge");
            Assert.AreEqual(head.extents.x, server.Extents.X, Tolerance, "head half-width");
        }

        [Test]
        public void TheStandingBodyBoxIsTheDrawnBody()
        {
            (_, Bounds body) = Measure(crouched: false);
            Aabb server = HitboxSet.Humanoid(Vec3.Zero, 0f, crouching: false).Torso;

            Assert.AreEqual(body.min.y, server.Min.Y, Tolerance, "body lower edge");
            // The server's body stops where its head starts (ledger X-24); the drawn body box
            // overlaps the chin by a few centimetres, where the head wins anyway.
            Assert.AreEqual(body.max.y, server.Max.Y, 0.08f, "body upper edge");
            Assert.AreEqual(body.extents.x, server.Extents.X, 0.10f, "body half-width");
        }

        [Test]
        public void TheCrouchedHeadBoxIsTheDrawnHead()
        {
            (Bounds head, _) = Measure(crouched: true);
            Aabb server = HitboxSet.Humanoid(Vec3.Zero, 0f, crouching: true).Head;

            Assert.AreEqual(head.center.y, server.Center.Y, Tolerance, "crouched head centre height");
            Assert.AreEqual(head.center.z, server.Center.Z, Tolerance, "crouched head forward offset");
            Assert.AreEqual(head.center.x, server.Center.X, 0.10f, "crouched head side offset");
        }

        [Test]
        public void TheEyeHeightIsThePlayersCamera()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
            Assert.IsNotNull(prefab, PlayerPath + " is missing");
            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                player.transform.position = Vector3.zero;
                var body = player.GetComponent<CharacterController>();
                Assert.IsNotNull(body, "the player prefab has no CharacterController to stand on");
                float feet = player.transform.position.y + body.center.y - body.height * 0.5f;

                Camera fp = null;
                foreach (Camera camera in player.GetComponentsInChildren<Camera>(true))
                {
                    if (camera.name == "FP Camera") fp = camera;
                }
                Assert.IsNotNull(fp, "the player prefab has no \"FP Camera\"");

                Assert.AreEqual(fp.transform.position.y - feet, ProtocolConstants.EYE_HEIGHT, 0.02f,
                    "the server fires from a different height than the player's camera sees from");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        // ------------------------------------------------------------------ every drawn pose

        /// <summary>
        /// The head box of each pose holds the drawn head's centre through the stride.
        /// </summary>
        /// <remarks>
        /// Bug 5 audit, same playtest: a client animates a remote body from its snapshot bits --
        /// moving, sprinting, crouched, seated -- and each moves the head. The server does not
        /// know which frame of the stride a client draws, only which stride, so the box must hold
        /// the head through all of it, not through one frame.
        /// </remarks>
        [TestCase("run forward",   false, false, true,  0f,    3.5f,  false, HumanoidPose.Moving)]
        [TestCase("run back",      false, false, true,  0f,   -2.87f, false, HumanoidPose.Moving)]
        [TestCase("run right",     false, false, true,  3.3f,  0f,    false, HumanoidPose.Moving)]
        [TestCase("run left",      false, false, true, -3.3f,  0f,    false, HumanoidPose.Moving)]
        [TestCase("sprint",        false, false, true,  0f,    6.5f,  true,  HumanoidPose.Sprinting)]
        [TestCase("crouched",      false, true,  false, 0f,    0f,    false, HumanoidPose.Crouched)]
        [TestCase("crouch walk",   false, true,  true,  0f,    1.8f,  false, HumanoidPose.CrouchMoving)]
        [TestCase("crouch strafe", false, true,  true,  1.8f,  0f,    false, HumanoidPose.CrouchMoving)]
        [TestCase("seated",        true,  false, false, 0f,    0f,    false, HumanoidPose.Seated)]
        public void TheHeadBoxHoldsTheDrawnHeadThroughTheStride(
            string pose, bool seated, bool crouched, bool moving,
            float movementX, float movementY, bool sprinting, HumanoidPose serverPose)
        {
            // Facing +Z, so the animator's local velocity is the world one.
            Aabb server = HitboxSet.Humanoid(Vec3.Zero, 0f, serverPose, movementX, movementY).Head;

            GameObject character = Instantiate(CharacterPath);
            try
            {
                Animator animator = Pose(character, seated, crouched, moving, movementX, movementY, sprinting);
                Collider head = HitboxCollider(character, "Bone_004");

                // Two conditions, per frame. The centre inside the box, and the head's whole height
                // inside its height -- chin to crown, less a 4 cm allowance for the bounds of a
                // tilted head box. Height is where a pose moves the head decisively (0.01-0.02 m
                // of sway in every pose); sideways the stride's own sway exceeds any box the size
                // of a head, and the server cannot know which frame a client draws. The centre
                // alone let a running head drop its chin below a standing box and still pass.
                const int frames = 60;
                const float allowance = 0.04f;
                int inside = 0;
                string worst = "";
                for (int i = 0; i < frames; i++)
                {
                    animator.Update(1f / 30f);
                    Physics.SyncTransforms();
                    Bounds drawn = head.bounds;
                    Vector3 c = drawn.center;
                    var centre = new Vec3(c.x, c.y, c.z);
                    bool held = Contains(in server, in centre)
                        && drawn.min.y + allowance >= server.Min.Y
                        && drawn.max.y - allowance <= server.Max.Y;
                    if (held) inside++;
                    else worst = $"{drawn.min.ToString("F2")}..{drawn.max.ToString("F2")}";
                }

                Assert.GreaterOrEqual(inside, frames * 95 / 100,
                    $"{pose}: the drawn head left the server's {serverPose} head box in "
                    + $"{frames - inside} of {frames} frames (e.g. {worst}; box {Describe(in server)}). "
                    + "A headshot there scores as a body hit or a miss.");
            }
            finally
            {
                Object.DestroyImmediate(character);
            }
        }

        /// <summary>
        /// Astride the quad bike -- <c>seated type</c> 1 -- the head box holds the drawn head.
        /// </summary>
        /// <remarks>
        /// Leftover from the 2026-09-28 audit, which drew and boxed the quad's driver in the chair
        /// pose. Astride, the head is 0.28 m further forward, over the handlebars.
        /// </remarks>
        [Test]
        public void TheQuadRidersHeadBoxHoldsTheDrawnHead()
        {
            Aabb server = HitboxSet.Humanoid(Vec3.Zero, 0f, HumanoidPose.SeatedQuad, 0f, 0f).Head;
            Aabb chair = HitboxSet.Humanoid(Vec3.Zero, 0f, HumanoidPose.Seated, 0f, 0f).Head;

            GameObject character = Instantiate(CharacterPath);
            try
            {
                // Set before the first step, as Actor.EnterSeat sets it before the seat: the
                // state machine picks the seated pose on the way in.
                Animator animator = character.GetComponentInChildren<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                animator.SetBool("seated", true);
                animator.SetInteger("seated type", HitboxSet.QuadSeatAnimation);
                for (int i = 0; i < 40; i++) animator.Update(0.05f);
                Collider head = HitboxCollider(character, "Bone_004");

                const int frames = 60;
                const float allowance = 0.04f;
                int inside = 0, insideChair = 0;
                for (int i = 0; i < frames; i++)
                {
                    animator.Update(1f / 30f);
                    Physics.SyncTransforms();
                    Bounds drawn = head.bounds;
                    var centre = new Vec3(drawn.center.x, drawn.center.y, drawn.center.z);
                    if (Contains(in server, in centre)
                        && drawn.min.y + allowance >= server.Min.Y
                        && drawn.max.y - allowance <= server.Max.Y) inside++;
                    if (Contains(in chair, in centre)) insideChair++;
                }

                Assert.GreaterOrEqual(inside, frames * 95 / 100,
                    $"the quad rider's drawn head left the SeatedQuad head box in {frames - inside} "
                    + $"of {frames} frames (box {Describe(in server)})");

                // And the pose matters: the chair's box does not hold it.
                Assert.Less(insideChair, frames / 2,
                    "precondition: the chair's head box already holds the quad rider's head");
            }
            finally
            {
                Object.DestroyImmediate(character);
            }
        }

        // ------------------------------------------------------------------ a player's own body

        /// <summary>
        /// The server's own body for a player -- the engine colliders a projectile, a blast or a
        /// bot's aim meets -- stands where the player stands, in the player's pose.
        /// </summary>
        /// <remarks>
        /// Bug 5 audit: measured before this, the head collider of a standing player's body stood
        /// at 2.21..2.63 m, because the AI prefab's feet are its root and a player's root is its
        /// capsule's centre, 0.9 m up. <see cref="NetServerActor.PresentAsPlayer"/> lowers the drawn
        /// body onto the ground and poses it.
        /// </remarks>
        [TestCase(false, false, 1.52f)]
        [TestCase(false, true, 1.11f)]
        [TestCase(true, false, 0.95f)]
        public void APlayersBodyStandsWhereThePlayerStands(bool seated, bool crouching, float headCentre)
        {
            GameObject body = Instantiate(CharacterPath);
            try
            {
                var replicated = body.GetComponent<NetServerActor>();
                Assert.IsNotNull(replicated, "the AI prefab no longer carries NetServerActor");
                replicated.AttachMovementAgent();

                // A seated body's root is the seat; on foot it is the capsule centre above the ground.
                float root = seated ? 0f : MovementCore.HeightFor(crouching) * 0.5f;
                body.transform.position = new Vector3(0f, root, 0f);

                replicated.PresentAsPlayer(seated, crouching, Vec3.Zero);

                Animator animator = body.GetComponentInChildren<Animator>();
                for (int i = 0; i < 40; i++) animator.Update(0.05f);
                Physics.SyncTransforms();

                Bounds head = HitboxCollider(body, "Bone_004").bounds;
                Assert.AreEqual(headCentre, head.center.y, Tolerance,
                    $"a player's own body (seated {seated}, crouched {crouching}) holds its head at "
                    + $"{head.center.y:F2} m over the ground it stands on; every client draws it at "
                    + $"{headCentre:F2} m.");

                // Handed back to the bot brain as the prefab authored it.
                Transform model = animator.transform;
                replicated.Release();
                Assert.AreEqual(0f, model.localPosition.y, 1e-4f,
                    "a released body kept the player's lowered model under the bot brain");
            }
            finally
            {
                Object.DestroyImmediate(body);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static GameObject Instantiate(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(prefab, path + " is missing");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            return instance;
        }

        /// <summary>Drives the animator the way <c>RemoteActorView.Apply</c> does, and settles it.</summary>
        private static Animator Pose(
            GameObject character, bool seated, bool crouched, bool moving,
            float movementX, float movementY, bool sprinting)
        {
            Animator animator = character.GetComponentInChildren<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.SetBool("seated", seated);
            animator.SetBool("crouched", crouched);
            animator.SetBool("moving", moving);
            animator.SetBool("sprinting", sprinting);
            animator.SetFloat("movement x", movementX);
            animator.SetFloat("movement y", movementY);
            for (int i = 0; i < 40; i++) animator.Update(0.05f);
            return animator;
        }

        private static Collider HitboxCollider(GameObject character, string name)
        {
            foreach (Collider collider in character.GetComponentsInChildren<Collider>(true))
            {
                if (collider.gameObject.layer == HitboxLayer && collider.name == name) return collider;
            }

            Assert.Fail($"no {name} on the Hitbox layer");
            return null;
        }

        private static bool Contains(in Aabb box, in Vec3 point)
            => point.X >= box.Min.X && point.X <= box.Max.X
               && point.Y >= box.Min.Y && point.Y <= box.Max.Y
               && point.Z >= box.Min.Z && point.Z <= box.Max.Z;

        private static string Describe(in Aabb box)
            => $"({box.Min.X:F2}, {box.Min.Y:F2}, {box.Min.Z:F2})..({box.Max.X:F2}, {box.Max.Y:F2}, {box.Max.Z:F2})";

        /// <summary>The head and body hitboxes of the drawn character, feet at the origin.</summary>
        private static (Bounds Head, Bounds Body) Measure(bool crouched)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
            Assert.IsNotNull(prefab, CharacterPath + " is missing");
            var character = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                character.transform.position = Vector3.zero;
                character.transform.rotation = Quaternion.identity;

                // SampleAnimation does not pose a humanoid rig; stepping the Animator does.
                Animator animator = character.GetComponentInChildren<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                animator.SetBool("crouched", crouched);
                for (int i = 0; i < 30; i++) animator.Update(0.1f);
                Physics.SyncTransforms();

                Collider head = null;
                Collider body = null;
                foreach (Collider collider in character.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.gameObject.layer != HitboxLayer) continue;
                    if (collider.name == "Bone_004") head = collider;
                    else if (collider.name == "Bone_002") body = collider;
                }

                Assert.IsNotNull(head, "no head hitbox (Bone_004 on layer 8) on the character");
                Assert.IsNotNull(body, "no body hitbox (Bone_002 on layer 8) on the character");
                return (head.bounds, body.bounds);
            }
            finally
            {
                Object.DestroyImmediate(character);
            }
        }
    }
}
