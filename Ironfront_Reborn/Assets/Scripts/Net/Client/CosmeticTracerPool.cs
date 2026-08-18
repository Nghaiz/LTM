using System.Collections.Generic;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Draws the streak a bullet leaves, and nothing else. phase-V10 task 6, decision D10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is new work, not a wire-up.</b> The project has no tracer system: a scan for
    /// <c>Tracer</c>, <c>TrailRenderer</c> and <c>LineRenderer</c> across
    /// <c>Assets/Scripts/</c> finds only A* internals and a road-editor field. The visible
    /// streak in the original game <i>is</i> the <c>Projectile</c> — which the cosmetic path is
    /// forbidden to spawn, because <c>Weapon.SpawnProjectile</c> sets <c>source = user</c> and
    /// would do real damage from a client.
    /// </para>
    /// <para>
    /// <b>So this deliberately carries no collider, no <c>Projectile</c> component and no
    /// source.</b> One file, so "can this tracer hurt anybody" has exactly one place to check,
    /// and the answer is visible in the type's whole surface.
    /// </para>
    /// <para>
    /// Pre-warmed and pooled. Tracers arrive at the rate of every visible player's trigger
    /// finger, which during a firefight is the busiest allocation opportunity in the frame.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CosmeticTracerPool : MonoBehaviour
    {
        [Tooltip("A streak that reads as a bullet. No collider, no Projectile, no source. Client-track item E4.")]
        [SerializeField] private GameObject _tracerPrefab;

        [Tooltip("Pre-warmed streaks. Above this, a fire event draws nothing rather than allocating.")]
        [SerializeField] private int _prewarm = 32;

        [Tooltip("Seconds one streak stays visible.")]
        [SerializeField] private float _lifetimeSeconds = 0.08f;

        [Tooltip("Metres the streak is drawn along the fire direction.")]
        [SerializeField] private float _lengthMetres = 40f;

        private readonly Stack<Transform> _pool = new Stack<Transform>();
        private readonly List<Transform> _active = new List<Transform>();
        private readonly List<float> _expiresAt = new List<float>();

        /// <summary>Streaks currently drawn.</summary>
        public int ActiveCount => _active.Count;

        /// <summary>Streaks held ready.</summary>
        public int PooledCount => _pool.Count;

        private void Awake()
        {
            if (_tracerPrefab == null)
            {
                NetClientPresenterGuard.WarnOnce(
                    "no-tracer-prefab",
                    "[net] CosmeticTracerPool has no tracer prefab, so remote shots will show a "
                    + "flash and a report but no streak. Client-track item E4 -- this asset does "
                    + "not exist in the project yet and has to be authored.");
                enabled = false;
                return;
            }

            for (int i = 0; i < _prewarm; i++) _pool.Push(NewPooled());
        }

        /// <summary>
        /// Draws one streak from <paramref name="origin"/> along <paramref name="direction"/>.
        /// Silently draws nothing when the pool is empty — a missing streak during a firefight
        /// is cheaper than a hitch, and the flash and report still land.
        /// </summary>
        public void Fire(Vector3 origin, Vector3 direction)
        {
            if (!enabled || _pool.Count == 0) return;
            if (direction.sqrMagnitude < 0.0001f) return;

            Transform t = _pool.Pop();
            t.position = origin;
            t.rotation = Quaternion.LookRotation(direction);
            t.localScale = new Vector3(t.localScale.x, t.localScale.y, _lengthMetres);
            t.gameObject.SetActive(true);

            _active.Add(t);
            _expiresAt.Add(Time.time + _lifetimeSeconds);
        }

        private void Update()
        {
            if (_active.Count == 0) return;

            float now = Time.time;

            // Backwards, so a removal does not shift an index this loop has yet to reach.
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (now < _expiresAt[i]) continue;

                Transform t = _active[i];
                _active.RemoveAt(i);
                _expiresAt.RemoveAt(i);

                t.gameObject.SetActive(false);
                _pool.Push(t);
            }
        }

        private Transform NewPooled()
        {
            GameObject go = Instantiate(_tracerPrefab, transform);
            go.SetActive(false);
            return go.transform;
        }
    }
}
