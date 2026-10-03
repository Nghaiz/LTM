using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Spawns, despawns and draws every actor this client does not control, positioning them
    /// between snapshots so they move smoothly rather than teleporting 30 times a second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At execution order -50: after <c>NetClientBootstrap</c> has pumped the transport at
    /// -1000, so the snapshot drawn this frame is the newest that arrived, and before anything
    /// at the default order reads a transform.
    /// </para>
    /// <para>
    /// <b>The local player is skipped.</b> It is driven by prediction and reconciliation, not by
    /// interpolation — drawing it from the snapshot buffer would put it two ticks in the past
    /// and reintroduce exactly the input lag prediction exists to remove. The one actor whose
    /// position the player can feel is the one this must not touch.
    /// </para>
    /// <para>
    /// <b>Despawn returns to a pool rather than destroying.</b> Interest management means actors
    /// cross the boundary constantly at 48 actors, and <c>Instantiate</c>/<c>Destroy</c> on every
    /// crossing is both a hitch and a steady allocation — against M1 criterion 9, which asks for
    /// none per tick.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class RemoteActorRegistry : MonoBehaviour
    {
        [Tooltip("Instantiated for each actor the server spawns into view.")]
        [SerializeField] private GameObject _remoteActorPrefab;

        [Tooltip("Pre-warmed pool size. 48 actors is the M1 target world.")]
        [SerializeField] private int _prewarm = ProtocolConstants.MAX_PLAYERS;

        private NetClientBootstrap _client;

        private readonly Dictionary<ushort, Transform> _live =
            new Dictionary<ushort, Transform>(ProtocolConstants.MAX_ACTORS);

        private readonly Stack<Transform> _pool = new Stack<Transform>();

        // Every body this registry made, live or pooled: they sit at the scene root (NewPooled),
        // so they are not destroyed with this object unless destroyed here.
        private readonly List<GameObject> _made = new List<GameObject>();

        // Resolved once per spawn, never per snapshot. GetComponent at 30 Hz x 48 actors is the
        // allocation-free-but-slow trap: it costs nothing the profiler flags as garbage and
        // shows up as a flat frame-time tax instead.
        private readonly Dictionary<ushort, RemoteActorView> _views =
            new Dictionary<ushort, RemoteActorView>(ProtocolConstants.MAX_ACTORS);

        // Network-player positions are CharacterController centres; original AI positions are
        // feet/root pivots. S_SPAWN_ACTOR's IsBot bit preserves that distinction for rendering.
        private readonly HashSet<ushort> _centrePivotActors = new HashSet<ushort>();

        /// <summary>Whether a remote body is a player rather than a bot.</summary>
        /// <remarks>The players are exactly the centre-pivot bodies: both follow S_SPAWN_ACTOR's IsBot bit.</remarks>
        private bool IsHuman(ushort actorId) => _centrePivotActors.Contains(actorId);

        // The vehicle each remote body was last seen seated in, or null on foot. Kept between
        // samples so a frame without one does not turn a driver's icon back into a soldier.
        private readonly Dictionary<ushort, Transform> _seatedIn =
            new Dictionary<ushort, Transform>(ProtocolConstants.MAX_ACTORS);

        // How far the ground under each standing body sat from its replicated feet at the last
        // probe, NaN when there was none within reach. See TryFooting.
        private readonly Dictionary<ushort, float> _footing =
            new Dictionary<ushort, float>(ProtocolConstants.MAX_ACTORS);

        // Each vehicle's crew team this frame, rebuilt by ApplyVehicleMarkers from _seatedIn.
        private readonly Dictionary<Transform, byte> _crewTeam =
            new Dictionary<Transform, byte>(ProtocolConstants.MAX_VEHICLES);

        /// <summary>Actors currently drawn.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Actors held in the pool, ready to reuse.</summary>
        public int PooledCount => _pool.Count;

        /// <summary>
        /// Resolves a network actor id to the transform drawing it, if this client is drawing
        /// one. phase-V10 task 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The local player is never here.</b> It is excluded from <c>_live</c> on purpose —
        /// it is predicted, not interpolated — so a caller asking about its own actor gets a
        /// miss. Check <c>NetClientPresenterGuard.IsLocalActor</c> first, always.
        /// </para>
        /// <para>
        /// <b>A miss is a normal outcome, not an error.</b> Interest management means an actor
        /// that died outside this client's view was never spawned here at all. Do not log one.
        /// </para>
        /// <para>
        /// <b>Do not cache the result across a despawn.</b> Transforms return to a pool and are
        /// handed to whichever actor spawns next, so a held reference silently starts pointing
        /// at a different player.
        /// </para>
        /// <para>
        /// Named for symmetry with <c>ServerActorRegistry.TryFind</c>, so both sides of the wire
        /// read alike.
        /// </para>
        /// </remarks>
        public bool TryFind(ushort actorId, out Transform t) => _live.TryGetValue(actorId, out t);

        /// <summary>
        /// Resolves a network actor id to the <see cref=RemoteActorView/> presenting it. Same
        /// three caveats as <see cref=TryFind/>.
        /// </summary>
        /// <remarks>
        /// The view is resolved once, on spawn, into the pooled entry. A
        /// <c>GetComponent</c> per snapshot would be 48 lookups at 30 Hz for a value that
        /// cannot change while the transform is live.
        /// </remarks>
        public bool TryFindView(ushort actorId, out RemoteActorView view)
        {
            if (_views.TryGetValue(actorId, out view)) return view != null;

            view = null;
            return false;
        }

        /// <summary>
        /// The seated pose of the seat <paramref name="state"/> names: 0 the chair, 1 astride the
        /// quad bike. The chair when the body is not seated, or the vehicle is not (yet) drawn
        /// here -- the pose every seated body had before, so an unknown seat degrades to it.
        /// </summary>
        private int SeatAnimationOf(in ActorSnapshotEntry state)
        {
            if ((state.StateFlags & ActorStateFlags.IsSeated) == 0) return 0;

            // Added to this GameObject by NetClientBootstrap.EnsureVehicleStage, which may run
            // after this component's Awake, so it is looked up on first need.
            if (_vehicles == null) _vehicles = GetComponent<RemoteVehicleRegistry>();
            if (_vehicles == null || !_vehicles.TryFind(state.VehicleId, out NetClientVehicle vehicle)) return 0;
            if (!vehicle.Exists || vehicle.Body == null) return 0;

            return vehicle.Body.GetSeatAnimation(state.SeatIndex);
        }

        private RemoteVehicleRegistry _vehicles;

        private void Awake()
        {
            _client = NetClientBootstrap.Current;

            if (_remoteActorPrefab == null)
            {
                Debug.LogError("[net] RemoteActorRegistry has no prefab. Remote actors will not be drawn.");
                enabled = false;
                return;
            }

            for (int i = 0; i < _prewarm; i++) _pool.Push(NewPooled());
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnSpawnActor += OnSpawn;
            _client.Router.OnDespawnActor += OnDespawn;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnSpawnActor -= OnSpawn;
            _client.Router.OnDespawnActor -= OnDespawn;
        }

        private void Update()
        {
            if (_client == null) return;

            byte localTeam = ResolveLocalTeam();
            bool hasLocalBody = TryLocalPosition(out Vector3 localPosition, out Transform localVehicle);

            SampleBodies(localTeam, hasLocalBody, localPosition);

            // After the bodies, so a crew change sampled this frame is drawn this frame; and
            // outside SampleBodies' early returns, so empty vehicles still show on a map with no
            // other player in it.
            ApplyVehicleMarkers(localTeam, hasLocalBody, localPosition, localVehicle);
        }

        private void SampleBodies(byte localTeam, bool hasLocalBody, Vector3 localPosition)
        {
            if (_live.Count == 0) return;

            SnapshotInterpolator buffer = _client.Router.Interpolator;
            if (buffer.Count < 2) return;

            // The router's clock, not the newest tick plus the prediction clock's Alpha: Alpha
            // wraps at 30 Hz while snapshots land at 20, and that sum threw every remote body
            // back a tick every 100 ms. See InterpolationClock.
            double renderTick = _client.Router.Clock.AdvanceTo(Time.unscaledTimeAsDouble);

            foreach (KeyValuePair<ushort, Transform> pair in _live)
            {
                InterpolationResult result = buffer.TrySampleActor(pair.Key, renderTick, out ActorSample sample);
                bool hasSample = result != InterpolationResult.NotPresent
                                 && result != InterpolationResult.Starved;

                // A corpse lying as a runtime ragdoll keeps the transform it fell from: its bones
                // are simulated in the root's space, so moving the root would drag the body along.
                bool frozen = _views.TryGetValue(pair.Key, out RemoteActorView lying)
                              && lying != null && lying.IsRagdollPosed;

                if (!frozen && hasSample)
                {
                    Vec3 p = sample.Position;
                    float y = p.Y;
                    bool human = _centrePivotActors.Contains(pair.Key);
                    if (human)
                    {
                        bool crouching = (sample.State.StateFlags & ActorStateFlags.IsCrouching) != 0;
                        y -= MovementCore.HeightFor(crouching) * 0.5f;
                    }
                    Quaternion facing = Quaternion.Euler(0f, sample.YawDegrees, 0f);
                    float surface = MovementCore.SurfaceAt(p.X, p.Z);
                    if (Swims(in sample.State) && !float.IsNegativeInfinity(surface))
                    {
                        // At the surface, by its head: a player's capsule and a bot's buoyant
                        // ragdoll both float there, and the pose last drawn says how far under the
                        // head the root has to be (SwimPresentation.RootHeight).
                        y = SwimPresentation.RootHeight(
                            surface,
                            lying != null ? lying.HeadAboveRoot : SwimPresentation.IdleHeadAboveRoot);
                        if ((sample.State.StateFlags & ActorStateFlags.IsRagdoll) != 0 && lying != null)
                            facing = SwimmingHeading(pair.Value.rotation, lying.PlanarVelocity, Time.deltaTime);
                    }
                    // On the ground, with the soles on it rather than the origin: the idle pose
                    // stands its feet above the body's origin (RemoteActorView.SoleLift).
                    else if (StandsOnGround(in sample.State, human) && TryFooting(_footing, pair.Key, lying == null || lying.IsSeen, p.X, y, p.Z, out float ground))
                        y = ground - (lying != null ? lying.SoleLift : RemoteActorView.IdleSoleLiftMetres);
                    pair.Value.SetPositionAndRotation(new Vector3(p.X, y, p.Z), facing);
                }

                // Everything past position and yaw -- pitch, stance, aim, ragdoll, weapon, team
                // -- was decoded and discarded until phase-V10. It is stepped rather than
                // interpolated: these are discrete states, and lerping a crouch is meaningless.
                if (!_views.TryGetValue(pair.Key, out RemoteActorView view) || view == null) continue;
                if (hasSample)
                {
                    _seatedIn[pair.Key] = SeatVehicleOf(in sample.State);
                    view.SetSeatAnimation(SeatAnimationOf(in sample.State));
                    view.Apply(in sample.State);

                    // A body lying as a ragdoll keeps its root where it fell (see `frozen`); the
                    // server's pelvis is steered toward instead, and water floats it.
                    if (view.IsRagdollPosed)
                    {
                        Vec3 pelvis = sample.Position;
                        view.SteerRagdoll(new Vector3(pelvis.X, pelvis.Y, pelvis.Z));
                    }
                }

                // P3 task 3.4. Team, life and seat arrive with the snapshot, not with the spawn, so
                // the icon is written every frame rather than once. SetBodyMarker is idempotent by
                // subject, and both directions are written every frame because every input can
                // change under us -- a body that stops qualifying must lose its icon, or the enemy
                // blip the rule exists to hide simply freezes on screen instead.
                _seatedIn.TryGetValue(pair.Key, out Transform seatedIn);
                ApplyMinimapMarker(
                    pair.Value, view.Team, view.IsAlive, IsHuman(pair.Key), seatedIn != null,
                    localTeam, hasLocalBody, localPosition);
            }
        }

        /// <summary>
        /// Whether a body in <paramref name="state"/> is swimming: alive, in water, not seated.
        /// </summary>
        internal static bool Swims(in ActorSnapshotEntry state)
        {
            ActorStateFlags flags = state.StateFlags;
            return SwimPresentation.Swims(
                (flags & ActorStateFlags.IsAlive) != 0,
                (flags & ActorStateFlags.IsInWater) != 0,
                (flags & ActorStateFlags.IsSeated) != 0);
        }

        /// <summary>How fast a swimming bot turns to face where it is going, degrees per second.</summary>
        internal const float SwimTurnDegreesPerSecond = 240f;

        /// <summary>
        /// A ragdoll-swimming bot's heading: toward where it is moving. The server's yaw for a body
        /// lying as a ragdoll is the one it had when it fell, so a bot drawn by it swims sideways.
        /// </summary>
        internal static Quaternion SwimmingHeading(Quaternion current, Vector3 planarVelocity, float deltaSeconds)
        {
            if (planarVelocity.sqrMagnitude < 0.09f) return current;
            Quaternion toward = Quaternion.LookRotation(new Vector3(planarVelocity.x, 0f, planarVelocity.z), Vector3.up);
            return Quaternion.RotateTowards(current, toward, SwimTurnDegreesPerSecond * deltaSeconds);
        }

        /// <summary>How far above a body's feet the ground under it is looked for.</summary>
        internal const float FootingProbeAboveMetres = 0.5f;

        /// <summary>
        /// How far below a body's feet a surface still counts as the ground it stands on. Past
        /// this the body is in the air, and is drawn where the server has it.
        /// </summary>
        internal const float FootingReachMetres = 0.3f;

        /// <summary>A player whose body rises faster than this is jumping, not standing.</summary>
        internal const float RisingMetresPerSecond = 1f;

        // Scenery: terrain, buildings and props. Vehicles are layer 12 and bodies carry no
        // collider here, so a body is never stood on a vehicle's roof or another body's head.
        private const int GroundMask = 1;

        /// <summary>
        /// Whether a body in <paramref name="state"/> is standing on its feet, and so is drawn on
        /// the ground under it rather than at the height the snapshot carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Owner report 2026-09-29, image 3: a friend stood in the air right after spawning.</b>
        /// Every body is a <c>CharacterController</c>, which rests a skin width (0.08 m) above
        /// whatever it stands on, and a player's replicated centre ran a few centimetres above
        /// its own client's on top of that (friend's log: <c>srv</c> 21.20 against 21.14 standing
        /// still). Measured in the rig: an idle player's feet 0.09 m up, its soles higher still.
        /// The original has the same gap and never showed it, because nobody saw a player from
        /// outside; bots come off a navmesh with gaps of their own, and snapshot interpolation
        /// cuts across every bump and dip between two samples.
        /// </para>
        /// <para>
        /// A grounded player replicates a vertical speed of -10 m/s (MovementCore's
        /// StickToGroundForce), so a falling one cannot be told from it -- only a rising one can,
        /// and that is a jump. The reach below the feet is what keeps the rest of a jump, and a
        /// fall, in the air.
        /// </para>
        /// </remarks>
        internal static bool StandsOnGround(in ActorSnapshotEntry state, bool isHuman)
        {
            ActorStateFlags flags = state.StateFlags;
            if ((flags & ActorStateFlags.IsAlive) == 0) return false;

            const ActorStateFlags offFeet =
                ActorStateFlags.IsRagdoll | ActorStateFlags.IsSeated | ActorStateFlags.IsInWater;
            if ((flags & offFeet) != 0) return false;

            return !isHuman || SnapshotBuilder.UnpackVelocity(in state).Y <= RisingMetresPerSecond;
        }

        /// <summary>
        /// The height of the ground under feet at (<paramref name="x"/>, <paramref name="feetY"/>,
        /// <paramref name="z"/>), false when there is none within reach -- above as well as below,
        /// so feet a little under the surface come up onto it.
        /// </summary>
        internal static bool TryGroundUnder(float x, float feetY, float z, out float groundY)
        {
            var from = new Vector3(x, feetY + FootingProbeAboveMetres, z);
            if (Physics.Raycast(from, Vector3.down, out RaycastHit ground,
                    FootingProbeAboveMetres + FootingReachMetres, GroundMask, QueryTriggerInteraction.Ignore))
            {
                groundY = ground.point.y;
                return true;
            }
            groundY = feetY;
            return false;
        }

        /// <summary>
        /// The ground under a standing body's feet: probed for a body a camera can see, and for one
        /// nobody can see, kept at the rise above its replicated feet the last probe found.
        /// </summary>
        /// <remarks>
        /// One raycast a standing body a frame was about 44 a frame in a 100-bot match, each able to
        /// resynchronise every moved transform first (auto-sync), for bodies mostly out of view. The
        /// kept rise is what a probe would find on the same ground, so a body walking back into
        /// view stands where it would have stood; it is a skin width or two of height, nothing more.
        /// </remarks>
        internal static bool TryFooting(
            Dictionary<ushort, float> footing, ushort actorId, bool seen, float x, float feetY, float z, out float ground)
        {
            if (!seen && footing.TryGetValue(actorId, out float rise))
            {
                ground = feetY + rise;
                return !float.IsNaN(rise);
            }

            bool found = TryGroundUnder(x, feetY, z, out ground);
            footing[actorId] = found ? ground - feetY : float.NaN;
            return found;
        }

        private void OnSpawn(SpawnActorMessage message)
        {
            // The local player is predicted, never interpolated. See the type remarks.
            if (_client != null && message.ActorId == _client.LocalActorId) return;
            if (_live.ContainsKey(message.ActorId)) return;

            Transform t = _pool.Count > 0 ? _pool.Pop() : NewPooled();

            // PLACED from the spawn message, not left wherever the pool parked it. X-17.
            //
            // This looks like a redundant write -- the Update loop below positions everything
            // every frame -- and it is not, for two reasons that only bite together.
            //
            // TryLerpPosition needs the actor in BOTH interpolation endpoints, and interest
            // culling REMOVES a distant actor from the accumulated world (DeltaDecoder.Current),
            // so an actor past InterestManager.CullRadius is in neither. Meanwhile
            // AnnounceNewActors announces EVERY actor to every client regardless of interest.
            // Together those describe an actor that is spawned and never replicated, and for
            // that actor this message carries the only position it will ever be given.
            //
            // Without this the proxy renders at the pool's parking spot and stays there. Measured
            // 2026-08-22 (artifacts/lane-b/x17-measure-01): a client 2570 m from its target drew
            // it at (0, 2000, 0) at every one of seven checkpoints, while the snapshot -- whenever
            // it did arrive -- carried (1088.11, 103.41, 954.30), the victim's exact position to
            // the centimetre. Nothing was wrong with the wire, the interest manager or the
            // decoder. The scripted aim solver reported `resolved: true` and fired 240 rounds
            // into open sky, and a human's crosshair would have done the same.
            float spawnX = Quantize.UnpackPos(message.PosX);
            float spawnY = Quantize.UnpackPos(message.PosY);
            float spawnZ = Quantize.UnpackPos(message.PosZ);
            if (!message.IsBot)
                spawnY -= MovementCore.HeightFor(crouching: false) * 0.5f;

            // On the ground, as SampleBodies stands every body on its feet (StandsOnGround): for
            // an actor out of interest range this is the only position it is ever drawn at, and
            // it came off the server a controller's skin width up (measured 0.15 m in the rig). A
            // body that has just spawned stands still, so it gets the idle pose's sole lift.
            if (TryGroundUnder(spawnX, spawnY, spawnZ, out float spawnGround))
                spawnY = spawnGround - RemoteActorView.IdleSoleLiftMetres;
            t.position = new Vector3(spawnX, spawnY, spawnZ);
            t.rotation = Quaternion.Euler(0f, Quantize.UnpackYaw(message.Yaw), 0f);

            t.gameObject.SetActive(true);
            _live[message.ActorId] = t;
            if (message.IsBot) _centrePivotActors.Remove(message.ActorId);
            else _centrePivotActors.Add(message.ActorId);

            // P3 task 3.4. The icon is bound HERE rather than waiting for the first snapshot,
            // because an actor past InterestManager.CullRadius may never appear in a snapshot at
            // all (see the placement comment above). Waiting would leave exactly those actors --
            // the far ones, the ones a minimap is FOR -- with no icon.
            //
            // P12 D-3 corrects this comment as well as the call. It read "SpawnActorMessage does
            // not carry a team", and it does: ServerTickLoop.AnnounceNewActors constructs it as
            // `new SpawnActorMessage(actor.ActorId, actor.Team, ...)`. That is why the neutral -1
            // is gone -- the team is known at the spawn, so the filter can be applied at the
            // spawn, and a body that is never in a snapshot is filtered rather than drawn.
            //
            // Ledger A-2 is not touched: nothing here registers a proxy with ActorManager, so
            // ActorManager.Player still resolves to the local body. That is the whole reason
            // this goes through MinimapUi.SetMarker (Transform-keyed) and not AddActorBlip.
            bool spawnHasLocal = TryLocalPosition(out Vector3 spawnLocalPosition, out _);
            ApplyMinimapMarker(
                t, message.Team, isAlive: true, isHuman: !message.IsBot, seated: false,
                ResolveLocalTeam(), spawnHasLocal, spawnLocalPosition);

            RemoteActorView view = t.GetComponent<RemoteActorView>();
            if (view != null)
            {
                view.Bind(message.ActorId, message.Team);
                _views[message.ActorId] = view;
            }
            else
            {
                _views.Remove(message.ActorId);
                NetClientPresenterGuard.WarnOnce(
                    "no-remote-actor-view",
                    "[net] the remote actor prefab carries no RemoteActorView, so remote players "
                    + "will slide at a fixed pose: no stance, no aim, no weapon, no ragdoll. This "
                    + "is client-track item E1 -- add the component to the prefab.");
            }
        }

        /// <summary>
        /// How close an enemy has to be before this client's minimap shows it: the interest
        /// manager's own "near", inside which every snapshot carries it.
        /// </summary>
        public const float EnemyRevealRadius = InterestManager.NearRadius;

        /// <summary>
        /// Gives <paramref name="subject"/> a minimap icon if this client should see it, and
        /// takes the icon away if it should not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rule is <see cref="ShouldMarkOnMinimap"/>'s</b>: every living team-mate, and an
        /// enemy only inside <see cref="EnemyRevealRadius"/> of this player. P12 D-3 had stopped
        /// the map showing every enemy on it, which was a map hack; it also stopped showing the
        /// enemy standing next to you (owner report 2026-09-29). The original shows an enemy only
        /// for four seconds after a loud shot (<c>Actor.IsHighlighted</c>); nothing carries that
        /// across the wire, and the owner asked for nearness instead.
        /// </para>
        /// <para>
        /// <b>Drawn as the original draws an <c>Actor</c></b>: in its team's colour, turned to its
        /// heading, and wearing the vehicle's icon while seated (<c>MinimapMarker.FollowBody</c>).
        /// </para>
        /// <para>
        /// <b>An unresolved local team marks nothing.</b> Before the first snapshot names this
        /// client's side there is no way to tell friend from enemy, and the failure directions
        /// are not symmetric: marking everything shows the enemy positions this exists to hide,
        /// while marking nothing costs a blank minimap for the fraction of a second before the
        /// team arrives. <c>MinimapUi.UpdateSpawnPointButtons</c> takes the same branch for the
        /// same reason.
        /// </para>
        /// </remarks>
        private static void ApplyMinimapMarker(
            Transform subject, byte team, bool isAlive, bool isHuman, bool seated,
            byte localTeam, bool hasLocalBody, Vector3 localPosition)
        {
            IMinimapMarkers minimap = NetClientBindings.Minimap;
            if (minimap == null) return;

            // A seated body is drawn by its vehicle's icon (ApplyVehicleMarkers): a soldier icon
            // on top of a jeep icon reads as two things in one place.
            if (seated)
            {
                minimap.RemoveMarker(subject);
                return;
            }

            float sqrDistance = hasLocalBody
                ? (subject.position - localPosition).sqrMagnitude
                : float.PositiveInfinity;

            if (ShouldMarkOnMinimap(team, localTeam, isAlive, sqrDistance))
                minimap.SetBodyMarker(subject, CapturePointOwnership.ToSpawnPointOwner(team), isHuman);
            else
                minimap.RemoveMarker(subject);
        }

        /// <summary>
        /// Gives every replicated vehicle an icon of its own, or takes it away, by
        /// <see cref="ShouldMarkVehicle"/>'s rule.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Owner report 2026-09-29</b>: vehicles need icons, and their movement must be clear.
        /// Until now a vehicle showed only through a soldier seated in it, wearing a vehicle
        /// texture on a soldier's icon. An earlier attempt drew every vehicle as a grey SOLDIER
        /// icon and showed enemy-driven ones wherever they were (the same day's first report);
        /// this draws the vehicle's own silhouette, only while crewed, with exactly the
        /// visibility its crew has.
        /// </para>
        /// <para>
        /// <b>The crew is read from the bodies</b>: <see cref="_seatedIn"/> as the last sampled
        /// snapshot left it, and this player's own seat from its snapshot entry, because the
        /// local body is predicted and never appears among the remote ones.
        /// </para>
        /// </remarks>
        /// <summary>
        /// How many living remote bodies sit in <paramref name="vehicle"/>, and the side of one of
        /// them, from the last sampled snapshot. For the seat prompt.
        /// </summary>
        internal int CrewCount(Transform vehicle, out byte team)
        {
            team = TeamId.None;
            int count = 0;
            if (vehicle == null) return 0;
            foreach (KeyValuePair<ushort, Transform> seat in _seatedIn)
            {
                if (seat.Value != vehicle) continue;
                if (!_views.TryGetValue(seat.Key, out RemoteActorView crew) || crew == null || !crew.IsAlive) continue;
                team = crew.Team;
                count++;
            }
            return count;
        }

        private void ApplyVehicleMarkers(byte localTeam, bool hasLocalBody, Vector3 localPosition, Transform localVehicle)
        {
            IMinimapMarkers minimap = NetClientBindings.Minimap;
            if (minimap == null) return;
            if (_vehicles == null) _vehicles = GetComponent<RemoteVehicleRegistry>();
            if (_vehicles == null) return;

            _crewTeam.Clear();
            foreach (KeyValuePair<ushort, Transform> seat in _seatedIn)
            {
                if (seat.Value == null || _crewTeam.ContainsKey(seat.Value)) continue;
                if (!_views.TryGetValue(seat.Key, out RemoteActorView crew) || crew == null || !crew.IsAlive) continue;
                _crewTeam[seat.Value] = crew.Team;
            }
            if (localVehicle != null && localTeam != TeamId.None) _crewTeam[localVehicle] = localTeam;

            IReadOnlyList<ushort> ids = _vehicles.LiveIds;
            for (int i = 0; i < ids.Count; i++)
            {
                if (!_vehicles.TryFind(ids[i], out NetClientVehicle vehicle) || !vehicle.Exists || vehicle.Body == null)
                    continue;

                Transform subject = vehicle.Body.Transform;
                if (vehicle.DiedFromSnapshot)
                {
                    minimap.RemoveMarker(subject);
                    continue;
                }

                bool crewed = _crewTeam.TryGetValue(subject, out byte crewTeam);
                float sqrDistance = hasLocalBody
                    ? (subject.position - localPosition).sqrMagnitude
                    : float.PositiveInfinity;

                if (ShouldMarkVehicle(crewed, crewTeam, localTeam, sqrDistance))
                    minimap.SetVehicleMarker(subject, CapturePointOwnership.ToSpawnPointOwner(crewTeam));
                else
                    minimap.RemoveMarker(subject);
            }
        }

        /// <summary>
        /// Whether a vehicle belongs on the minimap of a client on <paramref name="localTeam"/>,
        /// <paramref name="sqrDistance"/> squared metres away.
        /// </summary>
        /// <remarks>
        /// An EMPTY vehicle is not drawn: owner ruling 2026-09-29, after a map of grey parked
        /// rides buried the flags they stood beside. A CREWED vehicle is exactly as visible as a
        /// soldier of its crew's team would be (<see cref="ShouldMarkOnMinimap"/>): a team-mate's
        /// tank is always on the map, an enemy's only inside <see cref="EnemyRevealRadius"/>.
        /// Pure, for <see cref="ShouldMarkOnMinimap"/>'s reason.
        /// </remarks>
        internal static bool ShouldMarkVehicle(bool crewed, byte crewTeam, byte localTeam, float sqrDistance)
        {
            if (!crewed) return false;
            return ShouldMarkOnMinimap(crewTeam, localTeam, isAlive: true, sqrDistance);
        }

        /// <summary>
        /// This client's own team, or <see cref="TeamId.None"/> when it is not known yet.
        /// </summary>
        /// <remarks>
        /// Resolved ONCE per <see cref="Update"/> and passed down, not asked per actor: the
        /// answer walks the snapshot dictionary, and a run carrying fifty-odd remote bodies would
        /// otherwise repeat that walk fifty-odd times a frame for a value that cannot change
        /// within one.
        /// </remarks>
        private static byte ResolveLocalTeam()
            => NetPresenterGate.TryResolveLocalTeam(out byte team) ? team : TeamId.None;

        /// <summary>
        /// Whether a body on <paramref name="team"/> belongs on the minimap of a client on
        /// <paramref name="localTeam"/>, <paramref name="sqrDistance"/> squared metres away.
        /// </summary>
        /// <remarks>
        /// Pure, and split out of <see cref="ApplyMinimapMarker"/> for exactly that reason: a rule
        /// that needs a <c>Transform</c>, a marker sink and a live snapshot to observe is a rule
        /// that gets quietly widened later. <c>WhichSideAmIOnTests</c> pins it.
        /// </remarks>
        internal static bool ShouldMarkOnMinimap(byte team, byte localTeam, bool isAlive, float sqrDistance)
        {
            if (!isAlive || team == TeamId.None || localTeam == TeamId.None) return false;
            if (team == localTeam) return true;
            return sqrDistance <= EnemyRevealRadius * EnemyRevealRadius;
        }

        /// <summary>This client's own body, where the latest snapshot puts it.</summary>
        /// <remarks>
        /// From the snapshot rather than the local rig: this registry only ever handles OTHER
        /// actors, and the client-wiring gate's G4 rule keeps per-actor code away from the rig
        /// seam, whose writes would land on this player's own HUD. A reading at 20 Hz is plenty for
        /// a 60 m radius.
        /// </remarks>
        private bool TryLocalPosition(out Vector3 position, out Transform seatedIn)
        {
            position = Vector3.zero;
            seatedIn = null;
            ushort local = _client != null ? _client.LocalActorId : (ushort)0;
            if (local == 0 || !_client.Router.Decoder.Current.TryFind(local, out ActorSnapshotEntry entry))
                return false;

            position = new Vector3(
                Quantize.UnpackPos(entry.PosX), Quantize.UnpackPos(entry.PosY), Quantize.UnpackPos(entry.PosZ));
            seatedIn = SeatVehicleOf(in entry);
            return true;
        }

        /// <summary>The vehicle a snapshot seats this body in, or null on foot or when it is not drawn here.</summary>
        private Transform SeatVehicleOf(in ActorSnapshotEntry state)
        {
            if ((state.StateFlags & ActorStateFlags.IsSeated) == 0) return null;

            if (_vehicles == null) _vehicles = GetComponent<RemoteVehicleRegistry>();
            if (_vehicles == null || !_vehicles.TryFind(state.VehicleId, out NetClientVehicle vehicle)) return null;

            return vehicle.Exists && vehicle.Body != null ? vehicle.Body.Transform : null;
        }

        private void OnDespawn(DespawnActorMessage message)
        {
            if (!_live.TryGetValue(message.ActorId, out Transform t)) return;

            _live.Remove(message.ActorId);
            _views.Remove(message.ActorId);
            _centrePivotActors.Remove(message.ActorId);
            _seatedIn.Remove(message.ActorId);
            _footing.Remove(message.ActorId);

            // BEFORE the transform goes back to the pool. The marker is keyed by that
            // transform, and the pool hands the same one to the NEXT actor -- so a marker left
            // behind is not merely stale, it is an icon wearing the previous occupant's team
            // that SetMarker would then recolour instead of replacing.
            NetClientBindings.Minimap?.RemoveMarker(t);

            t.gameObject.SetActive(false);
            _pool.Push(t);
        }

        /// <summary>
        /// A fresh body for the pool, at the root of this registry's scene.
        /// </summary>
        /// <remarks>
        /// <b>Not under this registry.</b> Unity writes animation results back one transform
        /// hierarchy at a time, so a hundred animated bodies under one parent are written back in
        /// series on one thread, and any one of them moving marks the whole hierarchy changed
        /// (Unity, <i>Optimize your game performance for consoles and PCs</i>, "Separate animating
        /// hierarchies"). At the root, each body is its own hierarchy.
        /// </remarks>
        private Transform NewPooled()
        {
            GameObject go = Instantiate(_remoteActorPrefab);
            SceneManager.MoveGameObjectToScene(go, gameObject.scene);
            go.SetActive(false);
            _made.Add(go);
            return go.transform;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _made.Count; i++)
            {
                if (_made[i] == null) continue;
                if (Application.isPlaying) Destroy(_made[i]); else DestroyImmediate(_made[i]);
            }
            _made.Clear();
        }
    }
}
