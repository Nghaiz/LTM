using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The shapes a soldier is hit on, riding the bones of a body this client draws for the
    /// server: the same shapes the game's own soldier carries as colliders, which a remote body
    /// cannot (a client must not deal damage).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the numbers come from.</b> The soldier prefab (<c>Ai Character Optimizations</c>)
    /// carries a collider with a <c>Hitbox</c> on every part a round can strike: a box on the head
    /// (<c>Bone_004</c>, damage x4), a box running the length of the body (<c>Bone_002</c>), its
    /// ragdoll's head sphere (<c>Bone_003</c>, x3), chest and hip boxes, a capsule on each upper and
    /// lower arm and leg, and a box on each foot. Offline a round's raycast meets whichever comes first. The remote proxy shares
    /// the rig, so the same shapes on the same bones are where the shooter sees the body, and
    /// <c>RemoteBodyHitboxesTests</c> pins <see cref="Shapes"/> to the prefab in both directions.
    /// </para>
    /// <para>
    /// <b>Tested in each bone's own space</b>, so a shape turns, bends and scales with the pose the
    /// animator last drew -- crouched, running, seated, leaning out of a jeep -- with no collider
    /// and no physics query.
    /// </para>
    /// </remarks>
    internal sealed class RemoteBodyHitboxes
    {
        /// <summary>One collider of the soldier, in its bone's space.</summary>
        internal readonly struct BodyHitShape
        {
            private BodyHitShape(
                string bone, bool capsule, bool sphere, Vector3 centre, Vector3 size, float radius, float height,
                HitboxType part)
            {
                Bone = bone;
                IsCapsule = capsule;
                IsSphere = sphere;
                Centre = centre;
                Size = size;
                Radius = radius;
                Height = height;
                Part = part;
            }

            public string Bone { get; }

            /// <summary>A capsule along the bone's x axis (a sphere is one with no spine); otherwise a box.</summary>
            public bool IsCapsule { get; }

            /// <summary>A sphere: a capsule whose height is its diameter.</summary>
            public bool IsSphere { get; }

            public Vector3 Centre { get; }

            /// <summary>A box's size.</summary>
            public Vector3 Size { get; }

            /// <summary>A capsule's radius.</summary>
            public float Radius { get; }

            /// <summary>A capsule's height, end to end, caps included.</summary>
            public float Height { get; }

            /// <summary>What a round that strikes it hit, for the server's damage.</summary>
            public HitboxType Part { get; }

            public static BodyHitShape Box(string bone, Vector3 centre, Vector3 size, HitboxType part)
                => new BodyHitShape(bone, false, false, centre, size, 0f, 0f, part);

            public static BodyHitShape Capsule(string bone, Vector3 centre, float radius, float height, HitboxType part)
                => new BodyHitShape(bone, true, false, centre, Vector3.zero, radius, height, part);

            public static BodyHitShape Sphere(string bone, Vector3 centre, float radius, HitboxType part)
                => new BodyHitShape(bone, true, true, centre, Vector3.zero, radius, 2f * radius, part);
        }

        /// <summary>The head bone.</summary>
        internal const string HeadBone = "Bone_004";

        /// <summary>The body bone.</summary>
        internal const string BodyBone = "Bone_002";

        /// <summary>
        /// Every hitbox of the soldier prefab. The limbs' damage is the server's Limb, the hips' and
        /// chest's its Body, the head's and the ragdoll's head sphere's (x3 offline) its Head.
        /// </summary>
        internal static readonly BodyHitShape[] Shapes =
        {
            BodyHitShape.Box(HeadBone, new Vector3(-0.13f, 0f, 0f), new Vector3(0.4f, 0.3f, 0.3f), HitboxType.Head),
            BodyHitShape.Box(BodyBone, new Vector3(0.3f, 0f, 0f), new Vector3(1.4f, 0.7f, 0.5f), HitboxType.Body),
            BodyHitShape.Box(BodyBone, new Vector3(-0.2f, 0f, 0f), new Vector3(0.45f, 0.4f, 0.3f), HitboxType.Body),
            BodyHitShape.Box("Bone", new Vector3(-0.35f, 0f, 0f), new Vector3(0.4f, 0.4f, 0.3f), HitboxType.Body),
            BodyHitShape.Sphere("Bone_003", new Vector3(-0.25f, 0f, 0f), 0.15f, HitboxType.Head),
            BodyHitShape.Capsule("Bone_003_L_001", new Vector3(-0.13915128f, 0f, 0f), 0.06f, 0.39830256f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_003_R_001", new Vector3(-0.13915132f, 0f, 0f), 0.06f, 0.39830264f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_003_L_002", new Vector3(-0.124141574f, 0f, 0f), 0.06f, 0.36828315f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_003_R_002", new Vector3(-0.124141596f, 0f, 0f), 0.06f, 0.36828318f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_005_L_001", new Vector3(-0.15957704f, 0f, 0f), 0.08f, 0.47915408f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_005_R_001", new Vector3(-0.15957707f, 0f, 0f), 0.08f, 0.47915414f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_005_L_002", new Vector3(-0.1294818f, 0f, 0f), 0.08f, 0.4189636f, HitboxType.Limb),
            BodyHitShape.Capsule("Bone_005_R_002", new Vector3(-0.1294818f, 0f, 0f), 0.08f, 0.4189636f, HitboxType.Limb),
            BodyHitShape.Box("Bone_005_L_003", new Vector3(-0.07000001f, 0f, 0f), new Vector3(0.3f, 0.15f, 0.1f), HitboxType.Limb),
            BodyHitShape.Box("Bone_005_R_003", new Vector3(-0.07000001f, 0f, 0f), new Vector3(0.3f, 0.15f, 0.1f), HitboxType.Limb),
        };

        /// <summary>
        /// The radius around a body's root no part of it reaches, metres: a segment that passes
        /// further away is not tested shape by shape.
        /// </summary>
        internal const float ReachMetres = 2.5f;

        private readonly Transform _root;
        private readonly Transform[] _bones;

        private RemoteBodyHitboxes(Transform root, Transform[] bones)
        {
            _root = root;
            _bones = bones;
        }

        /// <summary>The shapes of the rig under <paramref name="root"/>, or null when it lacks any bone.</summary>
        internal static RemoteBodyHitboxes TryCreate(Transform root)
        {
            if (root == null) return null;
            var bones = new Transform[Shapes.Length];
            for (int i = 0; i < Shapes.Length; i++)
            {
                bones[i] = FindDeep(root, Shapes[i].Bone);
                if (bones[i] == null) return null;
            }

            return new RemoteBodyHitboxes(root, bones);
        }

        /// <summary>
        /// Where the segment first enters any shape: <paramref name="fraction"/> along it, and the
        /// part that shape belongs to.
        /// </summary>
        internal bool TryHit(Vector3 from, Vector3 to, out float fraction, out HitboxType part)
        {
            fraction = 1f;
            part = HitboxType.Body;
            if (!SegmentNear(from, to, _root.position, ReachMetres)) return false;

            bool hit = false;
            for (int i = 0; i < Shapes.Length; i++)
            {
                if (!SegmentEnters(_bones[i], in Shapes[i], from, to, out float at) || at >= fraction && hit) continue;
                fraction = at;
                part = Shapes[i].Part;
                hit = true;
            }

            return hit;
        }

        /// <summary>The fraction along the segment where it enters <paramref name="shape"/>, 0 when it starts inside.</summary>
        internal static bool SegmentEnters(Transform bone, in BodyHitShape shape, Vector3 from, Vector3 to, out float fraction)
        {
            Vector3 a = bone.InverseTransformPoint(from) - shape.Centre;
            Vector3 b = bone.InverseTransformPoint(to) - shape.Centre;
            return shape.IsCapsule
                ? SegmentEntersLocalCapsule(a, b, shape.Height * 0.5f - shape.Radius, shape.Radius, out fraction)
                : SegmentEntersLocalBox(a, b, shape.Size * 0.5f, out fraction);
        }

        /// <summary>The slab test, for a box centred on the origin with <paramref name="half"/> extents.</summary>
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

        /// <summary>
        /// A capsule along the x axis, centred on the origin: every point within
        /// <paramref name="radius"/> of the line from -<paramref name="halfSpine"/> to
        /// +<paramref name="halfSpine"/>.
        /// </summary>
        internal static bool SegmentEntersLocalCapsule(Vector3 a, Vector3 b, float halfSpine, float radius, out float fraction)
        {
            fraction = 1f;
            halfSpine = Mathf.Max(0f, halfSpine);
            var p = new Vector3(-halfSpine, 0f, 0f);
            var q = new Vector3(halfSpine, 0f, 0f);

            if (DistanceToSpine(a, halfSpine) <= radius)
            {
                fraction = 0f;
                return true;
            }

            Vector3 d = b - a;
            float length = d.magnitude;
            if (length < 1e-7f) return false;
            Vector3 dir = d / length;

            // The cylinder between the two caps: the part of the direction across the axis.
            float best = float.MaxValue;
            float a2 = dir.y * dir.y + dir.z * dir.z;
            if (a2 > 1e-10f)
            {
                float b2 = a.y * dir.y + a.z * dir.z;
                float c2 = a.y * a.y + a.z * a.z - radius * radius;
                float h = b2 * b2 - a2 * c2;
                if (h >= 0f)
                {
                    float t = (-b2 - Mathf.Sqrt(h)) / a2;
                    float x = a.x + t * dir.x;
                    if (t >= 0f && x >= -halfSpine && x <= halfSpine) best = t;
                }
            }

            // The two caps.
            best = Mathf.Min(best, RayEntersSphere(a, dir, p, radius));
            best = Mathf.Min(best, RayEntersSphere(a, dir, q, radius));

            if (best > length) return false;
            fraction = best / length;
            return true;
        }

        private static float RayEntersSphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius)
        {
            Vector3 oc = origin - centre;
            float b = Vector3.Dot(oc, dir);
            float c = oc.sqrMagnitude - radius * radius;
            float h = b * b - c;
            if (h < 0f) return float.MaxValue;
            float t = -b - Mathf.Sqrt(h);
            return t >= 0f ? t : float.MaxValue;
        }

        private static float DistanceToSpine(Vector3 point, float halfSpine)
        {
            float x = Mathf.Clamp(point.x, -halfSpine, halfSpine);
            return (point - new Vector3(x, 0f, 0f)).magnitude;
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
