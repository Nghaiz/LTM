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

        // ------------------------------------------------------------------ helpers

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
