using Ironfront.Net.Unity;
using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="ILocalPlayerRig"/>: every read of
    /// <c>FpsActorController.instance</c> the client netcode used to make, on this side of the
    /// seam. Phase C4a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stateless, and it resolves the singleton per call.</b> Holding the controller would be
    /// wrong for exactly the reason two of the call sites it replaces already documented: the
    /// body is spawned, killed and respawned independently of anything holding this, so a cached
    /// reference goes stale precisely at a death — the one moment the respawn button matters.
    /// A single instance of this class is therefore registered once and lives for the process.
    /// </para>
    /// <para>
    /// <b>Every member is safe when the rig is absent</b>, which is the normal state on a
    /// headless server and between a death and a respawn. That is not defensive padding: it is
    /// the branch each original call site had as <c>instance == null</c>, kept where the caller
    /// can no longer write it.
    /// </para>
    /// </remarks>
    internal sealed class LocalPlayerRigBinding : ILocalPlayerRig
    {
        /// <inheritdoc/>
        public bool Exists => FpsActorController.instance != null;

        /// <inheritdoc/>
        public IInputSource InputSource
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null ? local.InputSource : null;
            }
        }

        /// <inheritdoc/>
        public GameObject GameObject
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null ? local.gameObject : null;
            }
        }

        /// <inheritdoc/>
        public bool IsInputEnabled
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null && local.IsInputEnabled;
            }
        }

        /// <inheritdoc/>
        public void SetInputSource(IInputSource source)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            local.SetInputSource(source);
        }

        /// <inheritdoc/>
        public void EnableInput()
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            local.EnableInput();
        }

        /// <inheritdoc/>
        public void DisableInput()
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            local.DisableInput();
        }

        /// <inheritdoc/>
        public void EnterDeployedView()
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            local.EnterDeployedView();
        }

        /// <inheritdoc/>
        public bool IsDriving(IGameplayActorPresence actor)
        {
            if (actor == null) return false;

            FpsActorController local = FpsActorController.instance;
            return local != null && ReferenceEquals(local.actor, actor);
        }

        /// <inheritdoc/>
        public int Team => FpsActorController.playerTeam;

        /// <inheritdoc/>
        public void SetTeam(int team)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null) return;

            local.actor.SetTeam(team);
        }

        /// <inheritdoc/>
        public Vector3 Position
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null ? local.transform.position : Vector3.zero;
            }
        }

        /// <inheritdoc/>
        public float YawDegrees
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null ? local.transform.eulerAngles.y : 0f;
            }
        }

        /// <inheritdoc/>
        public bool CanApplyScreenshake
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null && local.fpParent != null;
            }
        }

        /// <inheritdoc/>
        public void ApplyScreenshake(float magnitude, int iterations)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.fpParent == null) return;

            local.fpParent.ApplyScreenshake(magnitude, iterations);
        }

        /// <inheritdoc/>
        public bool HasFellableBody
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null && local.actor != null && local.actor.ragdoll != null;
            }
        }

        /// <inheritdoc/>
        public void FellBody(Vector3 force, HumanBodyBones bone)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null || local.actor.ragdoll == null) return;

            local.actor.KnockOver(force, bone);
        }

        /// <inheritdoc/>
        public void GetChosenLoadout(
            out byte primary, out byte secondary, out byte gear1, out byte gear2, out byte gear3)
        {
            primary = secondary = gear1 = gear2 = gear3 = 0;

            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            WeaponManager.LoadoutSet loadout = local.GetLoadout();
            if (loadout == null) return;

            primary   = WeaponManager.NetworkIdOf(loadout.primary);
            secondary = WeaponManager.NetworkIdOf(loadout.secondary);
            gear1     = WeaponManager.NetworkIdOf(loadout.gear1);
            gear2     = WeaponManager.NetworkIdOf(loadout.gear2);
            gear3     = WeaponManager.NetworkIdOf(loadout.gear3);
        }
    }
}
