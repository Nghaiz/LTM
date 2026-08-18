using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Match;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Copies the authoritative capture-point state onto the scene components every tick, so
    /// that <c>SpawnPoint.owner</c> — which decides where everybody respawns — is the same value
    /// the netcode is broadcasting. Phase-V8 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the class that closes the reported bug.</b> Two capture systems were running
    /// at once: <see cref="CapturePointState"/>, which is replicated and which nothing in the
    /// scene read, and the scene's own 1 Hz <c>CapturePoint.UpdateOwner</c>, which nothing
    /// replicated and which <c>ActorManager.RandomSpawnPointForTeam</c> and
    /// <c>ServerCombatBridge.MoveToSpawnPoint</c> both selected from. They disagreed on rate, on
    /// threshold and on headcount rules, so the flag a player saw and the flag they spawned
    /// behind were decided by different arithmetic.
    /// </para>
    /// <para>
    /// <b>Lockstep by index, not by lookup.</b> The state array and the directory are bound from
    /// the same ordered source and their index IS the wire id, so this walks both with one
    /// counter. A dictionary keyed on point id would allocate and would let the two drift.
    /// </para>
    /// <para>
    /// <b>Presence is refreshed at a divider, ownership is not.</b> Ownership is one field
    /// assignment and must be exact on the tick it flips — a respawn landing one tick after a
    /// capture must use the new owner. The contested-spawn safe flags feed spawn <i>selection</i>,
    /// which happens at most once per death, so recomputing roughly 1900 dot products a second
    /// on a five-point map at <see cref="ContestedRefreshTicks"/> is indistinguishable from
    /// per-tick at the timescale a player can die.
    /// </para>
    /// </remarks>
    public sealed class CapturePointSlave
    {
        /// <summary>
        /// Ticks between contested-presence refreshes. Six at a 30 Hz loop is 5 Hz.
        /// </summary>
        /// <remarks>A named constant rather than a literal, so moving it is one edit and a diff
        /// that says what changed.</remarks>
        public const int ContestedRefreshTicks = 6;

        private readonly ICapturePointDirectory _directory;

        // Sized once at construction. The contested flag is only recomputed on refresh ticks,
        // so the value from the last refresh has to survive the ticks in between -- caching it
        // here rather than on the component keeps the component's fields to a single writer.
        private readonly bool[] _contested;

        private int _tick;

        public CapturePointSlave(ICapturePointDirectory directory, int pointCount)
        {
            if (pointCount < 0) throw new ArgumentOutOfRangeException(nameof(pointCount));

            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _contested = new bool[pointCount];
        }

        /// <summary>
        /// Pushes one tick of authoritative state onto the scene.
        /// </summary>
        /// <remarks>
        /// Allocation-free. Called from <c>MatchController.FixedUpdate</c> after the match has
        /// ticked and before the broadcasts, so the value written to the scene and the value put
        /// on the wire are the same one.
        /// </remarks>
        public void Apply(IReadOnlyList<CapturePointState> states, ReadOnlySpan<ActorPresence> actors)
        {
            if (states == null) return;

            bool refresh = _tick % ContestedRefreshTicks == 0;
            _tick++;

            int count = states.Count < _contested.Length ? states.Count : _contested.Length;
            for (int i = 0; i < count; i++)
            {
                CapturePointState state = states[i];
                if (state == null) continue;

                if (refresh) _contested[i] = _directory.RefreshPresence(i, actors);

                _directory.ApplyAuthoritativeOwner(
                    i,
                    CapturePointOwnership.ToSpawnPointOwner(state.OwningTeam),
                    CapturePointOwnership.ToControl(state.Owner),
                    _contested[i]);
            }
        }

        /// <summary>Rewinds the refresh divider, so a new round starts on a refresh tick.</summary>
        public void Reset()
        {
            _tick = 0;
            for (int i = 0; i < _contested.Length; i++) _contested[i] = false;
        }
    }
}
