using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A bot's body poses on a headless server, so the engine colliders a projectile or a blast
    /// meets are where the bot is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This pins a claim that turned out to be false.</b> The 2026-09-28 hit-registration
    /// audit (#353) recorded that bots' server colliders are "animation-culled on the headless
    /// server, so frozen in pose", and left it as a known gap needing ~40 animators run
    /// server-side. The premise came from the prefab: <c>Ai Character Optimizations.prefab</c>
    /// authors its Animator with <c>m_CullingMode: 1</c> (CullUpdateTransforms), and with no
    /// camera no renderer is ever visible. But <c>ActiveRaggy.Awake</c> -- the original game's
    /// own code, unchanged -- sets that same Animator to <c>AlwaysAnimate</c> the moment a body
    /// is instantiated. An EditMode test never sees it, because EditMode does not run Awake;
    /// that is why <c>HitboxGeometryTests</c> has to set AlwaysAnimate itself.
    /// </para>
    /// <para>
    /// <b>Run in batchmode -nographics this is the server's condition</b>: no camera, no visible
    /// renderer. The control below proves the fixture can see culling -- without the game's own
    /// set-up the same steps leave the head where it was -- so the positive result is not the
    /// fixture animating regardless.
    /// </para>
    /// </remarks>
    public sealed class HeadlessBotAnimationTests
    {
        private const string BotPath = "Assets/Prefab/Ai Character Optimizations.prefab";
        private const int HitboxLayer = 8;

        /// <summary>The crouched head's centre over the feet, as HitboxGeometryTests measures it.</summary>
        private const float CrouchedHeadCentre = 1.11f;

        [Test]
        public void TheGameTurnsAnimationCullingOffOnTheAnimatorItsActorDrives()
        {
            GameObject bot = Spawn();
            try
            {
                Animator animator = bot.GetComponentInChildren<Animator>(true);

                // The premise the audit read, and why it looked right: the prefab culls.
                Assert.AreEqual(AnimatorCullingMode.CullUpdateTransforms, animator.cullingMode,
                    "the bot prefab no longer authors culling; this fixture's premise changed");

                RunAwake(bot, "ActiveRaggy");

                Assert.AreEqual(AnimatorCullingMode.AlwaysAnimate, animator.cullingMode,
                    "ActiveRaggy.Awake no longer turns culling off, so a headless server stops "
                    + "posing bots: their colliders freeze in whatever pose they spawned in");

                // And it is the animator the gameplay actor sets its parameters on, not a
                // second one somewhere in the rig.
                Component actor = Find(bot, "Actor");
                FieldInfo field = actor.GetType().GetField(
                    "animator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "Actor has no 'animator' field any more");
                Assert.AreSame(animator, field.GetValue(actor),
                    "Actor drives a different Animator from the one ActiveRaggy un-culls");
            }
            finally
            {
                Object.DestroyImmediate(bot);
            }
        }

        [Test]
        public void ABotsHeadColliderFollowsItsPoseWithNoCamera()
        {
            float live = HeadCentreAfterCrouching(runTheGamesSetUp: true);
            float control = HeadCentreAfterCrouching(runTheGamesSetUp: false);

            Assert.AreEqual(CrouchedHeadCentre, live, 0.05f,
                $"a crouched bot's head collider sits at {live:F2} m on a headless server; the "
                + $"body every client draws has it at {CrouchedHeadCentre:F2} m");

            // The control: the prefab as authored, stepped identically, does NOT crouch here --
            // which is what makes the line above evidence rather than a fixture that animates
            // whatever it is given.
            Assert.Greater(control - live, 0.3f,
                $"with culling left on, the head still moved to {control:F2} m, so this fixture "
                + "cannot see culling and the assertion above proves nothing");
        }

        private static float HeadCentreAfterCrouching(bool runTheGamesSetUp)
        {
            GameObject bot = Spawn();
            try
            {
                if (runTheGamesSetUp) RunAwake(bot, "ActiveRaggy");

                Animator animator = bot.GetComponentInChildren<Animator>(true);
                animator.enabled = true;
                animator.Rebind();
                animator.SetBool("crouched", true);
                for (int i = 0; i < 30; i++) animator.Update(0.1f);
                Physics.SyncTransforms();

                return HeadCollider(bot).bounds.center.y;
            }
            finally
            {
                Object.DestroyImmediate(bot);
            }
        }

        private static GameObject Spawn()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BotPath);
            Assert.IsNotNull(prefab, BotPath + " is missing");
            var bot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            bot.transform.position = Vector3.zero;
            bot.transform.rotation = Quaternion.identity;
            return bot;
        }

        /// <summary>
        /// Runs a component's own Awake, which EditMode skips. By name, because this assembly
        /// cannot reference Assembly-CSharp.
        /// </summary>
        private static void RunAwake(GameObject root, string typeName)
        {
            Component target = Find(root, typeName);
            MethodInfo awake = target.GetType().GetMethod(
                "Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(awake, typeName + " has no Awake any more");
            awake.Invoke(target, null);
        }

        private static Component Find(GameObject root, string typeName)
        {
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().Name == typeName) return behaviour;
            }

            Assert.Fail($"no {typeName} on {root.name}");
            return null;
        }

        private static Collider HeadCollider(GameObject root)
        {
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.gameObject.layer == HitboxLayer && collider.name == "Bone_004") return collider;
            }

            Assert.Fail("no Bone_004 on the Hitbox layer");
            return null;
        }
    }
}
