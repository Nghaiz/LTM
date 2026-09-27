using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A ragdoll built at runtime on a remote proxy's own humanoid skeleton, so a death falls
    /// over instead of vanishing. Client-track item E1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why at runtime.</b> <c>Remote Actor Proxy.prefab</c> is a skinned mesh, a humanoid
    /// animator and a bone hierarchy -- no rigidbodies, no joints, no <c>Actor</c> -- so until
    /// now every remote death was answered by hiding the body until its respawn snapshot, and a
    /// bot shot in front of the player simply disappeared (owner report 2026-09-27). The avatar
    /// names every bone a ragdoll needs, so the parts are built from it on the first death and
    /// torn down on the respawn that reuses the body.
    /// </para>
    /// <para>
    /// <b>The same shape as the stock rig.</b> Eleven bodies on the bones Unity's ragdoll wizard
    /// uses, with the masses <c>Ai Character Optimizations 1.prefab</c> authors (16 for the
    /// pelvis and chest, 4 for limbs and head, 1 for forearms), on the <c>Ragdoll</c> layer so a
    /// corpse lies on the ground without blocking the living.
    /// </para>
    /// </remarks>
    // Built with AddComponent at runtime on purpose: the proxy prefab is one mesh-and-bones asset
    // shared by every remote body, and its avatar is what names the bones, so there is nothing
    // for an authored rig to add and one more asset to keep in step if it were authored.
    internal sealed class RemoteRagdoll
    {
        /// <summary>TagManager's "Ragdoll" layer.</summary>
        private const int RagdollLayer = 10;

        private const HumanBodyBones None = HumanBodyBones.LastBone;

        private readonly struct PartSpec
        {
            public readonly HumanBodyBones Bone;
            public readonly HumanBodyBones Parent;
            public readonly HumanBodyBones Toward;
            public readonly float Mass;
            public readonly float Radius;

            public PartSpec(HumanBodyBones bone, HumanBodyBones parent, HumanBodyBones toward, float mass, float radius)
            {
                Bone = bone;
                Parent = parent;
                Toward = toward;
                Mass = mass;
                Radius = radius;
            }
        }

        private static readonly PartSpec[] Specs =
        {
            new PartSpec(HumanBodyBones.Hips,          None,                       HumanBodyBones.Spine,         16f, 0.14f),
            new PartSpec(HumanBodyBones.Chest,         HumanBodyBones.Hips,        HumanBodyBones.Neck,          16f, 0.15f),
            new PartSpec(HumanBodyBones.Head,          HumanBodyBones.Chest,       None,                          4f, 0.12f),
            new PartSpec(HumanBodyBones.LeftUpperArm,  HumanBodyBones.Chest,       HumanBodyBones.LeftLowerArm,   4f, 0.06f),
            new PartSpec(HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand,      1f, 0.05f),
            new PartSpec(HumanBodyBones.RightUpperArm, HumanBodyBones.Chest,       HumanBodyBones.RightLowerArm,  4f, 0.06f),
            new PartSpec(HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand,    1f, 0.05f),
            new PartSpec(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.Hips,        HumanBodyBones.LeftLowerLeg,   4f, 0.08f),
            new PartSpec(HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftFoot,      4f, 0.06f),
            new PartSpec(HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips,        HumanBodyBones.RightLowerLeg,  4f, 0.08f),
            new PartSpec(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightFoot,    4f, 0.06f),
        };

        private readonly Animator _animator;
        private readonly Transform[] _bones = new Transform[Specs.Length];
        private readonly int[] _layers = new int[Specs.Length];
        private readonly Rigidbody[] _bodies = new Rigidbody[Specs.Length];

        private RemoteRagdoll(Animator animator)
        {
            _animator = animator;
        }

        /// <summary>Whether the body is lying limp right now.</summary>
        public bool IsActive { get; private set; }

        /// <summary>
        /// A ragdoll for <paramref name="animator"/>'s skeleton, or null when it is not a humanoid
        /// that names the chest, a head and all four limbs.
        /// </summary>
        public static RemoteRagdoll TryCreate(Animator animator)
        {
            if (animator == null || !animator.isHuman) return null;

            var ragdoll = new RemoteRagdoll(animator);
            for (int i = 0; i < Specs.Length; i++)
            {
                Transform bone = animator.GetBoneTransform(Specs[i].Bone);

                // A rig without a separate chest bone has its torso on the spine.
                if (bone == null && Specs[i].Bone == HumanBodyBones.Chest)
                    bone = animator.GetBoneTransform(HumanBodyBones.Spine);

                if (bone == null) return null;
                ragdoll._bones[i] = bone;
            }

            return ragdoll;
        }

        /// <summary>
        /// Stops the animator, builds the bodies from the pose it left, and throws
        /// <paramref name="impulse"/> into <paramref name="hit"/>.
        /// </summary>
        public void Fell(Vector3 impulse, HumanBodyBones hit)
        {
            if (IsActive) return;
            IsActive = true;

            _animator.enabled = false;
            Build();

            Rigidbody target = _bodies[0];
            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Bone == hit && _bodies[i] != null) target = _bodies[i];
            }

            if (target != null && impulse.sqrMagnitude > 0f) target.AddForce(impulse, ForceMode.Impulse);
        }

        /// <summary>Tears the bodies down and gives the skeleton back to the animator.</summary>
        public void Restore()
        {
            if (!IsActive) return;
            IsActive = false;

            // Joints before bodies: a joint whose connected body is destroyed first logs a PhysX
            // error for the frame in between.
            for (int i = Specs.Length - 1; i >= 0; i--)
            {
                Transform bone = _bones[i];
                if (bone == null) continue;

                CharacterJoint joint = bone.GetComponent<CharacterJoint>();
                if (joint != null) Object.DestroyImmediate(joint);
            }

            for (int i = 0; i < Specs.Length; i++)
            {
                Transform bone = _bones[i];
                if (bone == null) continue;

                Collider collider = bone.GetComponent<Collider>();
                if (collider != null) Object.DestroyImmediate(collider);
                if (_bodies[i] != null) Object.DestroyImmediate(_bodies[i]);
                _bodies[i] = null;

                bone.gameObject.layer = _layers[i];
            }

            _animator.enabled = true;
            _animator.Rebind();
        }

        private void Build()
        {
            for (int i = 0; i < Specs.Length; i++)
            {
                Transform bone = _bones[i];
                PartSpec spec = Specs[i];

                _layers[i] = bone.gameObject.layer;
                bone.gameObject.layer = RagdollLayer;

                Rigidbody body = bone.gameObject.AddComponent<Rigidbody>();
                body.mass = spec.Mass;
                body.angularDamping = 0.5f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                _bodies[i] = body;

                AddCollider(bone, spec);
            }

            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Parent == None) continue;

                Rigidbody parent = BodyFor(Specs[i].Parent);
                if (parent == null) continue;

                CharacterJoint joint = _bones[i].gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent;
                joint.enableProjection = true;
                joint.lowTwistLimit = new SoftJointLimit { limit = -30f };
                joint.highTwistLimit = new SoftJointLimit { limit = 30f };
                joint.swing1Limit = new SoftJointLimit { limit = 40f };
                joint.swing2Limit = new SoftJointLimit { limit = 40f };
            }
        }

        private void AddCollider(Transform bone, in PartSpec spec)
        {
            Transform toward = spec.Toward != None ? _animator.GetBoneTransform(spec.Toward) : null;

            if (toward == null)
            {
                SphereCollider sphere = bone.gameObject.AddComponent<SphereCollider>();
                sphere.radius = ScaleFree(bone, spec.Radius);
                return;
            }

            Vector3 local = bone.InverseTransformPoint(toward.position);
            float length = local.magnitude;
            CapsuleCollider capsule = bone.gameObject.AddComponent<CapsuleCollider>();

            Vector3 absolute = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
            capsule.direction = absolute.x >= absolute.y && absolute.x >= absolute.z ? 0
                : absolute.y >= absolute.z ? 1 : 2;
            capsule.center = local * 0.5f;
            capsule.radius = ScaleFree(bone, spec.Radius);
            capsule.height = length + capsule.radius;
        }

        /// <summary>A size in metres, expressed in <paramref name="bone"/>'s own scale.</summary>
        private static float ScaleFree(Transform bone, float metres)
        {
            float scale = bone.lossyScale.x;
            return scale > 1e-4f ? metres / scale : metres;
        }

        private Rigidbody BodyFor(HumanBodyBones bone)
        {
            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Bone == bone) return _bodies[i];
            }

            return null;
        }
    }
}
