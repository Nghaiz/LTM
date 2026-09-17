using Ironfront.Net.Protocol;
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
        public void ApplyAuthoritativeCombat(
            byte health, byte weaponId, byte ammoInClip, SpareAmmo spare, bool clipSettled)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null) return;

            Actor actor = local.actor;

            // Read BEFORE the write. This is the only place on a client that learns a hit
            // landed: Actor.DamageAttributed raises every piece of that feedback and never runs
            // at client role, and S_DEATH arrives only when the hit was fatal.
            float before = actor.health;
            actor.health = health;

            ShowIncomingHit(local, before, health);

            Weapon weapon = actor.activeWeapon;
            if (weapon != null && weapon.NetworkId == weaponId)
            {
                // S4 (CMB-19): only assign the clip once it is settled. Mid-reload, the
                // reconciled count on the wire is deliberately sticky (ClientCombatState keeps
                // the prediction or the frozen pre-reload count, never a half-delivered guess),
                // and assigning it every snapshot is what blinked the HUD through the reload
                // instead of holding it still until the server's answer actually lands.
                if (clipSettled) weapon.ammo = ammoInClip;

                // The reserve, which had two writers and no corrector: Weapon.ReloadDone spends
                // Actor.spareAmmo[slot] locally while the server spends its own pool, so every
                // reload the server refused or performed differently widened the gap for good.
                // A weapon does not know its own index, so the slot is found the way
                // Actor.RemainingSpareAmmoFor finds it.
                for (int slot = 0; slot < actor.weapons.Length; slot++)
                {
                    if (actor.weapons[slot] != weapon) continue;
                    actor.spareAmmo[slot] = LocalSpareEncoding(spare);
                    break;
                }
            }

            // These singleton calls are presentation only and are absent during scene teardown.
            if (IngameUi.instance == null) return;
            actor.UpdateHealthUi();
            if (weapon != null) actor.UpdateAmmoUi();
        }

        /// <summary>
        /// The feedback the game gives a player who has just been shot, minus the parts the wire
        /// cannot supply.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nothing else on a client learns that a hit landed.</b> <c>Actor.DamageAttributed</c>
        /// raises all of this and never runs at client role — the client does not own health —
        /// and <c>S_DEATH</c> arrives only when the hit was fatal. So before this a networked
        /// player watched their health number fall and had no other sign they were being shot at.
        /// </para>
        /// <para>
        /// <b>The two that are applied here are exact, not approximations.</b> The vignette's
        /// intensity is <c>Clamp01(0.3 + (1 - health/100))</c>, copied out of
        /// <c>Actor.DamageAttributed</c> where it is computed from the same health value this
        /// method was just handed. The camera kick's threshold and impulse are
        /// <c>FpsActorController.ReceivedDamage</c>'s own.
        /// </para>
        /// <para>
        /// <b>The directional arc is absent on purpose, and so are the screenshake and the
        /// deafening.</b> The arc needs the shooter's bearing and no message carries one for a
        /// non-lethal hit: <c>S_HIT_CONFIRM</c> goes to the shooter, and <c>S_DEATH</c> carries a
        /// force but only for a kill. <c>ReceivedDamage</c> converts a direction into the
        /// on-screen angle unconditionally, so calling it with a zero vector would draw the arc at
        /// a fixed and wrong bearing — a player would break cover from a threat that is not there.
        /// The shake and the deafening are keyed on BALANCE damage, which the wire carries no
        /// more than it carries the bearing, and inventing an intensity for them would be a
        /// fabrication rather than a translation of a number that exists.
        /// </para>
        /// <para>
        /// A rise is not a hit: a heal, a respawn and a snapshot that changed nothing all leave
        /// <paramref name="after"/> at or above <paramref name="before"/>.
        /// </para>
        /// </remarks>
        private static void ShowIncomingHit(FpsActorController local, float before, float after)
        {
            if (after >= before) return;

            if (before - after > 5f && local.fpParent != null)
            {
                local.fpParent.KickCamera(new Vector3(
                    UnityEngine.Random.Range(5f, 10f),
                    UnityEngine.Random.Range(-10f, 10f),
                    UnityEngine.Random.Range(-5f, 5f)));
            }

            if (IngameUi.instance != null)
            {
                IngameUi.instance.ShowVignette(
                    Mathf.Clamp01(0.3f + (1f - after / 100f)), 6f);
            }
        }

        /// <summary>
        /// The wire's three-state reserve in the encoding <c>Actor.spareAmmo</c> already uses: a
        /// count, <c>-1</c> for no resupply, <c>-2</c> for infinite.
        /// </summary>
        /// <remarks>
        /// Those sentinels are the game's, not this seam's. <c>IngameUi.SetAmmoText</c> switches
        /// on them to choose between a number, an empty label and "/∞", and
        /// <c>Weapon.AllowsResupply</c> tests the same <c>-2</c> against the weapon's own config.
        /// Reading the convention out of the code that draws it, rather than inventing a fourth
        /// one here, is the whole of this method.
        /// </remarks>
        private static int LocalSpareEncoding(SpareAmmo spare) => spare.Kind switch
        {
            SpareAmmoKind.Infinite => -2,
            SpareAmmoKind.NoResupply => -1,
            _ => spare.Rounds,
        };

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
