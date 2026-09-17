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
        public bool IsInWater
        {
            get
            {
                FpsActorController local = FpsActorController.instance;
                return local != null && local.actor != null && local.actor.inWater;
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
        public void OpenInitialLoadout()
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            local.OpenInitialNetworkLoadout();
        }

        /// <inheritdoc/>
        public bool ConsumeDeployIntent()
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null) return false;

            return local.ConsumeLoadoutDeployPressed();
        }

        /// <inheritdoc/>
        public bool IsLoadoutOpen => LoadoutUi.IsOpen();

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
            // KnockOver is also used for recoverable balance damage in the base game and does
            // not set Actor.dead.  This seam is called only from S_DEATH, so retire the local
            // gameplay update after the ragdoll impulse has been applied.  The next authoritative
            // deploy reverses it in EnterNetworkDeployedState.
            local.actor.MarkNetworkDead();
        }

        /// <inheritdoc/>
        public void ApplyAuthoritativeCombat(byte health, byte weaponId, byte ammoInClip)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null) return;

            Actor actor = local.actor;
            actor.health = health;

            Weapon weapon = actor.activeWeapon;
            if (weapon != null && weapon.NetworkId == weaponId)
                weapon.ammo = ammoInClip;

            // These singleton calls are presentation only and are absent during scene teardown.
            if (IngameUi.instance == null) return;
            actor.UpdateHealthUi();
            if (weapon != null) actor.UpdateAmmoUi();
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

        /// <inheritdoc/>
        public void EnterSeat(IGameplayVehicleBody vehicle, byte seatIndex)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null) return;
            if (vehicle == null || !vehicle.Exists) return;

            // GetComponent rather than a cached reference: a despawn destroys the object while
            // the registry is still holding its entry, so the body can be Exists-false one frame
            // after it was resolvable.
            GameObject hullObject = vehicle.GameObject;
            if (hullObject == null) return;

            Vehicle hull = hullObject.GetComponent<Vehicle>();
            if (hull == null) return;

            // The seat array is authored per vehicle and the index arrives from the wire, so it
            // is untrusted input until it has been ranged. Vehicle.GetSeatPosition makes the same
            // check by catching, which is why this one does not call it.
            Seat[] seats = hull.seats;
            if (seats == null || seatIndex >= seats.Length) return;

            // The bool is checked for the same reason ServerSeatBridge checks it (V4-D7): a false
            // means the live scene refused a seat the server has already booked, so the two
            // sides now disagree about where this body is. There is nothing to roll back on this
            // side -- the server is the authority and its next snapshot is what corrects it --
            // but a silent no-op here is exactly what hid this defect, so it is said out loud.
            if (!local.actor.EnterSeat(seats[seatIndex]))
            {
                Debug.LogWarning("[net] the client refused a seat the server granted (actor "
                    + local.actor.GetInstanceID() + ", vehicle " + hull.NetworkId
                    + ", seat " + seatIndex + "). The body stays on foot until the next "
                    + "authoritative position arrives.");
            }
        }

        /// <inheritdoc/>
        public void LeaveSeat()
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null) return;

            // Actor.LeaveSeat dereferences its seat on the first line, and a Left can arrive for
            // a body this client never seated -- see the interface's own remark.
            if (!local.actor.IsSeated()) return;

            local.actor.LeaveSeat();
        }
    }
}
