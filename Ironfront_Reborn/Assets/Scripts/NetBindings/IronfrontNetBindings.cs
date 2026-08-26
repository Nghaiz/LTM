using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Unity.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// Implements the seams <c>Ironfront.Net.Unity.Server</c> declares, in terms of the original
    /// game's own types, and installs them at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This file deliberately lives OUTSIDE any assembly definition, so it compiles into
    /// <c>Assembly-CSharp</c> alongside <c>Actor</c>, <c>Weapon</c>, <c>ActorManager</c> and
    /// <c>SpawnPoint</c>. That is the only assembly that can see both halves: predefined
    /// assemblies are compiled last and automatically reference every asmdef, while no asmdef
    /// can reference back into them.
    /// </para>
    /// <para>
    /// Moving it into an asmdef, or adding an <c>.asmdef</c> anywhere above it, breaks the
    /// build — the game types stop resolving and nothing registers.
    /// </para>
    /// </remarks>
    internal static class IronfrontNetBindings
    {
        /// <summary>
        /// Runs before the first scene's objects exist, so no <c>NetServerActor.Awake</c> can
        /// resolve its source before the resolver is in place. Re-runs on every entry into play
        /// mode, which is what keeps this correct when domain reload is disabled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            NetServerBindings.ActorSourceResolver = ResolveActorSource;
            NetServerBindings.VehicleSourceResolver = ResolveVehicleSource;
            NetServerBindings.DriverInputSinkResolver = NetDriverInputSink.Attach;
            NetServerBindings.AiDriverResolver = ResolveAiDriver;
            NetServerBindings.PlayerBodyFactory = CreatePlayerBody;
            NetServerBindings.SpawnPoints = new ActorManagerSpawnPoints();
            NetSceneBindings.CapturePoints = new SceneCapturePoints();
            // C2. Ironfront.Net.Unity.Input cannot name LoadoutUi or OptionsUi, so the loadout
            // screen's open state and the helicopter preferences are handed to it here. Before
            // the first scene's Awake, and so before FpsActorController can build a
            // LocalInputSource that reads them.
            NetInputBindings.Environment = new LocalInputEnvironmentBinding();

            // C4a. The client presenters may no longer name FpsActorController or IngameUi, so
            // the local player's rig and the hitmarker are handed over here. Both are registered
            // unconditionally, including on a dedicated server: each binding resolves its
            // singleton per call and reports absent when there is none, so registering on a
            // headless process costs one allocation and changes no behaviour. A role test here
            // would be a second copy of a decision NetContext already owns.
            Client.NetClientBindings.LocalPlayer = new LocalPlayerRigBinding();
            Client.NetClientBindings.Hud = new HitmarkerHudBinding();

            // C4b. The vehicle, projectile, decal and scoreboard seams. The two resolvers mirror
            // the server's VehicleSourceResolver for the same reason it exists: many objects,
            // arriving over the wire, and an adapter component on every prefab would be a change
            // to authored assets that this refactor is forbidden from making.
            Client.NetClientBindings.VehicleBodyResolver = ResolveVehicleBody;
            Client.NetClientBindings.ProjectileBodyResolver = ResolveProjectileBody;
            Client.NetClientBindings.VehiclePrefabs = new SceneVehiclePrefabDirectory();
            Client.NetClientBindings.Decals = new DecalSinkBinding();
            Client.NetClientBindings.Objectives = new ScoreUiObjectiveHud();
            Client.NetClientBindings.ProjectileCatalogReader = ProjectileCatalogBinding.Read;
        }

        /// <summary>
        /// The <c>GetComponent&lt;Vehicle&gt;()</c> the client assembly cannot do itself. Null
        /// for a spawned object carrying no vehicle, which the registry reads as an unrenderable
        /// spawn and counts. Phase C4b.
        /// </summary>
        private static Client.IGameplayVehicleBody ResolveVehicleBody(GameObject gameObject)
        {
            if (gameObject == null) return null;

            Vehicle vehicle = gameObject.GetComponent<Vehicle>();
            return vehicle != null ? vehicle : null;
        }

        /// <summary>
        /// The <c>GetComponent&lt;Projectile&gt;()</c> the client assembly cannot do itself. Null
        /// for an instance carrying no projectile — a purely decorative prefab — which the
        /// presenter reads as "spawned but not tracked". Phase C4b.
        /// </summary>
        private static Client.IProjectileBody ResolveProjectileBody(GameObject gameObject)
        {
            if (gameObject == null) return null;

            Projectile projectile = gameObject.GetComponent<Projectile>();
            return projectile != null ? projectile : null;
        }

        /// <summary>
        /// The <c>GetComponent&lt;AiActorController&gt;()</c> the server assembly cannot do
        /// itself. Null for a body that is not bot-driven — the local player's own avatar, a
        /// bare test rig — which the caller reads as "nothing to suspend". Phase-3A.
        /// </summary>
        private static IAiDriver ResolveAiDriver(GameObject gameObject)
        {
            if (gameObject == null) return null;

            AiActorController ai = gameObject.GetComponent<AiActorController>();
            return ai != null ? new AiActorControllerDriver(ai) : null;
        }

        /// <summary>
        /// Builds one player-slot body from the same AI character prefab, and by the same steps,
        /// that <c>ActorManager.CreateAIActor</c> uses for a bot. Phase-3A.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The same prefab as a bot, deliberately.</b> The alternative was
        /// <c>Player Fps Actor</c>, and it carries a camera, an <c>FpsActorController</c> and
        /// the whole client-side prediction stack — stripping those on a server is the fragile
        /// step, and <c>NetVerificationHarness.OpenSecondSlot</c>'s own remark said so before
        /// this existed. The AI character already carries <c>NetServerActor</c> and none of
        /// that.
        /// </para>
        /// <para>
        /// <b><c>SetTeam</c> is not optional and cannot be done on the far side of the seam.</b>
        /// It colours the renderer and the ragdoll's renderer from <c>ColorScheme.TeamColor</c>;
        /// a body that skipped it would be on team 0 wearing the wrong colours, which no test
        /// and no log would report. It also has to run after <c>Awake</c>, which
        /// <c>Instantiate</c> guarantees, because it dereferences fields <c>Awake</c> assigns.
        /// </para>
        /// <para>
        /// <b>The death stamp is what puts the body on the ground.</b> <c>Actor.Awake</c> leaves
        /// every fresh actor <c>dead</c>, and <c>ActorManager.SpawnWave</c> is what places a
        /// dead actor at a spawn point. Stamping the current time makes a pool body eligible for
        /// the first wave, exactly as a bot is; without it the whole pool would stand at the
        /// prefab's origin waiting for a wave that never selects them.
        /// </para>
        /// </remarks>
        private static GameObject CreatePlayerBody(byte team)
        {
            ActorManager manager = ActorManager.instance;

            if (manager == null || manager.actorPrefab == null)
            {
                Debug.LogError(
                    "[net] no ActorManager or no actorPrefab, so no player-slot body can be "
                    + "built. A scene that runs a server needs the _Managers prefab in it.");
                return null;
            }

            GameObject body = UnityEngine.Object.Instantiate(manager.actorPrefab);
            Actor actor = body.GetComponent<Actor>();

            if (actor == null)
            {
                Debug.LogError(
                    $"[net] '{manager.actorPrefab.name}' has no Actor component, so it cannot "
                    + "be a player-slot body.");
                UnityEngine.Object.Destroy(body);
                return null;
            }

            actor.SetTeam(team);
            actor.deathTimestamp = Time.time;

            // X-15. This body is driven by MoveInput from the server, not by its own controller,
            // and ServerPlayer.Tick needs a NetMovementAgent to move it THROUGH COLLISION. The AI
            // character prefab carries NetServerActor but not the agent -- that is authored on
            // Player Fps Actor.prefab and nowhere else -- so without this the session MoveState
            // took the detached branch and free-fell out of the world while the transform stood
            // still, and every shot originated from wherever the ghost had fallen to.
            //
            // Here rather than on the prefab, deliberately: every bot uses the same prefab and
            // is driven by AiActorController, so authoring the agent onto it would put a second
            // driver on every character in the game.
            NetServerActor replicated = body.GetComponent<NetServerActor>();
            if (replicated != null) replicated.AttachMovementAgent();

            return body;
        }

        /// <summary>
        /// The <c>GetComponent&lt;Actor&gt;()</c> that <c>NetServerActor.Awake</c> used to do
        /// itself, now performed here. Null for a replicated object that is not an actor — a
        /// prop, a bare test rig — which is the case the caller's fallback fields exist for.
        /// </summary>
        private static IGameplayActorSource ResolveActorSource(GameObject gameObject)
        {
            if (gameObject == null) return null;

            Actor actor = gameObject.GetComponent<Actor>();
            return actor != null ? new ActorGameplaySource(actor) : null;
        }

        /// <summary>
        /// The <c>GetComponent&lt;Vehicle&gt;()</c> the vehicle registry cannot do itself. Null
        /// for a GameObject that is not a vehicle, which the caller reads as "not replicated".
        /// </summary>
        private static IGameplayVehicleSource ResolveVehicleSource(GameObject gameObject)
        {
            if (gameObject == null) return null;

            Vehicle vehicle = gameObject.GetComponent<Vehicle>();
            return vehicle != null ? new VehicleGameplaySource(vehicle) : null;
        }
    }

    /// <summary>Adapts one <c>Vehicle</c> to <see cref="IGameplayVehicleSource"/>. V4 task 2.</summary>
    /// <remarks>
    /// The <see cref="ActorGameplaySource"/> arrangement, one entity type over. Everything the
    /// netcode decides about a vehicle is engine-free and tested in CI; what is left on this
    /// side of the seam is reading a <c>Rigidbody</c> and calling two <c>MonoBehaviour</c>
    /// methods, which design section 3.2 says cannot be ported.
    /// </remarks>
    internal sealed class VehicleGameplaySource : IGameplayVehicleSource
    {
        private readonly Vehicle _vehicle;

        internal VehicleGameplaySource(Vehicle vehicle) => _vehicle = vehicle;

        /// <summary>
        /// The <c>UnityEngine.Object</c> null check, kept on this side of the seam where it
        /// still means "the native half is alive".
        /// </summary>
        public bool Exists => _vehicle != null;

        public byte NetworkTypeId => _vehicle.NetworkId;

        public int SeatCount => _vehicle.seats != null ? _vehicle.seats.Length : 0;

        public float Health => _vehicle.Health;

        public float MaxHealth => _vehicle.maxHealth;

        public float BurnTimeSeconds => _vehicle.burnTime;

        public bool CrashSkipsBurn => _vehicle.crashSkipsBurn;

        public bool IsBurning => _vehicle.burning;

        public bool IsDead => _vehicle.dead;

        public int OwnerTeam => _vehicle.ownerTeam;

        /// <summary>
        /// Reads the <c>Rigidbody</c>, never the <c>Transform</c> (V4-D14).
        /// </summary>
        /// <remarks>
        /// <c>Vehicle.rigidbody</c> is cached in <c>Awake</c> and is the body PhysX integrates.
        /// The transform lags it by up to one substep, and that lag is CONSTANT rather than
        /// noisy -- so it does not average out, and shipping it would put a fixed interpolation
        /// error into every client for free. The null branch is for a vehicle torn down between
        /// registration and capture.
        /// </remarks>
        public void ReadPose(
            out Vector3 position, out Quaternion rotation,
            out Vector3 linearVelocity, out Vector3 angularVelocity)
        {
            Rigidbody body = _vehicle != null ? _vehicle.rigidbody : null;

            if (body == null)
            {
                position        = _vehicle != null ? _vehicle.transform.position : Vector3.zero;
                rotation        = _vehicle != null ? _vehicle.transform.rotation : Quaternion.identity;
                linearVelocity  = Vector3.zero;
                angularVelocity = Vector3.zero;
                return;
            }

            position        = body.position;
            rotation        = body.rotation;
            linearVelocity  = body.linearVelocity;
            angularVelocity = body.angularVelocity;   // rad/s, which is what Quantize expects
        }

        /// <summary>
        /// Turret aim. Zero until V6 owns it.
        /// </summary>
        /// <remarks>
        /// <c>TankTurret</c> and <c>MountedTurret</c> read <c>Input.GetAxis</c> and
        /// <c>OptionsUi.GetOptions()</c> directly inside <c>Update</c>, and there is no abstract
        /// <c>ActorController</c> member for turret aim (design section 3.6). Building that seam
        /// is V6's; the wire fields exist from V3, so V6 needs no protocol change and this
        /// becomes a two-line read when it lands.
        /// </remarks>
        public float TurretYaw => 0f;

        /// <summary>See <see cref="TurretYaw"/>.</summary>
        public float TurretPitch => 0f;

        public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB)
        {
            if (_vehicle == null)
            {
                subtypeA = 0;
                subtypeB = 0;
                return;
            }

            _vehicle.ReadNetworkSubtypeTail(out subtypeA, out subtypeB);
        }

        public bool IsInWater
            => _vehicle != null && WaterLevel.InWater(_vehicle.transform.position);

        /// <summary>
        /// Airborne. Always false, and honestly so.
        /// </summary>
        /// <remarks>
        /// <c>Vehicle</c> keeps no grounded flag: <c>Car</c> asks each <c>WheelCollider</c>
        /// whether it is touching, and a helicopter has no wheels at all. Synthesising one here
        /// from a raycast would be a second, differently-wrong answer to a question the vehicle
        /// already answers per wheel -- so the flag bit ships clear until a vehicle exposes the
        /// state it actually has. The bit is reserved on the wire either way.
        /// </remarks>
        public bool IsAirborne => false;

        public void SetHealthAuthoritative(float value)
        {
            if (_vehicle != null) _vehicle.SetHealthAuthoritative(value);
        }

        /// <inheritdoc />
        public void Kill()
        {
            // Guarded on the vehicle's OWN dead flag rather than the registry's, because this is
            // the scene's notion of already-destroyed and Die() is not idempotent -- it ejects
            // occupants and notifies the spawner.
            if (_vehicle == null || _vehicle.dead) return;

            _vehicle.Die();
        }

        public Vector3 GetSeatPosition(int seatIndex)
        {
            Seat seat = SeatAt(seatIndex);
            return seat != null ? seat.transform.position : Vector3.positiveInfinity;
        }

        /// <inheritdoc />
        public bool TryEnterSeat(GameObject actorObject, int seatIndex)
        {
            Seat seat = SeatAt(seatIndex);
            if (seat == null || actorObject == null) return false;

            Actor actor = actorObject.GetComponent<Actor>();
            if (actor == null) return false;

            // THE call site that checks EnterSeat's bool (V4-D7). The three shipped ones discard
            // it -- FpsActorController, AiActorController and Actor.SwitchSeat, all offline or AI
            // paths. EnterSeat re-reads seat.vehicle.dead and seat.IsOccupied() against the live
            // scene, so a false here is a condition the arbiter could not see, and the bridge
            // turns it into a refusal rather than a silent divergence.
            return actor.EnterSeat(seat);
        }

        /// <inheritdoc />
        public bool TryLeaveSeat(GameObject actorObject)
        {
            if (actorObject == null) return false;

            Actor actor = actorObject.GetComponent<Actor>();
            if (actor == null || !actor.IsSeated()) return false;

            // Only from a seat on THIS vehicle. An actor sitting in a different one would
            // otherwise be ejected by a request naming a vehicle it is nowhere near -- which is
            // precisely the client claim the arbiter refuses to honour by id.
            if (actor.seat == null || actor.seat.vehicle != _vehicle) return false;

            actor.LeaveSeat();
            return true;
        }

        private Seat SeatAt(int seatIndex)
        {
            if (_vehicle == null || _vehicle.seats == null) return null;
            if (seatIndex < 0 || seatIndex >= _vehicle.seats.Length) return null;

            return _vehicle.seats[seatIndex];
        }
    }

    /// <summary>Adapts one <c>Actor</c> to <see cref="IGameplayActorSource"/>.</summary>
    internal sealed class ActorGameplaySource : IGameplayActorSource
    {
        private readonly Actor _actor;

        internal ActorGameplaySource(Actor actor) => _actor = actor;

        /// <summary>
        /// The <c>UnityEngine.Object</c> null check, kept on this side of the seam where it
        /// still means "the native half is alive".
        /// </summary>
        public bool Exists => _actor != null;

        public float Health
        {
            get => _actor.health;
            set => _actor.health = value;
        }

        public bool IsDead
        {
            get => _actor.dead;
            set => _actor.dead = value;
        }

        /// <summary>
        /// The stagger half of <c>Actor.Damage</c>, without its health or death half.
        /// </summary>
        /// <remarks>
        /// The clamp and the knock-over threshold are copied from <c>Actor.Damage</c> deliberately
        /// -- they are the game's rules, and this is the game's side of the seam. A seated actor
        /// in an enclosed vehicle does not stagger, for the same reason it does not there.
        /// </remarks>
        public void ApplyBalanceDamage(float balanceDamage)
        {
            if (_actor == null || _actor.dead) return;
            if (_actor.IsSeated() && _actor.seat.enclosed) return;

            _actor.balance = Mathf.Max(_actor.balance - balanceDamage, -100f);

            if (_actor.balance < 0f) _actor.KnockOver(Vector3.up * 100f);
        }

        /// <summary>
        /// <c>Actor.SpawnWeapon</c> stamps <c>WeaponManager.NetworkIdOf(entry)</c> onto
        /// <c>Weapon.NetworkId</c> at spawn, and <c>activeWeapon</c> is whichever one is
        /// unholstered. Holstered-everything reports false, not zero.
        /// </summary>
        public bool TryGetActiveWeaponNetworkId(out byte networkId)
        {
            Weapon weapon = _actor.activeWeapon;
            if (weapon == null)
            {
                networkId = 0;
                return false;
            }

            networkId = weapon.NetworkId;
            return true;
        }

        /// <summary>Selects a weapon slot on the wrapped actor. See the seam for why there are
        /// no guards on this side.</summary>
        public void SwitchWeapon(int slot) => _actor.SwitchWeapon(slot);

        /// <summary>Arms the wrapped actor from its loadout. See the seam for why this is not
        /// <c>SpawnAt</c>.</summary>
        public void EquipLoadout() => _actor.EquipLoadout();
    }

    /// <summary>Adapts <c>ActorManager.spawnPoints</c> to <see cref="ISpawnPointDirectory"/>.</summary>
    /// <remarks>
    /// <c>ActorManager.instance</c> is read per call rather than captured, because the array is
    /// rebuilt by <c>FindObjectsOfType&lt;SpawnPoint&gt;</c> on scene load and a captured
    /// reference would go stale across a map change — which is exactly the moment respawning
    /// matters.
    /// </remarks>
    internal sealed class ActorManagerSpawnPoints : ISpawnPointDirectory
    {
        public int Count
        {
            get
            {
                SpawnPoint[] points = Points();
                return points != null ? points.Length : 0;
            }
        }

        public bool IsEligible(int index, int team)
        {
            SpawnPoint point = At(index);
            if (point == null) return false;

            // owner < 0 means "any team", which is how SpawnPoint.owner already defines it.
            return point.owner < 0 || point.owner == team;
        }

        public Vector3 GetSpawnPosition(int index) => At(index).GetSpawnPosition();

        private static SpawnPoint[] Points()
        {
            ActorManager manager = ActorManager.instance;
            return manager != null ? manager.spawnPoints : null;
        }

        private static SpawnPoint At(int index)
        {
            SpawnPoint[] points = Points();
            if (points == null || index < 0 || index >= points.Length) return null;
            return points[index];
        }
    }

    /// <summary>Adapts the scene's <c>CapturePoint</c> components to <see cref="ICapturePointDirectory"/>.</summary>
    /// <remarks>
    /// <para>
    /// Phase-V8 tasks 2 and 3. The array is captured once at <see cref="Bind"/> — unlike
    /// <see cref="ActorManagerSpawnPoints"/>, which re-reads per call — because these indices
    /// ARE the wire ids and re-resolving them mid-match would renumber the flags underneath
    /// every connected client. A map change tears the server down and rebuilds it, which is
    /// where the rebind belongs.
    /// </para>
    /// </remarks>
    internal sealed class SceneCapturePoints : ICapturePointDirectory
    {
        private CapturePoint[] _points = Array.Empty<CapturePoint>();

        public int Count => _points.Length;

        public int Bind(Transform[] authored, out bool discovered, out int skipped)
        {
            discovered = false;
            skipped = 0;

            if (authored != null && authored.Length > 0)
            {
                var resolved = new List<CapturePoint>(authored.Length);
                for (int i = 0; i < authored.Length; i++)
                {
                    Transform slot = authored[i];
                    CapturePoint point = slot != null ? slot.GetComponent<CapturePoint>() : null;
                    if (point == null)
                    {
                        skipped++;
                        continue;
                    }

                    resolved.Add(point);
                }

                _points = resolved.ToArray();
                return _points.Length;
            }

            // D7's fallback. Ordered by name, ordinal: FindObjectsOfType makes no ordering
            // promise at all, and an id order that changes between two runs of the same build
            // is a client/server flag mismatch nobody can reproduce.
            CapturePoint[] found = UnityEngine.Object.FindObjectsOfType<CapturePoint>();
            Array.Sort(found, CompareByName);

            _points = found;
            discovered = found.Length > 0;
            return found.Length;
        }

        public CapturePointDefinition GetDefinition(int index)
        {
            CapturePoint point = _points[index];

            // canBeCaptured == false is an HQ: capture speed of zero, so CapturePointState.Tick
            // moves it nowhere while it still counts for spawning, bleed and elimination.
            float speed = point.canBeCaptured ? point.captureSpeed : 0f;

            return new CapturePointDefinition(
                point.transform.position, point.captureRange, speed, point.name);
        }

        public void ApplyAuthoritativeOwner(int index, int spawnPointOwner, float control, bool contested)
        {
            CapturePoint point = _points[index];
            if (point == null) return;

            point.ApplyAuthoritativeOwner(spawnPointOwner, control, contested);
        }

        public bool RefreshPresence(int index, ReadOnlySpan<ActorPresence> actors)
        {
            CapturePoint point = _points[index];
            if (point == null) return false;

            return point.RefreshPresence(actors);
        }

        /// <summary>
        /// Every scene spawn point owned by <paramref name="team"/>, counted exactly the way
        /// <c>ActorManager.HasSpawnPoint</c> counts them (D10) — including uncapturable HQs,
        /// which is what keeps a team with a base alive.
        /// </summary>
        public int CountSpawnPointsOwnedBy(int team)
        {
            ActorManager manager = ActorManager.instance;
            SpawnPoint[] points = manager != null ? manager.spawnPoints : null;
            if (points == null) return 0;

            int count = 0;
            for (int i = 0; i < points.Length; i++)
            {
                SpawnPoint point = points[i];
                if (point != null && point.owner == team) count++;
            }

            return count;
        }

        private static int CompareByName(CapturePoint a, CapturePoint b)
            => string.CompareOrdinal(a != null ? a.name : string.Empty, b != null ? b.name : string.Empty);
    }

    /// <summary>Adapts one <c>AiActorController</c> to <see cref="IAiDriver"/>. Phase-3A.</summary>
    /// <remarks>
    /// <para>
    /// <b>Enabled, not destroyed, and not replaced.</b> <c>Actor.aiControlled</c> is frozen in
    /// <c>Awake</c> from <c>controller.GetType() == typeof(AiActorController)</c> and then read
    /// by <c>ActorManager.Register</c>, the minimap, LOD, weapon culling and <c>Binoculars</c>.
    /// Swapping the controller out would flip that flag's meaning under all of them at once —
    /// the same argument <c>NetDriverInputSink</c>'s remark makes for not subclassing
    /// <c>ActorController</c> (V5-D7), one layer over.
    /// </para>
    /// <para>
    /// <b>The eight coroutines stop with the component.</b> Disabling a <c>MonoBehaviour</c>
    /// halts its running coroutines, which is what actually stops the bot steering; a flag the
    /// controller checked itself would leave every coroutine running and merely idle.
    /// </para>
    /// </remarks>
    internal sealed class AiActorControllerDriver : IAiDriver
    {
        private readonly AiActorController _ai;

        internal AiActorControllerDriver(AiActorController ai) => _ai = ai;

        /// <summary>
        /// The <c>UnityEngine.Object</c> null check, kept on this side of the seam where it
        /// still means "the native half is alive".
        /// </summary>
        public bool Exists => _ai != null;

        public void Suspend()
        {
            if (_ai != null) _ai.enabled = false;
        }

        public void Resume()
        {
            if (_ai != null) _ai.enabled = true;
        }
    }
}
