using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the collider-ownership test behind ledger row <b>X-26</b>: the body that was hit is
    /// not cover for the shot that hit it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The evidence.</b> <c>artifacts/lane-b/x27-pinned-01..03</c>, weapon and witness both
    /// controlled: min distance 3.30 m / 1.52 m / 1.93 m, occluded 0 / 16 / 18, and <b>all 34
    /// occlusions across the three runs</b> were <c>collider=Bone_002 layer=8</c> at
    /// <c>frac=0.938..0.960</c>. Not one was terrain, not one was a building. A fraction that
    /// close to 1.0 puts the blocker at the endpoint — inside the victim — and the only run that
    /// scored a kill is the only one whose pair never got closer than 3.30 m.
    /// </para>
    /// <para>
    /// <b>Why the hierarchy and not the root GameObject.</b> The blockers are ragdoll bones
    /// several levels down an imported rig. A check that compared the collider's own GameObject
    /// against the actor's would have excluded nothing at all while looking exactly like a fix —
    /// which is the shape <c>green-that-proves-nothing.md</c> is about. That is the mutation
    /// these tests exist to kill.
    /// </para>
    /// <para>
    /// <b>Route chosen: an ignore-list per query, not a layer move.</b> Re-layering bone
    /// colliders so mask <c>-2049</c> excludes them is cheaper and riskier — layer assignment is
    /// authored, so a new rig or a re-imported model silently re-opens the defect, and it would
    /// have owed an asset gate (P-D5). Excluding the victim's own hierarchy is decided in code at
    /// the moment of the query and cannot be un-authored. It also keeps the thing X-26's row
    /// warned about: OTHER bodies still block, because widening the mask globally is a separate
    /// game decision with a separate blast radius.
    /// </para>
    /// </remarks>
    public sealed class SelfOcclusionTests
    {
        [Test]
        public void ARigBoneDeepInTheVictimIsPartOfIt()
        {
            GameObject victim = new GameObject("Victim");
            GameObject rig = new GameObject("character");
            GameObject bone = new GameObject("Bone_002");

            rig.transform.SetParent(victim.transform);
            bone.transform.SetParent(rig.transform);

            Collider boneCollider = bone.AddComponent<BoxCollider>();

            Assert.That(
                ServerTickLoop.IsPartOf(boneCollider, victim.transform), Is.True,
                "Bone_002 two levels down the victim's rig was not recognised as the victim's "
                + "own collider — this is exactly the collider that blocked 34 of 34 shots");

            Object.DestroyImmediate(victim);
        }

        [Test]
        public void TheVictimsOwnRootColliderIsPartOfIt()
        {
            // Transform.IsChildOf reports true for the transform itself, so the capsule on the
            // root is covered by the same check rather than by a special case.
            GameObject victim = new GameObject("Victim");
            Collider capsule = victim.AddComponent<CapsuleCollider>();

            Assert.That(ServerTickLoop.IsPartOf(capsule, victim.transform), Is.True);

            Object.DestroyImmediate(victim);
        }

        [Test]
        public void AWallIsNotPartOfTheVictim()
        {
            // The other direction, and the one that matters for the game: real cover still
            // blocks. A fix that excluded everything would read identically in a hit count.
            GameObject victim = new GameObject("Victim");
            GameObject wall = new GameObject("Building_04");
            Collider wallCollider = wall.AddComponent<BoxCollider>();

            Assert.That(ServerTickLoop.IsPartOf(wallCollider, victim.transform), Is.False);

            Object.DestroyImmediate(wall);
            Object.DestroyImmediate(victim);
        }

        [Test]
        public void AnotherPlayersBoneIsNotPartOfTheVictim()
        {
            // The clause X-26's row is explicit about: it does NOT say a body should stop
            // blocking bullets. A third player standing between shooter and victim is cover,
            // and widening mask -2049 globally would have removed that too.
            GameObject victim = new GameObject("Victim");
            GameObject bystander = new GameObject("Bystander");
            GameObject bone = new GameObject("Bone_002");

            bone.transform.SetParent(bystander.transform);
            Collider boneCollider = bone.AddComponent<BoxCollider>();

            Assert.That(ServerTickLoop.IsPartOf(boneCollider, victim.transform), Is.False);

            Object.DestroyImmediate(bystander);
            Object.DestroyImmediate(victim);
        }

        [Test]
        public void AVictimThatHasLeftTheWorldExcludesNothing()
        {
            // A dead lookup must not make a body invulnerable, and it must not make one
            // immortal either: with no root to compare against, the query behaves exactly as it
            // did before X-26 rather than silently ignoring every collider.
            GameObject wall = new GameObject("Building_04");
            Collider wallCollider = wall.AddComponent<BoxCollider>();

            Assert.That(ServerTickLoop.IsPartOf(wallCollider, null), Is.False);
            Assert.That(ServerTickLoop.IsPartOf(null, wall.transform), Is.False);

            Object.DestroyImmediate(wall);
        }
    }
}
