using System.Collections.Generic;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A dead body that outlives the actor it came from: a visual copy of a remote body taken at
    /// the moment of death, thrown as a ragdoll, left where it falls, and sunk out of sight later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a copy.</b> A bot respawns <c>RESPAWN_SECONDS</c> (3 s) after it dies, and the
    /// respawn reuses the same proxy: <see cref="RemoteRagdoll.Restore"/> stands it back up and
    /// the next snapshot carries it to its spawn. So a corpse built on the proxy itself lasted
    /// until that snapshot and no longer -- a body shot in front of the player collapsed and was
    /// gone a moment later (owner report 2026-09-29: "bắn chết chỉ khiến cho actor đó biến mất").
    /// The original has the same coupling. Copying the body lets the corpse stay on the field
    /// for as long as <see cref="RemoteCorpseDirector"/> keeps it, whatever the actor does next.
    /// </para>
    /// <para>
    /// <b>Only what is drawn is copied</b>: transforms, mesh and skinned-mesh renderers, and a
    /// private instance of every material, so the corpse keeps the colour it died in when the
    /// pooled proxy is rebound to someone on the other side. No script is copied -- an
    /// instantiated weapon would run its <c>Awake</c> and register itself as a live weapon.
    /// </para>
    /// <para>
    /// <b>The weapon drops.</b> As in the original's <c>Actor.Die</c> (<c>DropWeapon</c>), what
    /// the body held leaves the hand and falls as its own object.
    /// </para>
    /// </remarks>
    // Built at runtime rather than from a prefab: a corpse is a copy of whichever body died, in the
    // pose it died in, which no authored asset can hold.
    internal sealed class RemoteCorpse
    {
        /// <summary>TagManager's "Ragdoll" layer: lies on the ground, blocks nobody alive.</summary>
        private const int RagdollLayer = 10;

        private const string WeaponMountName = "Weapon Mount";

        private const float WeaponMass = 3.5f;

        private readonly GameObject _root;
        private readonly RemoteRagdoll _ragdoll;
        private readonly Transform _chest;
        private readonly Transform _head;
        private readonly Rigidbody _weapon;
        private readonly List<Object> _owned;

        private RemoteCorpse(
            GameObject root, RemoteRagdoll ragdoll, Transform chest, Transform head, Rigidbody weapon,
            List<Object> owned, ushort actorId, int team, float diedAt)
        {
            _root = root;
            _ragdoll = ragdoll;
            _chest = chest;
            _head = head;
            _weapon = weapon;
            _owned = owned;
            ActorId = actorId;
            Team = team;
            DiedAt = diedAt;
        }

        /// <summary>The copy's root, for a test to move into its own scene.</summary>
        internal GameObject Root => _root;

        /// <summary>The dropped weapon, or null when the hand held none.</summary>
        internal Rigidbody DroppedWeapon => _weapon;

        /// <summary>Whose body this was.</summary>
        public ushort ActorId { get; }

        /// <summary>The side it died on, for the blood and nothing else.</summary>
        public int Team { get; }

        /// <summary><c>Time.time</c> of the death.</summary>
        public float DiedAt { get; }

        /// <summary>The blood pool under the body has been drawn.</summary>
        public bool Pooled { get; set; }

        /// <summary>The last time the wound dripped.</summary>
        public float LastDripAt { get; set; }

        /// <summary>Asked to leave early, to stay under the corpse cap.</summary>
        public bool Evicted { get; set; }

        /// <summary><c>Time.time</c> the sink began, or negative while the body still lies.</summary>
        public float SinkStartedAt { get; private set; } = -1f;

        /// <summary>Destroyed, or its root lost under it.</summary>
        public bool IsGone => _root == null;

        /// <summary>Where the wound is drawn from: the chest while it has one.</summary>
        public Vector3 ChestPosition => _chest != null ? _chest.position : (_root != null ? _root.transform.position : Vector3.zero);

        /// <summary>The head, for a headshot's spray.</summary>
        public Vector3 HeadPosition => _head != null ? _head.position : ChestPosition;

        /// <summary>Whether every part has stopped moving.</summary>
        public bool IsResting => _ragdoll.IsResting;

        /// <summary>
        /// Copies <paramref name="source"/>'s body as it stands now, or null when it is not a humanoid
        /// the ragdoll can be built on.
        /// </summary>
        public static RemoteCorpse TryCreate(Animator source, ushort actorId, int team, float now)
        {
            if (source == null || !source.isHuman) return null;

            Transform from = source.transform;
            var root = new GameObject("Corpse " + actorId);
            root.layer = RagdollLayer;
            root.transform.SetPositionAndRotation(from.position, from.rotation);
            root.transform.localScale = from.lossyScale;

            var map = new Dictionary<Transform, Transform>(96) { [from] = root.transform };
            var owned = new List<Object>(4);
            var skinned = new List<KeyValuePair<SkinnedMeshRenderer, SkinnedMeshRenderer>>(2);
            CopyChildren(from, root.transform, map, owned, skinned);

            for (int i = 0; i < skinned.Count; i++)
            {
                RemapBones(skinned[i].Key, skinned[i].Value, map);
            }

            Transform Resolve(HumanBodyBones bone)
            {
                Transform original = source.GetBoneTransform(bone);
                return original != null && map.TryGetValue(original, out Transform copy) ? copy : null;
            }

            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(Resolve);
            if (ragdoll == null)
            {
                DestroyAll(root, owned);
                return null;
            }

            Transform chest = Resolve(HumanBodyBones.Chest) ?? Resolve(HumanBodyBones.Spine);
            Rigidbody weapon = DetachWeapon(Resolve(HumanBodyBones.RightHand), root.transform);
            return new RemoteCorpse(root, ragdoll, chest, Resolve(HumanBodyBones.Head), weapon, owned, actorId, team, now);
        }

        /// <summary>
        /// Throws the body: <paramref name="impulse"/> into <paramref name="hit"/>, carrying
        /// <paramref name="carried"/>, crumpling for <paramref name="crumpleSeconds"/>.
        /// </summary>
        public void Fell(Vector3 impulse, HumanBodyBones hit, Vector3 carried, float crumpleSeconds)
        {
            _ragdoll.Fell(impulse, hit, carried, crumpleSeconds);
            if (_weapon == null) return;

            _weapon.linearVelocity = carried + impulse * 0.02f + Random.insideUnitSphere * 0.6f;
            _weapon.angularVelocity = Random.insideUnitSphere * 4f;
        }

        /// <summary>A second push on a body that already fell: a late <c>S_DEATH</c>.</summary>
        public void Push(Vector3 impulse, HumanBodyBones hit)
        {
            _ragdoll.Push(impulse, hit);
        }

        /// <summary>Fades the crumple <paramref name="sinceDeath"/> seconds after the death.</summary>
        public void TickCrumple(float sinceDeath)
        {
            _ragdoll.TickCrumple(sinceDeath);
        }

        /// <summary>A blast rolls the body and knocks the dropped weapon about.</summary>
        public void ThrowByBlast(float force, Vector3 centre, float radius)
        {
            if (SinkStartedAt >= 0f) return;
            _ragdoll.AddExplosionForce(force, centre, radius, 1f);
            if (_weapon != null && !_weapon.isKinematic)
            {
                _weapon.AddExplosionForce(force * _weapon.mass, centre, radius, 1f, ForceMode.Impulse);
            }
        }

        /// <summary>Stops the physics so the body can go down through the ground in one piece.</summary>
        public void BeginSink(float now)
        {
            if (SinkStartedAt >= 0f) return;
            SinkStartedAt = now;
            _ragdoll.Freeze();
            if (_weapon != null)
            {
                _weapon.isKinematic = true;
                _weapon.detectCollisions = false;
            }
        }

        /// <summary>Lowers the whole body by <paramref name="metres"/> since the last call.</summary>
        public void SinkBy(float metres)
        {
            if (_root != null) _root.transform.position += Vector3.down * metres;
        }

        /// <summary>Destroys the copy and every material it owns.</summary>
        public void Destroy()
        {
            DestroyAll(_root, _owned);
        }

        private static void CopyChildren(
            Transform from, Transform to, Dictionary<Transform, Transform> map, List<Object> owned,
            List<KeyValuePair<SkinnedMeshRenderer, SkinnedMeshRenderer>> skinned)
        {
            for (int i = 0; i < from.childCount; i++)
            {
                Transform child = from.GetChild(i);
                if (!child.gameObject.activeSelf) continue;

                var copy = new GameObject(child.name);
                copy.layer = RagdollLayer;
                Transform t = copy.transform;
                t.SetParent(to, false);
                t.localPosition = child.localPosition;
                t.localRotation = child.localRotation;
                t.localScale = child.localScale;
                map[child] = t;

                CopyRenderers(child.gameObject, copy, owned, skinned);
                CopyChildren(child, t, map, owned, skinned);
            }
        }

        private static void CopyRenderers(
            GameObject from, GameObject to, List<Object> owned,
            List<KeyValuePair<SkinnedMeshRenderer, SkinnedMeshRenderer>> skinned)
        {
            var skin = from.GetComponent<SkinnedMeshRenderer>();
            if (skin != null && skin.enabled && skin.sharedMesh != null)
            {
                var copy = to.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = skin.sharedMesh;
                copy.sharedMaterials = OwnCopies(skin.sharedMaterials, owned);
                copy.localBounds = skin.localBounds;
                // The root never moves while the bones fly, so bounds read off the root would cull a
                // body thrown a few metres; a corpse is cheap enough to re-bound every frame.
                copy.updateWhenOffscreen = true;
                copy.shadowCastingMode = skin.shadowCastingMode;
                skinned.Add(new KeyValuePair<SkinnedMeshRenderer, SkinnedMeshRenderer>(skin, copy));
                return;
            }

            var filter = from.GetComponent<MeshFilter>();
            var mesh = from.GetComponent<MeshRenderer>();
            if (filter == null || mesh == null || !mesh.enabled || filter.sharedMesh == null) return;

            to.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var drawn = to.AddComponent<MeshRenderer>();
            drawn.sharedMaterials = OwnCopies(mesh.sharedMaterials, owned);
            drawn.shadowCastingMode = mesh.shadowCastingMode;
        }

        private static Material[] OwnCopies(Material[] materials, List<Object> owned)
        {
            var copies = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                copies[i] = new Material(materials[i]);
                owned.Add(copies[i]);
            }
            return copies;
        }

        private static void RemapBones(
            SkinnedMeshRenderer from, SkinnedMeshRenderer to, Dictionary<Transform, Transform> map)
        {
            Transform[] bones = from.bones;
            var copies = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null) map.TryGetValue(bones[i], out copies[i]);
            }
            to.bones = copies;
            if (from.rootBone != null && map.TryGetValue(from.rootBone, out Transform rootBone))
            {
                to.rootBone = rootBone;
            }
        }

        // The weapon the body held becomes its own falling object. Null when the hand held nothing
        // drawn, or nothing with a mesh to give it a collider -- a weapon without one would fall
        // through the ground, so it stays in the hand instead.
        private static Rigidbody DetachWeapon(Transform hand, Transform corpseRoot)
        {
            if (hand == null) return null;
            Transform mount = FindChild(hand, WeaponMountName);
            if (mount == null) return null;

            MeshFilter[] meshes = mount.GetComponentsInChildren<MeshFilter>();
            if (meshes.Length == 0) return null;

            mount.SetParent(corpseRoot, true);
            Bounds local = default(Bounds);
            bool any = false;
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh mesh = meshes[i].sharedMesh;
                if (mesh == null) continue;
                Bounds b = mesh.bounds;
                Matrix4x4 toMount = mount.worldToLocalMatrix * meshes[i].transform.localToWorldMatrix;
                Vector3 min = toMount.MultiplyPoint3x4(b.min);
                Vector3 max = toMount.MultiplyPoint3x4(b.max);
                Bounds part = new Bounds((min + max) * 0.5f, Vector3.zero);
                part.Encapsulate(min);
                part.Encapsulate(max);
                if (any) local.Encapsulate(part); else local = part;
                any = true;
            }
            if (!any) return null;

            BoxCollider box = mount.gameObject.AddComponent<BoxCollider>();
            box.center = local.center;
            box.size = Vector3.Max(local.size, new Vector3(0.05f, 0.05f, 0.05f));
            Rigidbody body = mount.gameObject.AddComponent<Rigidbody>();
            body.mass = WeaponMass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            return body;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name) return child;
                Transform deeper = FindChild(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static void DestroyAll(GameObject root, List<Object> owned)
        {
            if (root != null)
            {
                if (Application.isPlaying) Object.Destroy(root); else Object.DestroyImmediate(root);
            }
            for (int i = 0; i < owned.Count; i++)
            {
                if (owned[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(owned[i]); else Object.DestroyImmediate(owned[i]);
            }
            owned.Clear();
        }
    }
}
