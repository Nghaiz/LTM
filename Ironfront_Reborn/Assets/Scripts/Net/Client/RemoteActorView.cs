using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
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
    /// parameters are cached hashes, the weapon lookup is a bounded indexed scan, and nothing
    /// here builds a string outside a once-only warning.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RemoteActorView : MonoBehaviour
    {
        [Tooltip("Drives stance, aim and death. Optional; without it the body holds one pose.")]
        [SerializeField] private Animator _animator;

        [Tooltip("The Actor this body belongs to, for the ragdoll and the weapon set. Optional.")]
        [SerializeField] private Actor _actor;

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
        private static readonly int _hashCrouch   = Animator.StringToHash("crouch");
        private static readonly int _hashProne    = Animator.StringToHash("prone");
        private static readonly int _hashSprint   = Animator.StringToHash("sprint");
        private static readonly int _hashAim      = Animator.StringToHash("aiming");
        private static readonly int _hashDead     = Animator.StringToHash("dead");
        private static readonly int _hashRagdoll  = Animator.StringToHash("ragdolled");
        private static readonly int _hashPitch    = Animator.StringToHash("pitch");

        private RemoteActorVisualState _state;
        private bool _hasState;
        private bool _ragdollApplied;
        private byte _appliedWeaponId = byte.MaxValue;
        private Weapon _activeWeapon;

        /// <summary>The network actor id this body is currently drawing.</summary>
        public ushort ActorId { get; private set; }

        /// <summary>The last decoded pose. Default until the first snapshot arrives.</summary>
        public RemoteActorVisualState State => _state;

        /// <summary>Whether any snapshot has been applied since the last <see cref="Bind"/>.</summary>
        public bool HasState => _hasState;

        /// <summary>The <c>Actor</c> this body belongs to, or null on a prefab without one.</summary>
        public Actor Actor => _actor;

        /// <summary>The weapon the replicated <c>WeaponId</c> selects, or null.</summary>
        public Weapon ActiveWeapon => _activeWeapon;

        /// <summary>Replicated weapon id. Zero before the first snapshot.</summary>
        public byte WeaponId => _state.WeaponId;

        /// <summary>Replicated team. <see cref="TeamId.None"/> before the first snapshot.</summary>
        public byte Team => _hasState ? _state.Team : TeamId.None;

        /// <summary>Always false — the local player is never drawn by this component.</summary>
        public bool IsLocal => false;

        /// <summary>The rigidbody a death impulse is applied to, or null without a rig.</summary>
        public Rigidbody MainRagdollBody
            => _actor != null && _actor.ragdoll != null ? _actor.ragdoll.MainRigidbody() : null;

        /// <summary>Whether a death can produce a corpse on this prefab.</summary>
        public bool HasRagdollRig => _actor != null && _actor.ragdoll != null;

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

            if (_animator == null) return;

            _animator.SetBool(_hashCrouch,  false);
            _animator.SetBool(_hashProne,   false);
            _animator.SetBool(_hashSprint,  false);
            _animator.SetBool(_hashAim,     false);
            _animator.SetBool(_hashDead,    false);
            _animator.SetBool(_hashRagdoll, false);
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

            if (_animator != null)
            {
                _animator.SetBool(_hashCrouch,  _state.Stance == RemoteActorStance.Crouching);
                _animator.SetBool(_hashProne,   _state.Stance == RemoteActorStance.Prone);
                _animator.SetBool(_hashSprint,  _state.IsSprinting);
                _animator.SetBool(_hashAim,     _state.IsAiming);
                _animator.SetBool(_hashDead,    !_state.IsAlive);
                _animator.SetBool(_hashRagdoll, _state.IsRagdoll);
            }

            ApplyRagdoll(_state.IsRagdoll);
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

            if (_actor == null || _actor.ragdoll == null)
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
                if (!_actor.ragdoll.IsRagdoll()) _actor.KnockOver(Vector3.zero);
            }
            else if (_actor.ragdoll.IsRagdoll())
            {
                // A respawn reuses the same body. Without this the snapshot says "alive" while
                // the rig stays limp -- which reads exactly like the netcode dropped the respawn.
                _actor.ragdoll.InstantAnimate();
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

            if (_actor == null || _actor.weapons == null)
            {
                _activeWeapon = null;
                return;
            }

            // Indexed, not foreach, and bounded by the loadout size. An unknown id leaves the
            // previous weapon in place rather than clearing it: a newer server sending a weapon
            // this build does not know should cost the right model, never every cosmetic.
            for (int i = 0; i < _actor.weapons.Length; i++)
            {
                Weapon candidate = _actor.weapons[i];
                if (candidate == null || candidate.NetworkId != weaponId) continue;

                _activeWeapon = candidate;
                return;
            }

            if (_activeWeapon != null) return;

            _activeWeapon = _actor.activeWeapon;
            if (_activeWeapon == null)
            {
                NetClientPresenterGuard.WarnOnce(
                    "no-remote-weapon",
                    "[net] a remote actor has no weapon to play cosmetics on, so shots will be "
                    + "silent and flashless. Client-track items E1 and E3.");
            }
        }
    }
}
