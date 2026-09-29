using System.Collections.Generic;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Owns every <see cref="RemoteCorpse"/> on this client: spawns them from a death, bleeds them,
    /// lets blasts throw them about, and sinks each out of sight when its time is up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-09-29</b>: a body shot dead just disappeared -- no ragdoll to speak of,
    /// no blood -- and hard hits short of a kill looked as bare. A death here leaves a body that
    /// falls with the momentum it had, the impulse of the shot on the bone that was hit, and the
    /// pose it died in sagging over half a second (<see cref="RemoteRagdoll.TickCrumple"/>); the
    /// wound sprays in the shot's direction, drips for a moment, and leaves a pool where the body
    /// comes to rest; the weapon falls out of the hand; a grenade that lands beside it later rolls
    /// it over. It stays <see cref="LingerSeconds"/>, then goes down through the ground.
    /// </para>
    /// <para>
    /// <b>Bounded.</b> At most <see cref="MaxCorpses"/> lie at once; the oldest sinks early to make
    /// room, so a long match costs a fixed number of rigidbodies, not one set per death.
    /// </para>
    /// <para>
    /// Added at runtime by <see cref="NetClientCombatPresenter"/>, which owns the death path, so the
    /// map scenes need no new component.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    internal sealed class RemoteCorpseDirector : MonoBehaviour
    {
        /// <summary>The most bodies that lie on the field at once.</summary>
        public const int MaxCorpses = 24;

        /// <summary>How long a body lies before it sinks.</summary>
        public const float LingerSeconds = 30f;

        /// <summary>How long the sink takes, and how deep it goes.</summary>
        public const float SinkSeconds = 4f;

        public const float SinkMetres = 1.4f;

        /// <summary>How long the death pose takes to give way.</summary>
        public const float CrumpleSeconds = 0.55f;

        /// <summary>The wound's drip: how long, and how often.</summary>
        public const float BleedSeconds = 2.5f;

        public const float DripEverySeconds = 0.3f;

        /// <summary>A body that is still moving gets its pool no earlier than this after the death.</summary>
        public const float PoolAfterSeconds = 1.5f;

        /// <summary>Drops thrown by a killing shot; a headshot throws more.</summary>
        public const int DeathSprayDrops = 10;

        public const int HeadshotExtraDrops = 6;

        /// <summary>
        /// A blast's shove on a body, as the speed it gives one at the centre, per metre of blast
        /// radius: about 8 m/s for a grenade's 10 m, falling off to nothing at the edge.
        /// </summary>
        public const float BlastSpeedPerMetre = 0.8f;

        /// <summary>A blast reaches bodies this far beyond its damage radius.</summary>
        public const float BlastReachScale = 1.6f;

        /// <summary>How long a blast is remembered for a body that falls just after it.</summary>
        public const float BlastMemorySeconds = 0.6f;

        private struct BlastMemory
        {
            public Vector3 Centre;
            public float Radius;
            public float At;
        }

        private readonly List<RemoteCorpse> _corpses = new List<RemoteCorpse>(MaxCorpses + 8);
        private readonly List<BlastMemory> _blasts = new List<BlastMemory>(8);

        /// <summary>The director of the running match, or null with none.</summary>
        public static RemoteCorpseDirector Current { get; private set; }

        /// <summary>Bodies lying or sinking right now.</summary>
        public int Count => _corpses.Count;

        private void Awake()
        {
            Current = this;
        }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            for (int i = 0; i < _corpses.Count; i++) _corpses[i].Destroy();
            _corpses.Clear();
        }

        /// <summary>
        /// Leaves a corpse where <paramref name="view"/>'s body stands, thrown by
        /// <paramref name="force"/> into <paramref name="hit"/>. False when the body cannot be copied.
        /// </summary>
        public bool TrySpawn(RemoteActorView view, Vector3 force, HumanBodyBones hit)
        {
            if (view == null) return false;

            RemoteCorpse corpse = RemoteCorpse.TryCreate(view.BodyAnimator, view.ActorId, view.Team, Time.time);
            if (corpse == null) return false;

            corpse.Fell(force, hit, view.PlanarVelocity, CrumpleSeconds);
            if (TryRecentBlast(corpse.ChestPosition, out Vector3 centre, out float radius))
            {
                corpse.ThrowByBlast(BlastSpeedPerMetre * radius, centre, radius * BlastReachScale);
            }
            SprayBlood(corpse, force, hit);

            _corpses.Add(corpse);
            KeepUnderCap();
            return true;
        }

        /// <summary>
        /// Lands a late <c>S_DEATH</c>'s impulse on the corpse a snapshot already left for
        /// <paramref name="actorId"/>. A no-op when there is none from the last moment.
        /// </summary>
        public void Kick(ushort actorId, Vector3 force, HumanBodyBones hit)
        {
            float now = Time.time;
            for (int i = _corpses.Count - 1; i >= 0; i--)
            {
                RemoteCorpse corpse = _corpses[i];
                if (corpse.ActorId != actorId || now - corpse.DiedAt > 2f) continue;
                corpse.Push(force, hit);
                SprayBlood(corpse, force, hit);
                return;
            }
        }

        /// <summary>
        /// A blast went off: every body within reach is thrown, and the blast is remembered for a
        /// body that falls just after it (a bot knocked over by it, or killed by it).
        /// </summary>
        public void Explode(Vector3 centre, float radius)
        {
            if (radius <= 0f) return;
            _blasts.Add(new BlastMemory { Centre = centre, Radius = radius, At = Time.time });

            float reach = radius * BlastReachScale;
            float force = BlastSpeedPerMetre * radius;
            for (int i = 0; i < _corpses.Count; i++)
            {
                RemoteCorpse corpse = _corpses[i];
                if (corpse.IsGone) continue;
                if ((corpse.ChestPosition - centre).sqrMagnitude > reach * reach) continue;
                corpse.ThrowByBlast(force, centre, reach);
            }
        }

        /// <summary>
        /// The newest blast of the last moment that reaches <paramref name="position"/>, for a body
        /// that falls in it: its centre, and its reach.
        /// </summary>
        public bool TryRecentBlast(Vector3 position, out Vector3 centre, out float radius)
        {
            float now = Time.time;
            for (int i = _blasts.Count - 1; i >= 0; i--)
            {
                BlastMemory blast = _blasts[i];
                if (now - blast.At > BlastMemorySeconds)
                {
                    _blasts.RemoveAt(i);
                    continue;
                }
                float reach = blast.Radius * BlastReachScale;
                if ((position - blast.Centre).sqrMagnitude > reach * reach) continue;
                centre = blast.Centre;
                radius = blast.Radius;
                return true;
            }
            centre = Vector3.zero;
            radius = 0f;
            return false;
        }

        private void Update()
        {
            float now = Time.time;
            float dt = Time.deltaTime;
            for (int i = _corpses.Count - 1; i >= 0; i--)
            {
                RemoteCorpse corpse = _corpses[i];
                if (corpse.IsGone)
                {
                    _corpses.RemoveAt(i);
                    continue;
                }

                float age = now - corpse.DiedAt;
                if (corpse.SinkStartedAt < 0f)
                {
                    corpse.TickCrumple(age);
                    Bleed(corpse, age, now);
                    if (age >= LingerSeconds || corpse.Evicted) corpse.BeginSink(now);
                    continue;
                }

                corpse.SinkBy(SinkMetres / SinkSeconds * dt);
                if (now - corpse.SinkStartedAt >= SinkSeconds)
                {
                    corpse.Destroy();
                    _corpses.RemoveAt(i);
                }
            }
        }

        // The oldest bodies sink early to keep MaxCorpses on the field; if deaths come faster than
        // they can sink, the oldest sinking ones go at once.
        private void KeepUnderCap()
        {
            int lying = 0;
            for (int i = 0; i < _corpses.Count; i++)
            {
                if (!_corpses[i].Evicted && _corpses[i].SinkStartedAt < 0f) lying++;
            }
            for (int i = 0; i < _corpses.Count && lying > MaxCorpses; i++)
            {
                if (_corpses[i].Evicted || _corpses[i].SinkStartedAt >= 0f) continue;
                _corpses[i].Evicted = true;
                lying--;
            }
            while (_corpses.Count > MaxCorpses + 8)
            {
                _corpses[0].Destroy();
                _corpses.RemoveAt(0);
            }
        }

        // The killing shot's spray leaves the wound the way the shot went: out of the far side. A
        // death a snapshot reported without S_DEATH has no direction, so it wells up instead.
        private static void SprayBlood(RemoteCorpse corpse, Vector3 force, HumanBodyBones hit)
        {
            IDecalSink decals = NetClientBindings.Decals;
            if (decals == null) return;

            bool headshot = hit == HumanBodyBones.Head;
            Vector3 point = headshot ? corpse.HeadPosition : corpse.ChestPosition;
            Vector3 velocity = force.sqrMagnitude > 1e-4f
                ? force.normalized * Mathf.Clamp(force.magnitude / 15f, 2f, 6f)
                : Vector3.up * 1.5f;
            decals.AddBlood(point, velocity, corpse.Team, DeathSprayDrops + (headshot ? HeadshotExtraDrops : 0));
        }

        // The wound drips for a moment after the death, and once the body is at rest (or has had
        // time to be) a pool spreads under the chest.
        private static void Bleed(RemoteCorpse corpse, float age, float now)
        {
            IDecalSink decals = NetClientBindings.Decals;
            if (decals == null) return;

            if (age < BleedSeconds && now - corpse.LastDripAt >= DripEverySeconds)
            {
                corpse.LastDripAt = now;
                decals.AddBlood(corpse.ChestPosition, Vector3.down * 0.5f, corpse.Team, 1);
            }

            if (corpse.Pooled || age < PoolAfterSeconds) return;
            if (!corpse.IsResting && age < PoolAfterSeconds * 2f) return;

            corpse.Pooled = true;
            // Default layer only: the ground and the props, never the corpse's own Ragdoll layer.
            if (Physics.Raycast(corpse.ChestPosition + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f, 1,
                    QueryTriggerInteraction.Ignore))
            {
                decals.AddBloodPool(hit.point + hit.normal * 0.02f, hit.normal, Random.Range(1.2f, 1.8f));
            }
        }
    }
}
