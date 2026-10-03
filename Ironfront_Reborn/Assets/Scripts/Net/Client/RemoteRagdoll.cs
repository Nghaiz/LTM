using System;
using UnityEngine;
using Object = UnityEngine.Object;

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

        /// <summary>How near the camera a ragdoll's parts are drawn interpolated.</summary>
        internal const float InterpolateWithinMetres = 30f;

        /// <summary>
        /// Interpolation for a part at <paramref name="at"/>: on near the camera, off past
        /// <see cref="InterpolateWithinMetres"/>.
        /// </summary>
        /// <remarks>
        /// An interpolated body writes its transform every frame, and with auto-sync on every
        /// raycast after that pushes the written transforms back into PhysX: in a 100-bot Forest
        /// Lake match each awake body cost about 0.04 ms a frame in scripts on top of its physics
        /// step (fit over 365 windows of <c>[loop]</c> and <c>[physics]</c>, 2026-10-02), 146 of
        /// them at the busiest. Interpolation only shows where a body crosses enough pixels for a
        /// 60 Hz step to read as a jump, which is close to the camera; most bodies in such a match
        /// fall far from it.
        /// </remarks>
        internal static RigidbodyInterpolation InterpolationAt(Vector3 at, Camera viewer)
        {
            if (viewer == null) return RigidbodyInterpolation.None;
            return (at - viewer.transform.position).sqrMagnitude <= InterpolateWithinMetres * InterpolateWithinMetres
                ? RigidbodyInterpolation.Interpolate
                : RigidbodyInterpolation.None;
        }

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

        // Null for a ragdoll built on a corpse copy, which has no animator to switch off.
        private readonly Animator _animator;

        private readonly Func<HumanBodyBones, Transform> _resolve;

        // Set while the body crumples: joints that hold the death pose with a spring that fades.
        private ConfigurableJoint[] _crumpleJoints;

        private float _crumpleSeconds;
        private readonly Transform[] _bones = new Transform[Specs.Length];
        private readonly int[] _layers = new int[Specs.Length];
        private readonly Rigidbody[] _bodies = new Rigidbody[Specs.Length];

        private RemoteRagdoll(Animator animator, Func<HumanBodyBones, Transform> resolve)
        {
            _animator = animator;
            _resolve = resolve;
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

            return TryCreate(animator, animator.GetBoneTransform);
        }

        /// <summary>
        /// A ragdoll for a skeleton whose bones <paramref name="resolve"/> names, with no animator
        /// behind it: a corpse's copy of a body (<see cref="RemoteCorpse"/>). Null when a bone the
        /// ragdoll is built on is missing.
        /// </summary>
        public static RemoteRagdoll TryCreate(Func<HumanBodyBones, Transform> resolve)
        {
            return resolve == null ? null : TryCreate(null, resolve);
        }

        private static RemoteRagdoll TryCreate(Animator animator, Func<HumanBodyBones, Transform> resolve)
        {
            var ragdoll = new RemoteRagdoll(animator, resolve);
            for (int i = 0; i < Specs.Length; i++)
            {
                Transform bone = resolve(Specs[i].Bone);

                // A rig without a separate chest bone has its torso on the spine.
                if (bone == null && Specs[i].Bone == HumanBodyBones.Chest)
                    bone = resolve(HumanBodyBones.Spine);

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
            Fell(impulse, hit, Vector3.zero, 0f);
        }

        /// <summary>
        /// As <see cref="Fell(Vector3, HumanBodyBones)"/>, carrying the body's own motion into the
        /// fall and, for <paramref name="crumpleSeconds"/>, holding the pose it died in with a
        /// spring that fades to nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Momentum.</b> The original hands its ragdoll the controller's velocity
        /// (<c>ActiveRaggy.Ragdoll(controller.Velocity())</c>), so a soldier shot mid-sprint pitches
        /// forward. A body that starts from rest drops straight down on the spot instead, which
        /// reads as a puppet whose strings were cut.
        /// </para>
        /// <para>
        /// <b>The crumple.</b> The original keeps a weak muscle drive on a death
        /// (<c>ragdoll.SetDrive(50f, 1f)</c>) so the body sags rather than collapsing in one frame.
        /// A passive joint cannot do that; a <c>ConfigurableJoint</c> whose slerp drive targets the
        /// pose at the moment of death, weakened over the crumple, can: the knees give first and
        /// the torso follows. <see cref="TickCrumple"/> fades it.
        /// </para>
        /// </remarks>
        public void Fell(Vector3 impulse, HumanBodyBones hit, Vector3 inheritedVelocity, float crumpleSeconds)
        {
            if (IsActive) return;
            IsActive = true;
            _fellAt = float.NaN;
            _hasLastTarget = false;
            _lead = Vector3.zero;

            if (_animator != null) _animator.enabled = false;
            _crumpleSeconds = Mathf.Max(0f, crumpleSeconds);
            Build(_crumpleSeconds > 0f);

            if (inheritedVelocity.sqrMagnitude > 0f)
            {
                for (int i = 0; i < _bodies.Length; i++)
                {
                    if (_bodies[i] != null) _bodies[i].linearVelocity = inheritedVelocity;
                }
            }

            Rigidbody target = _bodies[0];
            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Bone == hit && _bodies[i] != null) target = _bodies[i];
            }

            if (target != null && impulse.sqrMagnitude > 0f) target.AddForce(impulse, ForceMode.Impulse);
        }

        /// <summary>The spring a crumpling joint starts with, per kilogram it carries.</summary>
        private const float CrumpleSpringPerKg = 14f;

        /// <summary>Damping on a crumpling joint; enough that the sag does not oscillate.</summary>
        private const float CrumpleDamper = 2.5f;

        /// <summary>
        /// Fades the death pose's hold <paramref name="sinceFell"/> seconds into the fall; a no-op
        /// for a body that is not crumpling. Quadratic, so the body holds for a moment and then goes.
        /// </summary>
        public void TickCrumple(float sinceFell)
        {
            if (_crumpleJoints == null || _crumpleSeconds <= 0f) return;

            float remaining = Mathf.Clamp01(1f - sinceFell / _crumpleSeconds);
            float share = remaining * remaining;
            for (int i = 0; i < _crumpleJoints.Length; i++)
            {
                ConfigurableJoint joint = _crumpleJoints[i];
                if (joint == null) continue;

                Rigidbody body = joint.GetComponent<Rigidbody>();
                float mass = body != null ? body.mass : 4f;
                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = CrumpleSpringPerKg * mass * share;
                drive.positionDamper = CrumpleDamper * share;
                joint.slerpDrive = drive;
            }

            if (remaining <= 0f) _crumpleJoints = null;
        }

        /// <summary>
        /// A second impulse on a body that has already fallen -- the <c>S_DEATH</c> that arrived
        /// after the snapshot which felled it -- landing on <paramref name="hit"/>.
        /// </summary>
        public void Push(Vector3 impulse, HumanBodyBones hit)
        {
            if (!IsActive || impulse.sqrMagnitude <= 0f) return;

            Rouse();
            Rigidbody target = _bodies[0];
            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Bone == hit && _bodies[i] != null) target = _bodies[i];
            }
            if (target != null && !target.isKinematic) target.AddForce(impulse, ForceMode.Impulse);
        }

        /// <summary>
        /// Pushes every part away from a blast: a corpse near a grenade tumbles instead of lying
        /// still through it.
        /// </summary>
        public void AddExplosionForce(float force, Vector3 centre, float radius, float upwards)
        {
            if (!IsActive) return;
            Rouse();
            for (int i = 0; i < _bodies.Length; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null || body.isKinematic) continue;
                body.AddExplosionForce(force * body.mass, centre, radius, upwards, ForceMode.Impulse);
            }
        }

        /// <summary>
        /// Whether the body has come to rest: every part asleep or all but still.
        /// </summary>
        public bool IsResting
        {
            get
            {
                if (!IsActive) return false;
                for (int i = 0; i < _bodies.Length; i++)
                {
                    Rigidbody body = _bodies[i];
                    if (body == null || body.isKinematic || body.IsSleeping()) continue;
                    if (body.linearVelocity.sqrMagnitude > 0.04f) return false;
                }
                return true;
            }
        }

        /// <summary>How many bodies the ragdoll is built from.</summary>
        public static int PartCount => Specs.Length;

        /// <summary>Writes where every part is now into the first <see cref="PartCount"/> slots of <paramref name="into"/>.</summary>
        public void SamplePositions(Vector3[] into)
        {
            for (int i = 0; i < _bodies.Length; i++)
            {
                into[i] = _bodies[i] != null ? _bodies[i].position : Vector3.zero;
            }
        }

        /// <summary>
        /// The squared distance the part that moved most has gone since <see cref="SamplePositions"/>
        /// wrote <paramref name="from"/>.
        /// </summary>
        public float FarthestMoveSqr(Vector3[] from)
        {
            float farthest = 0f;
            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i] == null) continue;
                farthest = Mathf.Max(farthest, (_bodies[i].position - from[i]).sqrMagnitude);
            }
            return farthest;
        }

        /// <summary>Whether every part is asleep (or frozen): what <see cref="Settle"/> leaves.</summary>
        public bool IsAsleep
        {
            get
            {
                if (!IsActive) return false;
                for (int i = 0; i < _bodies.Length; i++)
                {
                    Rigidbody body = _bodies[i];
                    if (body == null || body.isKinematic) continue;
                    if (!body.IsSleeping()) return false;
                }
                return true;
            }
        }

        /// <summary>Whether <see cref="Settle"/> has frozen the body and nothing has thrown it since.</summary>
        public bool IsSettled { get; private set; }

        /// <summary>
        /// Freezes a body that has stopped moving where it lies, out of the simulation, until a
        /// blast throws it again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why.</b> A corpse lies on the field for <see cref="RemoteCorpseDirector.LingerSeconds"/>
        /// as eleven interpolated bodies held together by projected joints, and a ragdoll on uneven
        /// ground never gets every part under PhysX's sleep threshold by itself. A 100-bot Forest
        /// Lake match had up to 200 corpse bodies awake at once, none of them visibly moving, and
        /// every awake body costs twice: about 0.014 ms in each physics step, and about 0.04 ms a
        /// frame in scripts, where its interpolated transform makes the next raycast resynchronise
        /// it (fit over 365 five-second windows of <c>[loop]</c> and <c>[physics]</c> lines, release
        /// player, 2026-10-02).
        /// </para>
        /// <para>
        /// <b>Frozen, not asleep.</b> Put to sleep, a settled body woke the moment anything awake
        /// touched it, and bodies fall in heaps where the fighting is: in the same match 144 of 260
        /// corpse bodies were awake again three minutes in. Frozen, it costs nothing and touches
        /// nothing -- the vehicle this client drives passes through it as it does on the server,
        /// which has no client corpses to hit, and a body that falls later lies down through it.
        /// </para>
        /// </remarks>
        public void Settle()
        {
            for (int i = 0; i < _bodies.Length; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null || body.isKinematic) continue;
                body.interpolation = RigidbodyInterpolation.None;
                body.isKinematic = true;
                body.detectCollisions = false;
            }
            IsSettled = true;
        }

        // A settled body is about to be thrown: back into the simulation, smoothed again. Nothing
        // for a body that was never settled -- a live one knocked over, or a corpse sinking.
        private void Rouse()
        {
            if (!IsSettled) return;
            IsSettled = false;
            // A live body thrown again, or seen again, lands again before it may be frozen.
            _fellAt = float.NaN;
            RigidbodyInterpolation smoothing = InterpolationAt(_bones[0].position, Camera.main);
            for (int i = 0; i < _bodies.Length; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null) continue;
                body.isKinematic = false;
                body.detectCollisions = true;
                body.interpolation = smoothing;
            }
        }

        /// <summary>
        /// Freezes every part where it lies, so the body can be moved as one piece (a corpse
        /// sinking out of sight at the end of its time).
        /// </summary>
        public void Freeze()
        {
            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i] == null) continue;
                _bodies[i].isKinematic = true;
                _bodies[i].detectCollisions = false;
            }
        }

        /// <summary>Seconds the pelvis takes to close on where the server's ragdoll has it.</summary>
        private const float SteerSeconds = 0.25f;

        /// <summary>No faster than this toward the target, so one bad sample cannot launch the body.</summary>
        private const float MaxSteerSpeed = 20f;

        /// <summary>
        /// A gap wider than this is a fling the client never saw -- the server's ragdoll was thrown by
        /// a blast, this one merely collapsed -- so the whole body moves by it at once.
        /// </summary>
        private const float SnapMetres = 4f;

        /// <summary>Linear damping while floating: a body in water drifts, it does not swing.</summary>
        private const float WaterDamping = 2f;

        private bool _floating;

        /// <summary>
        /// Keeps a LIVE ragdoll with the server's: the pelvis is driven toward
        /// <paramref name="pelvis"/>, where the server's own ragdoll has it, and the limbs follow on
        /// their joints. For a body knocked over or swimming, never for a corpse (corpses are the
        /// client's own).
        /// </summary>
        /// <remarks>
        /// A velocity, set each frame, rather than a force: it survives however many physics steps
        /// fall between two frames, which a force applied from Update does not.
        /// </remarks>
        public void Steer(Vector3 pelvis) => Steer(pelvis, Time.deltaTime);

        /// <inheritdoc cref="Steer(Vector3)"/>
        /// <param name="pelvis">Where the server's ragdoll has the pelvis now.</param>
        /// <param name="dt">Seconds since the previous call, for the target's own speed.</param>
        /// <remarks>
        /// <b>The target's speed is fed forward and only the gap is steered.</b> Steering the gap
        /// alone (<c>gap / SteerSeconds</c>, capped at <see cref="MaxSteerSpeed"/>) trails a moving
        /// target by its speed times <see cref="SteerSeconds"/> and cannot keep up with one faster
        /// than the cap at all: a body falling from height passes 20 m/s after 1.7 s, the gap grew
        /// past <see cref="SnapMetres"/>, the whole body snapped, fell behind and snapped again --
        /// a remote body jerking down a cliff (playtest 2026-10-03, bug 2).
        /// </remarks>
        public void Steer(Vector3 pelvis, float dt)
        {
            if (!IsActive) return;

            Rigidbody hips = _bodies[0];
            if (hips == null) return;

            Vector3 lead = TrackTarget(pelvis, dt);
            Vector3 gap = pelvis - hips.position;
            if (IsSettled)
            {
                // Frozen (SimulateWhereSeen): carried after the pelvis whole, as the snap below
                // carries a live body, once it has drifted far enough to be worth a write.
                if (gap.sqrMagnitude <= FrozenFollowMetres * FrozenFollowMetres) return;
                for (int i = 0; i < _bodies.Length; i++)
                {
                    if (_bodies[i] != null) _bodies[i].position += gap;
                }
                return;
            }

            if (gap.sqrMagnitude > SnapMetres * SnapMetres)
            {
                // Every body by the same offset, so the joints keep the shape they have.
                for (int i = 0; i < _bodies.Length; i++)
                {
                    if (_bodies[i] == null) continue;
                    _bodies[i].position += gap;
                    _bodies[i].linearVelocity = Vector3.zero;
                }

                return;
            }

            hips.linearVelocity = lead + Vector3.ClampMagnitude(gap / SteerSeconds, MaxSteerSpeed);
        }

        /// <summary>The fastest target speed fed forward: the wire's own velocity range.</summary>
        private const float MaxLeadSpeed = 64f;

        /// <summary>How quickly the fed-forward speed follows the target's, in seconds.</summary>
        private const float LeadSmoothingSeconds = 0.1f;

        private Vector3 _lastTarget;
        private bool _hasLastTarget;
        private Vector3 _lead;

        /// <summary>
        /// The target's velocity, smoothed and bounded so a held or jumping interpolation sample
        /// cannot launch the body.
        /// </summary>
        private Vector3 TrackTarget(Vector3 pelvis, float dt)
        {
            if (_hasLastTarget && dt > 0f)
            {
                Vector3 raw = Vector3.ClampMagnitude((pelvis - _lastTarget) / dt, MaxLeadSpeed);
                _lead = Vector3.Lerp(_lead, raw, 1f - Mathf.Exp(-dt / LeadSmoothingSeconds));
            }

            _lastTarget = pelvis;
            _hasLastTarget = true;
            return _lead;
        }

        /// <summary>How near the camera a knocked-over body keeps simulating once it has landed.</summary>
        internal const float SimulateWithinMetres = 60f;

        /// <summary>How long a knocked-over body simulates before it may be frozen far from the camera.</summary>
        internal const float LandSeconds = 1.5f;

        /// <summary>How far the pelvis moves from a frozen body before the body is carried after it.</summary>
        internal const float FrozenFollowMetres = 0.5f;

        // When SimulateWhereSeen first saw this fall; NaN until then.
        private float _fellAt = float.NaN;

        /// <summary>
        /// Freezes a knocked-over body that has landed far from the camera, and gives it back to the
        /// simulation as soon as the camera is near again. A live body only: a corpse settles by its
        /// own rule (<see cref="RemoteCorpse.TickSettle"/>).
        /// </summary>
        /// <param name="nearCamera">Within <see cref="SimulateWithinMetres"/> of the camera.</param>
        /// <param name="now">Seconds, on any clock that only moves forward.</param>
        /// <remarks>
        /// <para>
        /// <b>Why.</b> A bot knocked over by a blast lies as a ragdoll steered toward the server's
        /// pelvis every frame, so its eleven bodies never sleep. In a 100-bot Forest Lake match eight
        /// of them at once were 88 of the 113 awake bodies the client simulated, three physics steps
        /// a frame, mostly in fights the player could not see (development profile, 2026-10-02).
        /// </para>
        /// <para>
        /// <b>After the fall, not before.</b> For <see cref="LandSeconds"/> the body is thrown and
        /// lands as it always did, wherever it is, so a fall seen through binoculars is a fall. Once
        /// frozen it keeps the pose it landed in and is carried after the pelvis
        /// (<see cref="Steer"/>), and a blast beside it rouses it (<see cref="AddExplosionForce"/>).
        /// </para>
        /// </remarks>
        public void SimulateWhereSeen(bool nearCamera, float now)
        {
            if (!IsActive) return;
            if (float.IsNaN(_fellAt)) _fellAt = now;

            if (nearCamera)
            {
                Rouse();
                return;
            }
            if (!IsSettled && now - _fellAt >= LandSeconds) Settle();
        }

        /// <summary>
        /// Floats the body -- no gravity, and damped -- while it is in water, as the original game's
        /// buoyant ragdoll floats; sinks it again when it is not.
        /// </summary>
        public void SetFloating(bool floating)
        {
            if (!IsActive || floating == _floating) return;
            _floating = floating;

            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i] == null) continue;
                _bodies[i].useGravity = !floating;
                _bodies[i].linearDamping = floating ? WaterDamping : 0f;
            }
        }

        /// <summary>Tears the bodies down and gives the skeleton back to the animator.</summary>
        public void Restore()
        {
            if (!IsActive) return;
            IsActive = false;
            IsSettled = false;
            _floating = false;

            // Joints before bodies: a joint whose connected body is destroyed first logs a PhysX
            // error for the frame in between.
            for (int i = Specs.Length - 1; i >= 0; i--)
            {
                Transform bone = _bones[i];
                if (bone == null) continue;

                Joint joint = bone.GetComponent<Joint>();
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

            _crumpleJoints = null;
            if (_animator == null) return;
            _animator.enabled = true;
            _animator.Rebind();
        }

        private void Build(bool crumple)
        {
            RigidbodyInterpolation smoothing = InterpolationAt(_bones[0].position, Camera.main);
            for (int i = 0; i < Specs.Length; i++)
            {
                Transform bone = _bones[i];
                PartSpec spec = Specs[i];

                _layers[i] = bone.gameObject.layer;
                bone.gameObject.layer = RagdollLayer;

                Rigidbody body = bone.gameObject.AddComponent<Rigidbody>();
                body.mass = spec.Mass;
                body.angularDamping = 0.5f;
                body.interpolation = smoothing;
                _bodies[i] = body;

                AddCollider(bone, spec);
            }

            _crumpleJoints = crumple ? new ConfigurableJoint[Specs.Length] : null;
            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Parent == None) continue;

                Rigidbody parent = BodyFor(Specs[i].Parent);
                if (parent == null) continue;

                if (crumple)
                {
                    _crumpleJoints[i] = AddCrumpleJoint(_bones[i].gameObject, parent, Specs[i].Mass);
                    continue;
                }

                CharacterJoint joint = _bones[i].gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent;
                joint.enableProjection = true;
                joint.lowTwistLimit = new SoftJointLimit { limit = -30f };
                joint.highTwistLimit = new SoftJointLimit { limit = 30f };
                joint.swing1Limit = new SoftJointLimit { limit = 40f };
                joint.swing2Limit = new SoftJointLimit { limit = 40f };
            }
        }

        // The CharacterJoint above as a ConfigurableJoint -- the same twist and swing limits on the
        // same default axes -- plus a slerp drive holding the pose the joint was made in. A
        // ConfigurableJoint's target rotation is relative to its starting orientation, so the
        // identity target IS the death pose.
        private static ConfigurableJoint AddCrumpleJoint(GameObject bone, Rigidbody parent, float mass)
        {
            ConfigurableJoint joint = bone.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parent;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -30f };
            joint.highAngularXLimit = new SoftJointLimit { limit = 30f };
            joint.angularYLimit = new SoftJointLimit { limit = 40f };
            joint.angularZLimit = new SoftJointLimit { limit = 40f };
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.targetRotation = Quaternion.identity;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = CrumpleSpringPerKg * mass,
                positionDamper = CrumpleDamper,
                maximumForce = float.MaxValue,
            };
            return joint;
        }

        private void AddCollider(Transform bone, in PartSpec spec)
        {
            Transform toward = spec.Toward != None ? _resolve(spec.Toward) : null;

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
