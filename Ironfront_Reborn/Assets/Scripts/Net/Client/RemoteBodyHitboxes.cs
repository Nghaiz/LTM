using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The two boxes a soldier is hit on, riding the bones of a body this client draws for the
    /// server: the same boxes the game's own soldier carries as colliders, which a remote body
    /// cannot (a client must not deal damage).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the numbers come from.</b> The soldier prefabs (<c>Ai Character Optimizations</c>,
    /// <c>Player Fps Actor</c>) carry exactly two colliders on the hitbox layer (8): a box on
    /// <c>Bone_004</c>, the head, and one on <c>Bone_002</c> that runs the length of the body. The
    /// limbs' capsules are on the ragdoll layer and are not hit offline either. The remote proxy
    /// shares the rig, so the same boxes on the same bones are where the shooter sees the body.
    /// </para>
    /// <para>
    /// <b>Tested in each bone's own space</b>, so the box turns, bends and scales with the pose
    /// the animator last drew -- crouched, running, seated, leaning out of a jeep -- with no
    /// collider and no physics query.
    /// </para>
    /// </remarks>
    internal sealed class RemoteBodyHitboxes
    {
        /// <summary>The head bone, and its hitbox in that bone's space.</summary>
        internal const string HeadBone = "Bone_004";
        internal static readonly Vector3 HeadCentre = new Vector3(-0.13f, 0f, 0f);
        internal static readonly Vector3 HeadSize = new Vector3(0.4f, 0.3f, 0.3f);

        /// <summary>The body bone, and its hitbox in that bone's space.</summary>
        internal const string BodyBone = "Bone_002";
        internal static readonly Vector3 BodyCentre = new Vector3(0.3f, 0f, 0f);
        internal static readonly Vector3 BodySize = new Vector3(1.4f, 0.7f, 0.5f);

        /// <summary>
        /// The radius around a body's root no part of it reaches, metres: a segment that passes
        /// further away is not tested box by box.
        /// </summary>
        internal const float ReachMetres = 2.5f;

        private readonly Transform _root;
        private readonly Transform _head;
        private readonly Transform _body;

        private RemoteBodyHitboxes(Transform root, Transform head, Transform body)
        {
            _root = root;
            _head = head;
            _body = body;
        }

        /// <summary>The boxes of the rig under <paramref name="root"/>, or null when it lacks either bone.</summary>
        internal static RemoteBodyHitboxes TryCreate(Transform root)
        {
            if (root == null) return null;
            Transform head = FindDeep(root, HeadBone);
            Transform body = FindDeep(root, BodyBone);
            return head != null && body != null ? new RemoteBodyHitboxes(root, head, body) : null;
        }

        /// <summary>
        /// Where the segment first enters either box: <paramref name="fraction"/> along it, and
        /// whether the head was met first.
        /// </summary>
        internal bool TryHit(Vector3 from, Vector3 to, out float fraction, out bool head)
        {
            fraction = 1f;
            head = false;
            if (!SegmentNear(from, to, _root.position, ReachMetres)) return false;

            bool hitHead = SegmentEntersBox(_head, HeadCentre, HeadSize, from, to, out float headAt);
            bool hitBody = SegmentEntersBox(_body, BodyCentre, BodySize, from, to, out float bodyAt);
            if (!hitHead && !hitBody) return false;

            head = hitHead && (!hitBody || headAt <= bodyAt);
            fraction = head ? headAt : bodyAt;
            return true;
        }

        /// <summary>
        /// Slab test in <paramref name="bone"/>'s space: the fraction along the segment where it
        /// enters the box, 0 when it starts inside.
        /// </summary>
        internal static bool SegmentEntersBox(
            Transform bone, Vector3 centre, Vector3 size, Vector3 from, Vector3 to, out float fraction)
        {
            Vector3 a = bone.InverseTransformPoint(from) - centre;
            Vector3 b = bone.InverseTransformPoint(to) - centre;
            return SegmentEntersLocalBox(a, b, size * 0.5f, out fraction);
        }

        /// <summary>The slab test itself, for a box centred on the origin with <paramref name="half"/> extents.</summary>
        internal static bool SegmentEntersLocalBox(Vector3 a, Vector3 b, Vector3 half, out float fraction)
        {
            Vector3 d = b - a;
            float enter = 0f;
            float exit = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float start = a[axis];
                float step = d[axis];
                float extent = half[axis];
                if (Mathf.Abs(step) < 1e-7f)
                {
                    if (start < -extent || start > extent)
                    {
                        fraction = 1f;
                        return false;
                    }
                    continue;
                }

                float t0 = (-extent - start) / step;
                float t1 = (extent - start) / step;
                if (t0 > t1)
                {
                    float swap = t0;
                    t0 = t1;
                    t1 = swap;
                }

                if (t0 > enter) enter = t0;
                if (t1 < exit) exit = t1;
                if (enter > exit)
                {
                    fraction = 1f;
                    return false;
                }
            }

            fraction = enter;
            return true;
        }

        private static bool SegmentNear(Vector3 from, Vector3 to, Vector3 point, float radius)
        {
            Vector3 d = to - from;
            float lengthSquared = d.sqrMagnitude;
            float t = lengthSquared > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - from, d) / lengthSquared) : 0f;
            return (from + d * t - point).sqrMagnitude <= radius * radius;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name) return child;
                Transform found = FindDeep(child, name);
                if (found != null) return found;
            }

            return null;
        }
    }
}
