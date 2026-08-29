using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns the snapshot fields a remote actor has always been sent into a body that crouches,
    /// aims, holds the right weapon and falls over. phase-V10 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> Before V10 a remote actor was a bare pooled
    /// <c>Transform</c> and the interpolation loop applied exactly two fields: position and yaw.
    /// Pitch, the eight state flags, health, weapon and team were decoded by
    /// <c>DeltaDecoder</c> and thrown away — so remote players never crouched, never aimed,
    /// never ragdolled, and always held the same weapon. Six of the nine router events had
    /// nothing to hang a cosmetic on, which is why the representation comes first and the event
    /// layer sits on top of it (V10 D1).
    /// </para>
    /// <para>
    /// <b>The decode is not here.</b> <see cref="RemoteActorVisualState"/> owns the flag-to-pose
    /// mapping and is graded by CI; this component pushes the result at the engine. That split
    /// is what makes the half of task 2 that CI can judge, judged.
    /// </para>
    /// <para>
    /// <b>Every piece is optional and every absence is announced once.</b> Whether the remote
    /// actor prefab carries an animator, a ragdoll rig, a muzzle anchor and a weapon mount is
    /// authored in the Editor and unreadable from source — client-track item E1. A silent no-op
    /// would be indistinguishable from the bug this phase closes, so a missing piece degrades
    /// visibly and says which piece and which checklist row.
    /// </para>
    /// <para>
    /// <b>No allocation.</b> <see cref="Apply"/> runs per interpolated actor per frame. Animator
    /// parameters are cached hashes, the weapon lookup is a bounded indexed scan on the far side
    /// of the actor seam, and nothing here builds a string outside a once-only warning.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RemoteActorView : MonoBehaviour
    {
        [Tooltip("Drives stance, aim and death. Optional; without it the body holds one pose.")]
        [SerializeField] private Animator _animator;

        /// <remarks>
        /// <para>
        /// <b>Typed <c>MonoBehaviour</c>, not <c>Actor</c>, and not <c>IGameplayActorPresence</c>
        /// — this field is the reason phase C4a is not a two-line change.</b> Unity does not
        /// serialise an interface-typed object reference at all, and <c>Actor</c> is a name this
        /// assembly may no longer speak. <c>MonoBehaviour</c> is the widest type that is both
        /// serialisable and legal here.
        /// </para>
        /// <para>
        /// <b>The field NAME is deliberately unchanged, so no serialised data migrates.</b> Unity
        /// keys an object reference on the field name and the target's file id rather than on the
        /// declared type, so widening the type re-binds an existing reference silently and
        /// correctly, while renaming would have dropped it. The resolved seam below is therefore
        /// called <c>_presence</c>: this field keeps the name it was authored against and no
        /// <c>FormerlySerializedAs</c> migration is in play at all.
        /// </para>
        /// <para>
        /// <b>Checked rather than assumed, 2026-08-26:</b> exactly one asset in the tree carries
        /// this component — <c>Assets/Prefab/Remote Actor Proxy.prefab</c> — and its link reads
        /// <c>_actor: {fileID: 0}</c>. <em>Nothing is wired to this field anywhere</em>, so the
        /// widening could not have broken an authored reference even had the name changed. That
        /// also means the ragdoll and weapon-cosmetic paths below are degraded in the shipped
        /// prefab today and announce themselves through the once-only warnings — client-track
        /// item E1, unchanged by phase C4a and not its to close.
        /// </para>
        /// <para>
        /// The cost is that the inspector will now accept any component. <see cref="Awake"/>
        /// rejects one that is not an actor, once and loudly, rather than presenting as "remote
        /// bodies never ragdoll".
        /// </para>
        /// </remarks>
        [Tooltip("The Actor this body belongs to, for the ragdoll and the weapon set. Optional.")]
        [SerializeField] private MonoBehaviour _actor;

        // Resolved once in Awake. Re-casting per frame would hide a mis-wired field behind a
        // silent null instead of the one-time error below, and HasActor is on a per-frame path.
        private IGameplayActorPresence _presence;

        [Tooltip("Where a muzzle flash and a tracer originate. Optional.")]
        [SerializeField] private Transform _muzzleAnchor;

        [Tooltip("Metres the muzzle drops when crouched, so the flash is at the right height.")]
        [SerializeField] private float _crouchedMuzzleDrop = 0.45f;

        [Tooltip("Metres the muzzle drops when prone.")]
        [SerializeField] private float _proneMuzzleDrop = 1.1f;

        [Tooltip("Rotated by the replicated pitch. Falls back to the animator when unset.")]
        [SerializeField] private Transform _upperBody;

        // Resolved once. Animator.StringToHash allocates nothing but is not free, and this runs
        // for every visible actor every frame.
        //
        // THE NAMES ARE THE CONTROLLER'S, AND FIVE OF THEM WERE NOT, WHICH IS WHY REMOTE BODIES
        // SLID. `Remote Actor Proxy.prefab` and `Player Fps Actor.prefab` share ONE animator
        // controller -- Assets/AnimatorController/Actor.controller, GUID
        // 54b1bd752e9742e459d70a1045db1667 -- and its parameter list is
        //
        //   reset ragdolled falling protect onBack moving movement-x movement-y dead seated
        //   lean hail regroup move halt hurt-x hurt crouched swim swim-forward sprinting
        //   seated-type
        //
        // read from the asset on 2026-08-29, phase-P2 task 3.1. Against that list the writes
        // this class shipped with were `crouch`, `prone`, `sprint`, `aiming`, `dead`,
        // `ragdolled`, `pitch` -- and Animator.SetBool on a hash the controller does not carry
        // is a SILENT no-op, so five of the seven went nowhere and nothing said so.
        //
        // P2 corrects the two that select which locomotion clip plays (`crouch` -> `crouched`,
        // `sprint` -> `sprinting`) and adds the three that make it play at all (`moving`,
        // `movement x`, `movement y`) plus `seated`. `prone`, `aiming` and `pitch` name
        // parameters that DO NOT EXIST on this controller at all; authoring them is animator
        // work, not a parameter-write fix, so they are left standing and are now reported once
        // and loudly by ReportUnknownParameters below rather than failing in silence.
        private static readonly int _hashCrouch    = Animator.StringToHash("crouched");
        private static readonly int _hashProne     = Animator.StringToHash("prone");
        private static readonly int _hashSprint    = Animator.StringToHash("sprinting");
        private static readonly int _hashAim       = Animator.StringToHash("aiming");
        private static readonly int _hashDead      = Animator.StringToHash("dead");
        private static readonly int _hashRagdoll   = Animator.StringToHash("ragdolled");
        private static readonly int _hashPitch     = Animator.StringToHash("pitch");
        private static readonly int _hashSeated    = Animator.StringToHash("seated");
        private static readonly int _hashMoving    = Animator.StringToHash("moving");
        private static readonly int _hashMovementX = Animator.StringToHash("movement x");
        private static readonly int _hashMovementY = Animator.StringToHash("movement y");

        /// <summary>Every parameter name this component writes, for the once-only audit.</summary>
        private static readonly string[] _writtenParameters =
        {
            "crouched", "prone", "sprinting", "aiming", "dead", "ragdolled", "pitch",
            "seated", "moving", "movement x", "movement y",
        };

        private RemoteActorVisualState _state;
        private bool _hasState;

        // Locomotion, phase-P2. The smoothing is stateful by nature -- RemoteLocomotionSolver is
        // pure and takes last frame's value back -- and the sample pair is the displacement
        // fallback the solver uses only where InterestManager has culled the wire velocity.
        private RemoteLocomotion _locomotion;
        private Vector3 _lastSampledPosition;
        private float _lastSampleTime;
        private bool _hasSample;
        private bool _ragdollApplied;
        private byte _appliedWeaponId = byte.MaxValue;
        private IGameplayWeapon _activeWeapon;

        /// <summary>The network actor id this body is currently drawing.</summary>
        public ushort ActorId { get; private set; }

        /// <summary>The last decoded pose. Default until the first snapshot arrives.</summary>
        public RemoteActorVisualState State => _state;

        /// <summary>The last solved locomotion. <c>Idle</c> until this body first moves.</summary>
        public RemoteLocomotion Locomotion => _locomotion;

        /// <summary>Whether any snapshot has been applied since the last <see cref="Bind"/>.</summary>
        public bool HasState => _hasState;

        /// <summary>Whether this body carries a live gameplay actor.</summary>
        public bool HasActor => _presence != null && _presence.Exists;

        /// <summary>Replicated weapon id. Zero before the first snapshot.</summary>
        public byte WeaponId => _state.WeaponId;

        /// <summary>Replicated team. <see cref="TeamId.None"/> before the first snapshot.</summary>
        public byte Team => _hasState ? _state.Team : TeamId.None;

        /// <summary>Always false — the local player is never drawn by this component.</summary>
        public bool IsLocal => false;

        /// <summary>The rigidbody a death impulse is applied to, or null without a rig.</summary>
        public Rigidbody MainRagdollBody => HasRagdollRig ? _presence.MainRagdollBody : null;

        /// <summary>Whether a death can produce a corpse on this prefab.</summary>
        public bool HasRagdollRig => HasActor && _presence.HasRagdollRig;

        /// <summary>
        /// Where a muzzle flash and a tracer start, dropped for stance so a crouched shooter's
        /// flash is not at standing height. Falls back to the body transform.
        /// </summary>
        public Vector3 MuzzlePosition
        {
            get
            {
                Vector3 origin = _muzzleAnchor != null ? _muzzleAnchor.position : transform.position;

                switch (_state.Stance)
                {
                    case RemoteActorStance.Crouching: origin.y -= _crouchedMuzzleDrop; break;
                    case RemoteActorStance.Prone:     origin.y -= _proneMuzzleDrop;    break;
                }

                return origin;
            }
        }

        /// <summary>Whether this body may play a cosmetic. A corpse fires nothing.</summary>
        public bool CanPlayCosmetics => _hasState && _state.CanPlayCosmetics;

        /// <summary>
        /// Resolves the serialised actor link to the seam this assembly speaks.
        /// </summary>
        /// <remarks>
        /// An unset field is normal — the class remark says every piece is optional — so only a
        /// field wired to a component that is <em>not</em> an actor is an error. That is a
        /// mis-wire in an authored prefab, unreadable from source (client-track item E1), and
        /// silently treating it as "no actor" would present as the exact bug phase V10 closed.
        /// </remarks>
        private void Awake()
        {
            // Before the actor-link check, deliberately: the animator audit is about this
            // prefab's controller and holds whether or not an actor is wired, and the shipped
            // prefab has `_actor: {fileID: 0}` -- so behind that early return it would never
            // have run on the one asset that carries this component.
            ReportUnknownParameters();

            if (_actor == null) return;

            _presence = _actor as IGameplayActorPresence;
            if (_presence != null) return;

            Debug.LogError(
                $"[net] {name}'s RemoteActorView has its actor link wired to a "
                + $"{_actor.GetType().Name}, which is not a gameplay actor. This body will not "
                + "ragdoll and will play no weapon cosmetics. Client-track item E1.",
                this);
        }

        /// <summary>
        /// Names, once, every parameter this component writes that the attached controller does
        /// not carry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The failure this exists to stop is the one phase-P2 found.</b> Five of the seven
        /// parameters this class shipped with -- <c>crouch</c>, <c>prone</c>, <c>sprint</c>,
        /// <c>aiming</c>, <c>pitch</c> -- do not exist on <c>Actor.controller</c>, and
        /// <c>Animator.SetBool</c> against an absent hash returns without complaint. That is the
        /// class remark's own "a silent no-op would be indistinguishable from the bug this phase
        /// closes", realised: for three releases the writes ran every frame and moved nothing.
        /// A typo in an animator parameter is unreachable from the compiler and invisible in the
        /// profiler; this is the only place it can be caught at runtime.
        /// </para>
        /// <para>
        /// <b>Allocates, and only here.</b> <c>Animator.parameters</c> builds an array per call,
        /// which is why this runs once in <c>Awake</c> and never from <see cref="Apply"/>. The
        /// class's no-allocation contract is about the per-frame path.
        /// </para>
        /// </remarks>
        private void ReportUnknownParameters()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;

            AnimatorControllerParameter[] declared = _animator.parameters;

            for (int i = 0; i < _writtenParameters.Length; i++)
            {
                string wanted = _writtenParameters[i];

                bool found = false;
                for (int j = 0; j < declared.Length; j++)
                {
                    if (!string.Equals(declared[j].name, wanted, System.StringComparison.Ordinal))
                        continue;

                    found = true;
                    break;
                }

                if (found) continue;

                NetClientPresenterGuard.WarnOnce(
                    "unknown-animator-parameter-" + wanted,
                    $"[net] RemoteActorView writes the animator parameter '{wanted}', which "
                    + $"'{_animator.runtimeAnimatorController.name}' does not declare. That write "
                    + "is a silent no-op and the pose it carries will never be drawn. Either add "
                    + "the parameter to the controller or stop writing it -- phase-P2 task 3.1.");
            }
        }

        /// <summary>
        /// Fells this body, landing the impulse on <paramref name="bone"/>.
        /// </summary>
        /// <remarks>
        /// <b>The impulse goes through the actor, not around it.</b> <c>Actor.KnockOver</c> owns
        /// the re-entrancy guard that lets the death message and the snapshot confirmation both
        /// call it, so a presenter reaching for the rigidbody directly would be a second writer
        /// for one event. Returns false when there is no rig to fell, which is the caller's cue
        /// to degrade visibly rather than silently.
        /// </remarks>
        public bool TryFellBody(Vector3 force, HumanBodyBones bone)
        {
            if (!HasRagdollRig) return false;

            _presence.KnockOver(force, bone);
            return true;
        }

        /// <summary>
        /// Plays one flash and one report on whatever this body is holding.
        /// </summary>
        /// <remarks>
        /// The weapon itself does not cross the seam to the caller: a presenter holding an
        /// <c>IGameplayWeapon</c> across frames would have to repeat this component's own
        /// liveness check, and V10 D9 forbids it touching anything on a weapon but this.
        /// </remarks>
        public void PlayActiveWeaponFireCosmetics()
        {
            if (_activeWeapon == null || !_activeWeapon.Exists) return;

            _activeWeapon.PlayFireCosmetics();
        }

        /// <summary>
        /// Claims this body for an actor id. Called from the registry on spawn, before any
        /// snapshot — a pooled transform carries the previous occupant's pose, and leaving it
        /// would show the new player crouched or ragdolled for one frame.
        /// </summary>
        public void Bind(ushort actorId)
        {
            ActorId          = actorId;
            _state           = default;
            _hasState        = false;
            _ragdollApplied  = false;
            _appliedWeaponId = byte.MaxValue;
            _activeWeapon    = null;

            // Clearing the sample is what makes a respawn not a teleport. The displacement
            // fallback measures this transform against where it was last frame, and a pooled body
            // reappearing across the map would otherwise read as one frame at several hundred
            // metres per second -- a full sprint blend on a man who has just stood up. Dropping
            // the sample yields zero for that frame instead, and the pair re-seeds on the next.
            _locomotion   = RemoteLocomotion.Idle;
            _hasSample    = false;

            if (_animator == null) return;

            _animator.SetBool(_hashCrouch,  false);
            _animator.SetBool(_hashProne,   false);
            _animator.SetBool(_hashSprint,  false);
            _animator.SetBool(_hashAim,     false);
            _animator.SetBool(_hashDead,    false);
            _animator.SetBool(_hashRagdoll, false);
            _animator.SetBool(_hashSeated,  false);
            _animator.SetBool(_hashMoving,  false);
            _animator.SetFloat(_hashMovementX, 0f);
            _animator.SetFloat(_hashMovementY, 0f);
        }

        /// <summary>
        /// Applies one snapshot entry: stance, aim, sprint, weapon, team and the ragdoll state.
        /// </summary>
        /// <remarks>
        /// <b>The ragdoll is edge-triggered off <c>IsRagdoll</c>.</b> The death message enables
        /// the corpse first, for the impulse; this then confirms it. A death that arrives out of
        /// order therefore self-corrects rather than leaving a standing body, and a corpse is
        /// never re-thrown by a repeated snapshot.
        /// </remarks>
        public void Apply(in ActorSnapshotEntry entry)
        {
            _state    = RemoteActorVisualState.From(in entry);
            _hasState = true;

            ApplyWeapon(_state.WeaponId);
            ApplyPitch(_state.PitchDegrees);
            SolveLocomotion();

            if (_animator != null)
            {
                _animator.SetBool(_hashCrouch,  _state.Stance == RemoteActorStance.Crouching);
                _animator.SetBool(_hashProne,   _state.Stance == RemoteActorStance.Prone);
                _animator.SetBool(_hashSprint,  _state.IsSprinting);
                _animator.SetBool(_hashAim,     _state.IsAiming);
                _animator.SetBool(_hashDead,    !_state.IsAlive);
                _animator.SetBool(_hashRagdoll, _state.IsRagdoll);
                _animator.SetBool(_hashSeated,  _state.IsSeated);

                _animator.SetBool(_hashMoving,     _locomotion.IsMoving);
                _animator.SetFloat(_hashMovementX, _locomotion.MovementX);
                _animator.SetFloat(_hashMovementY, _locomotion.MovementY);
            }

            ApplyRagdoll(_state.IsRagdoll);
        }

        /// <summary>
        /// Advances this body's locomotion parameters by one frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Called after the registry has written this frame's position and yaw</b>, which is
        /// what makes both inputs current: <c>RemoteActorRegistry.Update</c> lerps the transform
        /// and only then calls <see cref="Apply"/>. Reading the transform rather than the
        /// snapshot entry is deliberate -- the entry carries the interpolation's END yaw, and the
        /// blend tree's axes have to be expressed in the frame the body is actually DRAWN in, or
        /// a turning body leans against a heading it has not reached yet.
        /// </para>
        /// <para>
        /// <b>No allocation.</b> The displacement pair is two fields, the solver is a static over
        /// structs, and <c>eulerAngles</c> returns a value type.
        /// </para>
        /// </remarks>
        private void SolveLocomotion()
        {
            Transform t = transform;
            Vector3 position = t.position;
            float now = Time.time;

            // Elapsed since the last solve, not Time.deltaTime: Apply runs only for an actor
            // present in the interpolator's `to` snapshot, so a frame where this body is missing
            // is a frame this method skips. Dividing a two-frame displacement by one frame's
            // deltaTime would double the speed and put a run cycle on a walk.
            float elapsed = _hasSample ? now - _lastSampleTime : 0f;
            Vector3 delta = _hasSample ? position - _lastSampledPosition : Vector3.zero;

            _lastSampledPosition = position;
            _lastSampleTime      = now;
            _hasSample           = true;

            Vec3 derived = elapsed > 0f
                ? new Vec3(delta.x / elapsed, 0f, delta.z / elapsed)
                : Vec3.Zero;

            _locomotion = RemoteLocomotionSolver.Solve(
                in _locomotion, in _state, in derived, t.eulerAngles.y, elapsed);
        }

        /// <summary>
        /// Enables the corpse without an impulse. The combat presenter calls this through
        /// <c>Actor.KnockOver</c> instead when it has a force; this is the snapshot's
        /// confirmation path, and the guard below is what keeps the two from fighting.
        /// </summary>
        private void ApplyRagdoll(bool shouldRagdoll)
        {
            if (shouldRagdoll == _ragdollApplied) return;
            _ragdollApplied = shouldRagdoll;

            if (!HasRagdollRig)
            {
                if (!shouldRagdoll) return;

                NetClientPresenterGuard.WarnOnce(
                    "no-ragdoll-rig",
                    "[net] a remote actor reported IsRagdoll but its prefab carries no ragdoll "
                    + "rig, so deaths will not produce corpses. Client-track item E1.");
                return;
            }

            if (shouldRagdoll)
            {
                if (!_presence.IsRagdollActive) _presence.KnockOver(Vector3.zero);
            }
            else if (_presence.IsRagdollActive)
            {
                // A respawn reuses the same body. Without this the snapshot says "alive" while
                // the rig stays limp -- which reads exactly like the netcode dropped the respawn.
                _presence.RestoreFromRagdoll();
            }
        }

        private void ApplyPitch(float pitchDegrees)
        {
            if (_upperBody != null)
            {
                _upperBody.localRotation = Quaternion.Euler(pitchDegrees, 0f, 0f);
                return;
            }

            if (_animator != null) _animator.SetFloat(_hashPitch, pitchDegrees);
        }

        /// <summary>
        /// Selects the weapon the replicated id names, so a shot plays that weapon's flash and
        /// report rather than whatever the prefab happened to be holding.
        /// </summary>
        private void ApplyWeapon(byte weaponId)
        {
            if (weaponId == _appliedWeaponId) return;
            _appliedWeaponId = weaponId;

            if (!HasActor)
            {
                _activeWeapon = null;
                return;
            }

            // The bounded, allocation-free scan itself now lives on the far side of the seam --
            // it reads the game's own loadout array and the game's own ids, so a copy of that
            // shape here would be free to drift from whatever the loadout does next. An unknown
            // id still leaves the previous weapon in place rather than clearing it: a newer
            // server sending a weapon this build does not know should cost the right model,
            // never every cosmetic.
            if (_presence.TryGetWeaponByNetworkId(weaponId, out IGameplayWeapon resolved))
            {
                _activeWeapon = resolved;
                return;
            }

            if (_activeWeapon != null && _activeWeapon.Exists) return;

            _activeWeapon = _presence.ActiveWeapon;
            if (_activeWeapon == null || !_activeWeapon.Exists)
            {
                NetClientPresenterGuard.WarnOnce(
                    "no-remote-weapon",
                    "[net] a remote actor has no weapon to play cosmetics on, so shots will be "
                    + "silent and flashless. Client-track items E1 and E3.");
            }
        }
    }
}
