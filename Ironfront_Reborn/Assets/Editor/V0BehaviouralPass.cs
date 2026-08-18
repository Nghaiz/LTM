using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Editor.Verification
{
    /// <summary>
    /// The Editor half of phase-v0's acceptance: the six behavioural checks in
    /// <c>plans/replication/phases/phase-v0-debt-and-seams.md</c> § 7, run as measurements rather
    /// than as a play session somebody describes afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification scaffolding, not shipping code — it lives under <c>Assets/Editor/</c> so it
    /// compiles into <c>Assembly-CSharp-Editor</c> and cannot reach a player build. Same
    /// precedent as <see cref="NetVerificationHarness"/>.
    /// </para>
    /// <para>
    /// <b>Why this is not a PlayMode test assembly.</b> Issue #83: the project has no assembly
    /// definitions, so every game type is in the predefined <c>Assembly-CSharp</c>, and an
    /// <c>.asmdef</c> test assembly structurally cannot reference a predefined assembly.
    /// <c>Assembly-CSharp-Editor</c> is the only assembly that both sees <see cref="Vehicle"/>
    /// and is excluded from player builds. When #83 lands these become real PlayMode tests; the
    /// measurements do not change, only where they live.
    /// </para>
    /// <para>
    /// <b>Why <c>Time.captureFramerate</c> and not a hand-rolled loop.</b> Setting it makes Unity
    /// advance the clock by exactly <c>1/fps</c> per rendered frame, so the real engine loop runs
    /// the real number of <c>FixedUpdate</c> calls — which is the thing under test. Invoking
    /// <c>FixedUpdate</c> through reflection would prove only that the code is
    /// framerate-independent when somebody else supplies the timestep, which is the claim rather
    /// than evidence for it.
    /// </para>
    /// <para>
    /// Results are written to the console as single <c>[V0PASS]</c> lines so an external driver
    /// (the MCP bridge) can read them back without a UI.
    /// </para>
    /// </remarks>
    public static class V0BehaviouralPass
    {
        public const string Tag = "[V0PASS]";

        public const string JeepPath = "Assets/Prefab/jeep.prefab";
        public const string TankPath = "Assets/Prefab/tank.prefab";
        public const string RhibPath = "Assets/Prefab/rhib.prefab";
        public const string HelicopterPath = "Assets/Prefab/helicopter.prefab";

        /// <summary>Checks 1, 2, 3, 5 and 6 — an isolated rig, no match running.</summary>
        [MenuItem("Ironfront/V0 behavioural pass — isolated checks (1, 2, 3, 5, 6)")]
        public static void BeginIsolated()
        {
            Launch(false);
        }

        /// <summary>Check 4 — needs a live scene with real actors; run it in Dustbowl.</summary>
        [MenuItem("Ironfront/V0 behavioural pass — seat timer (check 4, live scene)")]
        public static void BeginSeatTimer()
        {
            Launch(true);
        }

        private static void Launch(bool seatTimer)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError(Tag + " not in Play Mode. Enter Play Mode first, then run the pass.");
                return;
            }
            GameObject go = new GameObject("V0 Behavioural Pass Runner");
            Object.DontDestroyOnLoad(go);
            V0PassRunner runner = go.AddComponent<V0PassRunner>();
            runner.seatTimerOnly = seatTimer;
        }

        /// <summary>
        /// <see cref="Vehicle.Awake"/> calls <c>ActorManager.RegisterVehicle</c>, which
        /// dereferences the singleton. The isolated rig has no game manager, so stand one up on
        /// an INACTIVE object — inactive means <c>ActorManager.Awake</c> never runs, so none of
        /// the match bootstrap (AI parameter setup, scene-load subscription, spawn waves) starts
        /// and the rig stays a rig.
        /// </summary>
        public static void EnsureActorManager()
        {
            if (ActorManager.instance == null)
            {
                GameObject go = new GameObject("V0 ActorManager (inactive)");
                go.SetActive(false);
                ActorManager.instance = go.AddComponent<ActorManager>();
            }
            if (ActorManager.instance.vehicles == null)
            {
                ActorManager.instance.vehicles = new List<Vehicle>();
            }
        }

        public static GameObject LoadPrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError(Tag + " prefab missing at " + path);
            }
            return prefab;
        }

        public static string V(Vector3 v)
        {
            return v.x.ToString("F4", CultureInfo.InvariantCulture) + ","
                + v.y.ToString("F4", CultureInfo.InvariantCulture) + ","
                + v.z.ToString("F4", CultureInfo.InvariantCulture);
        }

        public static string F(float f)
        {
            return f.ToString("F6", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A driver that answers every <see cref="ActorController"/> question with a value the
    /// harness sets, so a vehicle can be driven with an input that depends on neither a player,
    /// nor a bot goal, nor the framerate.
    /// </summary>
    public sealed class V0StubController : ActorController
    {
        public Vector2 car;
        public Vector2 boat;
        public Vector4 helicopter;

        /// <summary>
        /// When set, <see cref="FacingDirection"/> returns <see cref="facingLocal"/> expressed in
        /// this transform's space — so a turret's aim DEMAND stays constant as the turret
        /// rotates, instead of converging on a fixed world direction. A converging demand would
        /// make the two framerate runs differ for a reason that is not the integrator.
        /// </summary>
        public Transform facingRelativeTo;

        public Vector3 facingLocal = Vector3.forward;

        public override Vector3 FacingDirection()
        {
            if (facingRelativeTo != null)
            {
                return facingRelativeTo.TransformVector(facingLocal);
            }
            return facingLocal;
        }

        public override bool UseMuzzleDirection() { return false; }
        public override Vector3 Velocity() { return Vector3.zero; }
        public override bool OnGround() { return true; }
        public override bool Fire() { return false; }
        public override bool ProjectToGround() { return false; }
        public override Vector3 SwimInput() { return Vector3.zero; }
        public override Vector2 BoatInput() { return boat; }
        public override Vector2 CarInput() { return car; }
        public override Vector4 HelicopterInput() { return helicopter; }
        public override float Lean() { return 0f; }
        public override bool Crouch() { return false; }
        public override bool Aiming() { return false; }
        public override bool Reload() { return false; }
        public override bool IsSprinting() { return false; }
        public override SpawnPoint SelectedSpawnPoint() { return null; }
        public override Transform WeaponParent() { return base.transform; }
        public override WeaponManager.LoadoutSet GetLoadout() { return null; }
        public override bool IsGroupedUp() { return false; }
        public override void SwitchedToWeapon(Weapon weapon) { }
        public override void ReceivedDamage(float damage, float balanceDamage, Vector3 point, Vector3 direction, Vector3 force) { }
        public override void DisableInput() { }
        public override void EnableInput() { }
        public override void StartSeated(Seat seat) { }
        public override void EndSeated(Vector3 exitPosition, Quaternion flatFacing) { }
        public override void StartRagdoll() { }
        public override void GettingUp() { }
        public override void EndRagdoll() { }
        public override void Die() { }
        public override void SpawnAt(Vector3 position) { }
        public override void ApplyRecoil(Vector3 impulse) { }
        public override void StartCrouch() { }
        public override bool EndCrouch() { return true; }
    }

    /// <summary>
    /// Counts <c>FixedUpdate</c> calls on the object it is attached to, so a framerate
    /// comparison can state how many fixed steps each run actually took rather than assuming
    /// the two matched.
    /// </summary>
    public sealed class V0StepCounter : MonoBehaviour
    {
        public int fixedSteps;

        private void FixedUpdate()
        {
            fixedSteps++;
        }
    }

    /// <summary>
    /// Runs the checks and logs one <c>[V0PASS]</c> line each. Spawned by
    /// <see cref="V0BehaviouralPass"/>; not meant to be added by hand.
    /// </summary>
    /// <remarks>
    /// Every framerate comparison rebuilds the vehicle from its prefab between the two runs, so
    /// the only difference between them is <c>Time.captureFramerate</c>. Both runs cover the same
    /// amount of GAME time, which at a fixed 50 Hz step is the same number of
    /// <c>FixedUpdate</c> calls — so any difference in the result is a difference the renderer
    /// caused, which is exactly what V0 removed.
    /// </remarks>
    public sealed class V0PassRunner : MonoBehaviour
    {
        private const int SlowFps = 30;
        private const int FastFps = 144;

        public bool seatTimerOnly;

        private int _pass;
        private int _fail;

        // Scratch, written by the per-run helpers and read by the check that called them. A
        // coroutine cannot return a value and out-parameters cannot cross a yield.
        private Vector3 _pos;
        private Quaternion _rot;
        private float _scalar;
        private int _steps;
        private float _gameSeconds;

        private void Start()
        {
            StartCoroutine(seatTimerOnly ? RunSeatTimer() : RunIsolated());
        }

        private IEnumerator RunIsolated()
        {
            int capture = Time.captureFramerate;
            float scale = Time.timeScale;
            float water = WaterLevel.height;

            V0BehaviouralPass.EnsureActorManager();
            GameObject ground = MakeGround();
            Debug.Log(V0BehaviouralPass.Tag + " begin isolated pass | fixedDeltaTime="
                + V0BehaviouralPass.F(Time.fixedDeltaTime));

            yield return Check0Control();
            yield return Check1Car();
            yield return Check2TurretRate();
            yield return Check2bMouseLatch();
            yield return Check3BoatRolled();
            yield return Check5HelicopterInverted();
            yield return Check6AutoDamage();

            Time.captureFramerate = capture;
            Time.timeScale = scale;
            WaterLevel.height = water;
            Destroy(ground);
            Debug.Log(V0BehaviouralPass.Tag + " done isolated | pass=" + _pass + " fail=" + _fail);
            Destroy(base.gameObject);
        }

        // ----------------------------------------------------------------------- control

        /// <summary>
        /// A negative control, and the reason the greens below are worth anything: it integrates
        /// a fixed step PER RENDERED FRAME, which is exactly what the shipped turret and
        /// helicopter code did, and asserts the same 30-vs-144 equality the real checks assert.
        /// It must report <c>FAIL</c>. A run in which this line says PASS means the comparison
        /// cannot distinguish framerate-dependent code from framerate-independent code, and
        /// every other verdict in the run is uninterpretable.
        /// </summary>
        private IEnumerator Check0Control()
        {
            yield return PerFrameIntegrate(SlowFps, 1f);
            float slow = _scalar;
            yield return PerFrameIntegrate(FastFps, 1f);
            float fast = _scalar;
            // Reported inverted: the control PASSES the suite by FAILING the equality.
            bool detected = Mathf.Abs(slow - fast) >= 1e-3f;
            Report("0-control-per-frame-integration-must-diverge",
                "perFrameStep=1.5deg sum30=" + V0BehaviouralPass.F(slow)
                + "deg sum144=" + V0BehaviouralPass.F(fast)
                + "deg delta=" + V0BehaviouralPass.F(Mathf.Abs(slow - fast))
                + "deg (the equality below would have rejected this; divergence detected="
                + detected + ")",
                detected);
        }

        private IEnumerator PerFrameIntegrate(int fps, float seconds)
        {
            Time.captureFramerate = fps;
            yield return null;
            float sum = 0f;
            int frames = Mathf.RoundToInt(seconds * fps);
            for (int i = 0; i < frames; i++)
            {
                sum += 1.5f;
                yield return null;
            }
            _scalar = sum;
        }

        // ------------------------------------------------------------------------- check 1

        /// <summary>
        /// § 7 check 1 — a car driven with the same input for the same amount of game time ends
        /// in the same place at 30 fps and at 144 fps. Before V0 the drive block ran in
        /// <c>Update</c>, so a 144 Hz peer fed the solver the last of ~2.4 writes per step.
        /// </summary>
        private IEnumerator Check1Car()
        {
            yield return DriveCar(SlowFps, 3f);
            Vector3 slowPos = _pos;
            Quaternion slowRot = _rot;
            int slowSteps = _steps;
            float slowSeconds = _gameSeconds;

            yield return DriveCar(FastFps, 3f);
            Vector3 fastPos = _pos;
            Quaternion fastRot = _rot;
            int fastSteps = _steps;
            float fastSeconds = _gameSeconds;

            float posDelta = Vector3.Distance(slowPos, fastPos);
            float angDelta = Quaternion.Angle(slowRot, fastRot);
            Report("1-car-framerate",
                "travelled=" + V0BehaviouralPass.F(slowPos.magnitude)
                + "m p30=" + V0BehaviouralPass.V(slowPos)
                + " p144=" + V0BehaviouralPass.V(fastPos)
                + " steps30=" + slowSteps + " steps144=" + fastSteps
                + " gameSec30=" + V0BehaviouralPass.F(slowSeconds)
                + " gameSec144=" + V0BehaviouralPass.F(fastSeconds)
                + " posDelta=" + V0BehaviouralPass.F(posDelta)
                + "m angDelta=" + V0BehaviouralPass.F(angDelta) + "deg tol=0.05m/0.5deg",
                posDelta < 0.05f && angDelta < 0.5f);
        }

        private IEnumerator DriveCar(int fps, float seconds)
        {
            Time.captureFramerate = fps;
            // Let the capture rate take effect before the vehicle exists. Unity applies it from
            // the NEXT frame, so a run that spawns immediately takes its first step at whatever
            // rate the PREVIOUS run left behind — which put a 4.9 m gap between two runs that
            // were otherwise identical, and read as a V0 defect until the step counts were
            // printed. Every framerate comparison in this file yields once for the same reason.
            yield return null;
            GameObject go = Spawn(V0BehaviouralPass.JeepPath, new Vector3(0f, 0.7f, 0f), Quaternion.identity);
            Car car = go.GetComponent<Car>();
            V0StepCounter counter = go.AddComponent<V0StepCounter>();
            V0StubController ctrl;
            Actor driver = MakeOccupant(out ctrl, false);
            ctrl.car = new Vector2(0.4f, 1f);
            SeatDriver(car, driver);

            float t0 = Time.time;
            yield return Frames(Mathf.RoundToInt(seconds * fps));
            _pos = car.transform.position;
            _rot = car.transform.rotation;
            _steps = counter.fixedSteps;
            _gameSeconds = Time.time - t0;
            Despawn(go, driver);
        }

        // ------------------------------------------------------------------------- check 2

        /// <summary>
        /// § 7 check 2, and criterion 4's Editor half — a turret held at a constant aim demand
        /// traverses the same arc per second of game time at 30 fps and at 144 fps.
        /// </summary>
        private IEnumerator Check2TurretRate()
        {
            yield return TraverseTurret(SlowFps, 1f, true);
            float tankSlow = _scalar;
            yield return TraverseTurret(FastFps, 1f, true);
            float tankFast = _scalar;
            Report("2-tank-turret-rate",
                "yaw30=" + V0BehaviouralPass.F(tankSlow) + "deg yaw144=" + V0BehaviouralPass.F(tankFast)
                + "deg delta=" + V0BehaviouralPass.F(Mathf.Abs(tankSlow - tankFast)) + "deg tol=1e-3",
                Mathf.Abs(tankSlow - tankFast) < 1e-3f && Mathf.Abs(tankSlow) > 1f);

            yield return TraverseTurret(SlowFps, 1f, false);
            float mountedSlow = _scalar;
            yield return TraverseTurret(FastFps, 1f, false);
            float mountedFast = _scalar;
            Report("2-mounted-turret-rate",
                "yaw30=" + V0BehaviouralPass.F(mountedSlow) + "deg yaw144=" + V0BehaviouralPass.F(mountedFast)
                + "deg delta=" + V0BehaviouralPass.F(Mathf.Abs(mountedSlow - mountedFast)) + "deg tol=1e-3",
                Mathf.Abs(mountedSlow - mountedFast) < 1e-3f && Mathf.Abs(mountedSlow) > 1f);
        }

        /// <summary>
        /// Drives one turret from the BOT input path. The demand is expressed in the muzzle's own
        /// space so it stays constant as the turret rotates; a world-space facing would converge
        /// and the two runs would then differ for a reason that is not the integrator.
        /// </summary>
        private IEnumerator TraverseTurret(int fps, float seconds, bool tankTurret)
        {
            Time.captureFramerate = fps;
            yield return null;
            GameObject go = Spawn(V0BehaviouralPass.TankPath, new Vector3(200f, 0.7f, 0f), Quaternion.identity);
            Freeze(go);
            V0StubController ctrl;
            Actor user = MakeOccupant(out ctrl, true);

            float startYaw;
            float endYaw;
            if (tankTurret)
            {
                TankTurret t = go.GetComponentInChildren<TankTurret>();
                ctrl.facingRelativeTo = t.configuration.muzzle;
                // demand = 0.5 * 3 / 5 = 0.3 of 300 deg/s = 90 deg/s, whose per-step delta is
                // exactly representable at both framerates (phase § 7 deviation 7).
                ctrl.facingLocal = new Vector3(0.5f, 0f, 1f);
                t.user = user;
                startYaw = t.Yaw;
                yield return Frames(Mathf.RoundToInt(seconds * fps));
                endYaw = t.Yaw;
            }
            else
            {
                MountedTurret m = go.GetComponentInChildren<MountedTurret>();
                ctrl.facingRelativeTo = m.configuration.muzzle;
                // demand = 0.3 * 5 / 10 = 0.15 of 600 deg/s = 90 deg/s, as above.
                ctrl.facingLocal = new Vector3(0.3f, 0f, 1f);
                m.user = user;
                startYaw = m.Yaw;
                yield return Frames(Mathf.RoundToInt(seconds * fps));
                endYaw = m.Yaw;
            }
            _scalar = Mathf.DeltaAngle(startYaw, endYaw);
            Despawn(go, user);
        }

        /// <summary>
        /// § 7 check 2, player half — the same physical hand movement per second produces the
        /// same traverse at both framerates. This is the check that catches a regression to
        /// sampling <c>Input.GetAxis</c> from <c>FixedUpdate</c>, which drops ~65% of the motion
        /// at 144 fps and double-counts it at 30 (phase § 7 deviation 2). The mouse is injected
        /// into the latch directly because <c>Input.GetAxis</c> cannot be driven from a script.
        /// </summary>
        private IEnumerator Check2bMouseLatch()
        {
            yield return MouseTraverse(SlowFps, 1f, 90f);
            float slow = _scalar;
            yield return MouseTraverse(FastFps, 1f, 90f);
            float fast = _scalar;
            Report("2b-mouse-latch-conserves-motion",
                "injected=90.000000deg/s yaw30=" + V0BehaviouralPass.F(slow)
                + "deg yaw144=" + V0BehaviouralPass.F(fast)
                + "deg delta=" + V0BehaviouralPass.F(Mathf.Abs(slow - fast)) + "deg tol=1e-2",
                Mathf.Abs(slow - fast) < 1e-2f && Mathf.Abs(slow) > 1f);
        }

        private IEnumerator MouseTraverse(int fps, float seconds, float degreesPerSecond)
        {
            Time.captureFramerate = fps;
            yield return null;
            GameObject go = Spawn(V0BehaviouralPass.TankPath, new Vector3(400f, 0.7f, 0f), Quaternion.identity);
            Freeze(go);
            V0StubController ctrl;
            Actor user = MakeOccupant(out ctrl, false);
            TankTurret t = go.GetComponentInChildren<TankTurret>();
            t.user = user;

            FieldInfo latch = typeof(TankTurret).GetField("_pendingMouseAim",
                BindingFlags.Instance | BindingFlags.NonPublic);
            float perFrame = degreesPerSecond / fps;
            float startYaw = t.Yaw;
            int frames = Mathf.RoundToInt(seconds * fps);
            for (int i = 0; i < frames; i++)
            {
                Vector2 pending = (Vector2)latch.GetValue(t);
                latch.SetValue(t, pending + new Vector2(perFrame, 0f));
                yield return null;
            }
            // Two more fixed steps so the final frame's injection is drained on both runs.
            yield return WaitFixedSteps(2);
            _scalar = Mathf.DeltaAngle(startYaw, t.Yaw);
            Despawn(go, user);
        }

        // ------------------------------------------------------------------------- check 3

        /// <summary>
        /// § 7 check 3 — steering a rolled boat produces yaw and nothing else. Buoyancy and
        /// gravity are switched off for the measurement so the ONLY force in play is the steering
        /// torque; with them on, a rolled hull pitches for reasons that have nothing to do with
        /// the axis under test.
        /// </summary>
        private IEnumerator Check3BoatRolled()
        {
            WaterLevel.height = 1000f;
            Time.captureFramerate = SlowFps;
            // Well clear of the ground plane. Gravity is off for this check, so the height costs
            // nothing — and a hull resting IN the ground slab answers with contact torque, which
            // is what the first version of this check actually measured.
            GameObject go = Spawn(V0BehaviouralPass.RhibPath, new Vector3(600f, 50f, 0f),
                Quaternion.Euler(0f, 0f, 40f));
            Boat boat = go.GetComponent<Boat>();
            boat.floatAcceleration = 0f;
            Rigidbody rb = go.GetComponent<Rigidbody>();
            rb.useGravity = false;
            rb.angularDamping = 0f;
            // A hull's inertia tensor is not spherical, so a free body spun about ONE body axis
            // precesses onto the others within a few steps — Euler's equations, not a steering
            // bug. Sphere-ise the tensor and read the FIRST step's delta, so what is measured is
            // the axis the torque was applied about and nothing that happened afterwards.
            rb.inertiaTensor = Vector3.one;
            rb.inertiaTensorRotation = Quaternion.identity;
            V0StubController ctrl;
            Actor driver = MakeOccupant(out ctrl, false);
            ctrl.boat = new Vector2(1f, 0f);
            SeatDriver(boat, driver);

            yield return WaitFixedSteps(1);
            rb.angularVelocity = Vector3.zero;
            Quaternion frame = go.transform.rotation;
            yield return WaitFixedSteps(1);
            Vector3 local = Quaternion.Inverse(frame) * rb.angularVelocity;
            float offAxis = Mathf.Sqrt(local.x * local.x + local.z * local.z);
            float onAxis = Mathf.Abs(local.y);
            Report("3-boat-steer-axis",
                "rollDeg=40 inWater=" + boat.inWater + " oneStepBodyAngularVel=" + V0BehaviouralPass.V(local)
                + " onAxis=" + V0BehaviouralPass.F(onAxis) + " offAxis=" + V0BehaviouralPass.F(offAxis)
                + " ratio=" + V0BehaviouralPass.F(onAxis > 0f ? offAxis / onAxis : -1f) + " tol=1e-3",
                onAxis > 1e-4f && offAxis < 1e-3f * onAxis);
            Despawn(go, driver);
        }

        // ------------------------------------------------------------------------- check 5

        /// <summary>
        /// § 7 check 5 — an inverted helicopter loses 30 HP per second of game time at any
        /// framerate. Before V0 the call fired once per RENDERED frame, so the rate was whatever
        /// the renderer happened to be doing.
        /// </summary>
        private IEnumerator Check5HelicopterInverted()
        {
            yield return InvertedHelicopter(SlowFps, 2f);
            float slow = _scalar;
            yield return InvertedHelicopter(FastFps, 2f);
            float fast = _scalar;
            Report("5-helicopter-inverted-damage",
                "lost30fps=" + V0BehaviouralPass.F(slow) + "HP lost144fps=" + V0BehaviouralPass.F(fast)
                + "HP expected=60.000000HP delta=" + V0BehaviouralPass.F(Mathf.Abs(slow - fast))
                + " tol=0.5HP/1.5HP",
                Mathf.Abs(slow - fast) < 0.5f && Mathf.Abs(slow - 60f) < 1.5f);
        }

        private IEnumerator InvertedHelicopter(int fps, float seconds)
        {
            Time.captureFramerate = fps;
            yield return null;
            GameObject go = Spawn(V0BehaviouralPass.HelicopterPath, new Vector3(800f, 50f, 0f),
                Quaternion.Euler(0f, 0f, 180f));
            Freeze(go);
            Helicopter heli = go.GetComponent<Helicopter>();
            V0StubController ctrl;
            Actor pilot = MakeOccupant(out ctrl, false);
            SeatDriver(heli, pilot);

            float before = heli.Health;
            yield return Frames(Mathf.RoundToInt(seconds * fps));
            _scalar = before - heli.Health;
            Despawn(go, pilot);
        }

        // ------------------------------------------------------------------------- check 6

        /// <summary>
        /// § 7 check 6 — the abandoned-vehicle decay runs at one rate after repeated
        /// enter/repair/leave cycles, and after repeated repairs of an ALREADY-EMPTY vehicle.
        /// </summary>
        /// <remarks>
        /// The § 7 sequence (6a) ends on a leave, and <c>OccupantLeft</c> cancels every pending
        /// invoke by name before arming one — so 6a lands on one schedule whether or not the fix
        /// is present, and on its own it is a green that proves nothing. 6b is the discriminating
        /// case: the shipped <c>Repair</c> armed unconditionally without cancelling, so five
        /// repairs of an empty vehicle left five schedules and nothing after them to collapse the
        /// stack. Both are reported, and 6b is the one that can fail.
        /// </remarks>
        private IEnumerator Check6AutoDamage()
        {
            yield return DecayTicks(true);
            float cycleTicks = _scalar;
            Report("6a-decay-after-enter-repair-leave-x5",
                "ticks=" + V0BehaviouralPass.F(cycleTicks) + " expected=3 (one schedule)",
                Mathf.Abs(cycleTicks - 3f) < 0.51f);

            yield return DecayTicks(false);
            float repairTicks = _scalar;
            Report("6b-decay-after-empty-repair-x5",
                "ticks=" + V0BehaviouralPass.F(repairTicks)
                + " expected=3 (one schedule; a stack of five would give 15)",
                Mathf.Abs(repairTicks - 3f) < 0.51f);
        }

        private IEnumerator DecayTicks(bool enterRepairLeave)
        {
            Time.captureFramerate = 60;
            GameObject go = Spawn(V0BehaviouralPass.JeepPath, new Vector3(1000f, 0.7f, 0f), Quaternion.identity);
            Freeze(go);
            Vehicle v = go.GetComponent<Vehicle>();
            V0StubController ctrl;
            Actor driver = MakeOccupant(out ctrl, false);
            yield return null;

            for (int i = 0; i < 5; i++)
            {
                if (enterRepairLeave)
                {
                    SeatDriver(v, driver);
                    v.Repair(1f);
                    UnseatDriver(v, driver);
                }
                else
                {
                    v.Repair(1f);
                }
            }

            // The decay starts 50 s after the last arming call and repeats every 2 s, so 55 s of
            // game time covers ticks at +50, +52 and +54 — three, unless the schedule stacked.
            // Compressed with timeScale, which is what InvokeRepeating is driven by.
            float t0 = Time.time;
            float before = v.Health;
            Time.timeScale = 20f;
            while (Time.time - t0 < 55f && !v.dead)
            {
                yield return null;
            }
            Time.timeScale = 1f;
            _scalar = (before - v.Health) / (v.MaxHealth * 0.07f);
            Despawn(go, driver);
        }

        // ------------------------------------------------------------------------- check 4

        /// <summary>
        /// § 7 check 4 — leaving a vehicle and re-entering inside the window keeps the actor's
        /// hitboxes on the vehicle layer; leaving and staying out returns them to the default
        /// layer exactly when the tick timer expires. Runs against a live scene because it needs
        /// a real <see cref="Actor"/> — ragdoll, animator, hitbox colliders — which the isolated
        /// rig deliberately does not build.
        /// </summary>
        private IEnumerator RunSeatTimer()
        {
            Debug.Log(V0BehaviouralPass.Tag + " begin seat-timer pass");
            Vehicle vehicle = FindFreeVehicle();
            Actor actor = FindFreeActor();
            if (actor == null || vehicle == null)
            {
                Report("4-seat-timer", "no free actor/vehicle in the live scene — vehicle="
                    + (vehicle != null) + " actor=" + (actor != null), false);
                Destroy(base.gameObject);
                yield break;
            }

            FieldInfo hitboxes = typeof(Actor).GetField("hitboxColliders",
                BindingFlags.Instance | BindingFlags.NonPublic);
            actor.transform.position = vehicle.transform.position + Vector3.up * 2f;
            yield return null;

            actor.EnterSeat(vehicle.seats[0]);
            yield return WaitFixedSteps(1);
            int seated = FirstLayer(hitboxes, actor);

            actor.LeaveSeat();
            yield return WaitFixedSteps(10);
            int midWindow = FirstLayer(hitboxes, actor);

            // Re-enter inside the window, then wait PAST it. EnterSeat cancels the timer, so the
            // hitboxes must STAY on the vehicle layer — under the shipped coroutine this was a
            // race decided by whatever seat state happened to be true 0.5 s later.
            actor.EnterSeat(vehicle.seats[0]);
            yield return WaitFixedSteps(40);
            int reentered = FirstLayer(hitboxes, actor);

            // Straddle the boundary: still held at 29, returned by 32. REACTIVATE_COLLISION_TICKS
            // is 30, so a probe on only one side of it would not pin the window's length.
            actor.LeaveSeat();
            yield return WaitFixedSteps(29);
            int tick29 = FirstLayer(hitboxes, actor);
            yield return WaitFixedSteps(3);
            int tick32 = FirstLayer(hitboxes, actor);

            Report("4-seat-timer",
                "vehicle=" + vehicle.name + " actor=" + actor.name
                + " fixedStepMs=" + V0BehaviouralPass.F(Time.fixedDeltaTime * 1000f)
                + " seated=" + seated + "(want 16) midWindow=" + midWindow + "(want 16)"
                + " reenteredAfter40Ticks=" + reentered + "(want 16)"
                + " tick29=" + tick29 + "(want 16) tick32=" + tick32 + "(want 8)",
                seated == 16 && midWindow == 16 && reentered == 16 && tick29 == 16 && tick32 == 8);

            Debug.Log(V0BehaviouralPass.Tag + " done seat-timer | pass=" + _pass + " fail=" + _fail);
            Destroy(base.gameObject);
        }

        private static Vehicle FindFreeVehicle()
        {
            if (ActorManager.instance == null || ActorManager.instance.vehicles == null)
            {
                return null;
            }
            for (int i = 0; i < ActorManager.instance.vehicles.Count; i++)
            {
                Vehicle candidate = ActorManager.instance.vehicles[i];
                if (candidate != null && !candidate.dead && candidate.seats != null
                    && candidate.seats.Length > 0 && !candidate.seats[0].IsOccupied())
                {
                    return candidate;
                }
            }
            return null;
        }

        private static Actor FindFreeActor()
        {
            Actor[] actors = Object.FindObjectsByType<Actor>(FindObjectsSortMode.None);
            for (int i = 0; i < actors.Length; i++)
            {
                if (actors[i] != null && !actors[i].dead && actors[i].aiControlled && !actors[i].IsSeated())
                {
                    return actors[i];
                }
            }
            return null;
        }

        private static int FirstLayer(FieldInfo hitboxes, Actor actor)
        {
            Collider[] colliders = (Collider[])hitboxes.GetValue(actor);
            if (colliders == null || colliders.Length == 0)
            {
                return -1;
            }
            return colliders[0].gameObject.layer;
        }

        // ----------------------------------------------------------------------------- rig

        private static GameObject MakeGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "V0 Ground";
            ground.layer = 0;
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(4000f, 1f, 4000f);
            // Every vehicle prefab plays engine audio the moment a driver is seated, and an
            // empty scene has no listener — which Unity reports once per source per frame and
            // buries the [V0PASS] lines under thousands of warnings.
            ground.AddComponent<AudioListener>();
            return ground;
        }

        private static GameObject Spawn(string path, Vector3 position, Quaternion rotation)
        {
            GameObject prefab = V0BehaviouralPass.LoadPrefab(path);
            return Object.Instantiate(prefab, position, rotation);
        }

        /// <summary>
        /// A driver built on an INACTIVE object, so <c>Actor.Awake</c> — which wants a ragdoll, an
        /// animator and an IK rig none of these checks use — never runs. Nothing in the vehicle
        /// code under test reads more of its occupant than <c>controller</c>,
        /// <c>aiControlled</c> and <c>team</c>.
        /// </summary>
        private static Actor MakeOccupant(out V0StubController ctrl, bool aiControlled)
        {
            GameObject go = new GameObject("V0 Stub Occupant");
            go.SetActive(false);
            Actor actor = go.AddComponent<Actor>();
            ctrl = go.AddComponent<V0StubController>();
            actor.controller = ctrl;
            ctrl.actor = actor;
            actor.aiControlled = aiControlled;
            return actor;
        }

        private static void SeatDriver(Vehicle vehicle, Actor actor)
        {
            Seat seat = vehicle.seats[0];
            seat.occupant = actor;
            if (seat.weapon != null)
            {
                seat.weapon.user = actor;
            }
            vehicle.OccupantEntered(seat);
        }

        private static void UnseatDriver(Vehicle vehicle, Actor actor)
        {
            Seat seat = vehicle.seats[0];
            seat.occupant = null;
            vehicle.OccupantLeft(seat, actor);
        }

        private static void Freeze(GameObject go)
        {
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezeAll;
            }
        }

        private static void Despawn(GameObject vehicle, Actor occupant)
        {
            if (vehicle != null)
            {
                Destroy(vehicle);
            }
            if (occupant != null)
            {
                Destroy(occupant.gameObject);
            }
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
            }
        }

        private static IEnumerator WaitFixedSteps(int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return new WaitForFixedUpdate();
            }
        }

        private void Report(string check, string detail, bool ok)
        {
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
            }
            Debug.Log(V0BehaviouralPass.Tag + " check=" + check + " | " + detail
                + " | verdict=" + (ok ? "PASS" : "FAIL"));
        }
    }
}
