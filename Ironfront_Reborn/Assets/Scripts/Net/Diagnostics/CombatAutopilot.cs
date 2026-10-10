// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Fights for a measuring client (<c>IRONFRONT_AUTOPLAY_FIGHT=1</c>, beside
    /// <see cref="MenuAutopilot"/>): turns the camera onto the nearest enemy body it can see and
    /// fires, so a live match on the real servers measures hit registration with no hand on the
    /// mouse (owner's run of 2026-10-10, phase P38: "aimed dead on and it does not hit").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It turns the camera, not a number on the wire.</b> Since 14.0.6 a round leaves for the
    /// point the camera's own aim rests on and the client reports what it struck, so a scripted
    /// aim that only steered <c>C_INPUT</c> (as lane B's programmes do) would fire one way and
    /// shoot another. The camera's parent carries the mouse's yaw and pitch, and the mouse look
    /// adds nothing to it while no mouse moves, so setting it is aiming.
    /// </para>
    /// <para>
    /// <b>What it reports.</b> Every <see cref="StatsSeconds"/>: the rounds the client saw strike
    /// a body (<see cref="ClientShotReporter.HitsSent"/>) against the hits the server confirmed,
    /// with heads and kills. The server's own <c>[shot-report]</c> line says why any claim was
    /// refused. The two together are the whole pipeline.
    /// </para>
    /// </remarks>
    public sealed class CombatAutopilot : MonoBehaviour
    {
        /// <summary>The farthest enemy it engages, metres.</summary>
        private const float MaxRangeMetres = 150f;

        private const float RetargetSeconds = 0.5f;
        private const float StatsSeconds = 15f;

        /// <summary>Trigger pulses a second: a semi-automatic weapon fires on each press.</summary>
        private const float PulsesPerSecond = 8f;

        /// <summary>The bone the body hitbox rides, and that box's centre on it.</summary>
        private const string BodyBone = "Bone_002";

        private static readonly Vector3 BodyCentre = new Vector3(0.3f, 0f, 0f);

        private readonly List<RemoteActorView> _views = new List<RemoteActorView>();
        private readonly Dictionary<RemoteActorView, Transform> _bodies = new Dictionary<RemoteActorView, Transform>();
        private readonly RaycastHit[] _hits = new RaycastHit[16];

        private FightingInput _input;
        private RemoteActorRegistry _registry;
        private ClientShotReporter _reporter;
        private Transform _look;
        private RemoteActorView _target;
        private float _nextRetarget;
        private float _nextStats;
        private bool _subscribed;
        private int _confirms;
        private int _heads;
        private int _kills;
        private float _damage;

        /// <summary>Whether it wants the trigger held this frame.</summary>
        internal bool Engaging { get; private set; }

        private void Update()
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            Engaging = false;
            if (client == null || !local.Exists || local.GameObject == null) return;

            Subscribe(client);
            if (!Install(local, client)) return;

            if (_target == null || !IsEnemyAlive(_target, local.Team) || Time.unscaledTime >= _nextRetarget)
            {
                _target = PickTarget(local);
                _nextRetarget = Time.unscaledTime + RetargetSeconds;
            }

            if (_target != null && TryBody(_target, out Vector3 body) && Sees(local, body))
            {
                _look.rotation = Quaternion.LookRotation(body - _look.position);
                Engaging = true;
            }

            if (Time.unscaledTime >= _nextStats)
            {
                _nextStats = Time.unscaledTime + StatsSeconds;
                Debug.Log($"[fight] target={(_target != null ? _target.ActorId : 0)} "
                          + $"struck={(_reporter != null ? _reporter.HitsSent : 0)} "
                          + $"reports={(_reporter != null ? _reporter.ReportsSent : 0)} "
                          + $"confirmed={_confirms} heads={_heads} kills={_kills} damage={_damage:F0}");
            }
        }

        private void OnDestroy()
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            if (_subscribed && client != null) client.Router.OnHitConfirm -= OnHitConfirm;
        }

        private void Subscribe(NetClientBootstrap client)
        {
            if (_subscribed) return;
            client.Router.OnHitConfirm += OnHitConfirm;
            _reporter = client.GetComponent<ClientShotReporter>();
            _subscribed = true;
        }

        private void OnHitConfirm(HitConfirmMessage message)
        {
            _confirms++;
            _damage += message.Damage;
            if (message.Headshot) _heads++;
            if (message.Killed) _kills++;
        }

        /// <summary>Wraps the player's own input once per body: the same controls, plus the trigger.</summary>
        private bool Install(ILocalPlayerRig local, NetClientBootstrap client)
        {
            if (_registry == null) _registry = client.GetComponent<RemoteActorRegistry>() ?? FindFirstObjectByType<RemoteActorRegistry>();
            if (_registry == null) return false;

            if (_input == null || !ReferenceEquals(local.InputSource, _input))
            {
                if (local.InputSource == null) return false;
                _input = new FightingInput(local.InputSource, this);
                local.SetInputSource(_input);
                _look = LookTransform();
                Debug.Log("[fight] armed: the camera follows the nearest enemy it can see, and the trigger follows the camera");
            }

            if (_look == null) _look = LookTransform();
            return _look != null;
        }

        /// <summary><c>FpsActorController.instance.fpCameraParent</c>, read by name: Assembly-CSharp is out of reach.</summary>
        private static Transform LookTransform()
        {
            Type controller = Type.GetType("FpsActorController, Assembly-CSharp");
            object instance = controller?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return instance != null
                ? controller.GetField("fpCameraParent", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as Transform
                : null;
        }

        private RemoteActorView PickTarget(ILocalPlayerRig local)
        {
            _registry.CopyLiveViews(_views);
            RemoteActorView best = null;
            float bestDistance = MaxRangeMetres;
            for (int i = 0; i < _views.Count; i++)
            {
                RemoteActorView view = _views[i];
                if (!IsEnemyAlive(view, local.Team) || !TryBody(view, out Vector3 body)) continue;
                float distance = Vector3.Distance(_look.position, body);
                if (distance >= bestDistance || !Sees(local, body)) continue;
                best = view;
                bestDistance = distance;
            }
            return best;
        }

        private static bool IsEnemyAlive(RemoteActorView view, int localTeam)
            => view != null && view.isActiveAndEnabled && view.HasState && view.IsAlive && view.Team != localTeam;

        private bool TryBody(RemoteActorView view, out Vector3 body)
        {
            if (!_bodies.TryGetValue(view, out Transform bone) || bone == null)
            {
                bone = FindDeep(view.transform, BodyBone);
                _bodies[view] = bone;
            }
            body = bone != null ? bone.TransformPoint(BodyCentre) : view.transform.position + Vector3.up * 1.2f;
            return true;
        }

        /// <summary>No wall between the eye and the point: the player's own body and vehicle aside.</summary>
        private bool Sees(ILocalPlayerRig local, Vector3 point)
        {
            Vector3 eye = _look.position;
            Vector3 toPoint = point - eye;
            float distance = toPoint.magnitude;
            if (distance < 0.5f) return true;
            int count = Physics.RaycastNonAlloc(eye, toPoint / distance, _hits, distance - 0.3f, -2049, QueryTriggerInteraction.Ignore);
            Transform self = local.GameObject.transform;
            for (int i = 0; i < count; i++)
                if (!_hits[i].collider.transform.IsChildOf(self)) return false;
            return true;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name) return child;
                Transform found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The player's own input with the trigger and the sights added while engaging.</summary>
        private sealed class FightingInput : IInputSource
        {
            private readonly IInputSource _own;
            private readonly CombatAutopilot _pilot;

            public FightingInput(IInputSource own, CombatAutopilot pilot)
            {
                _own = own;
                _pilot = pilot;
            }

            public float MoveX => _own.MoveX;
            public float MoveZ => _own.MoveZ;
            public float Yaw => _own.Yaw;
            public float Pitch => _own.Pitch;
            public float Lean => _own.Lean;
            public float LookDeltaX => _own.LookDeltaX;
            public float LookDeltaY => _own.LookDeltaY;

            public ushort Buttons
            {
                get
                {
                    ushort buttons = _own.Buttons;
                    if (_pilot == null || !_pilot.Engaging) return buttons;
                    buttons |= (ushort)InputButtons.Aim;
                    bool pulse = Mathf.Repeat(Time.unscaledTime * PulsesPerSecond, 1f) < 0.5f;
                    return pulse ? (ushort)(buttons | (ushort)InputButtons.Fire) : buttons;
                }
            }

            public float HeliYaw => _own.HeliYaw;
            public float HeliCollective => _own.HeliCollective;
            public float HeliRoll => _own.HeliRoll;
            public float HeliPitch => _own.HeliPitch;
            public bool RespawnPressed => _own.RespawnPressed;
            public bool SeatTogglePressed => _own.SeatTogglePressed;
        }
    }
}
#endif
