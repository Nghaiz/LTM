// Diagnostics are compiled OUT of a shipping client build. Sense inverted for the reason
// ScriptedAim.cs states at length: extraScriptingDefines can only ADD a symbol.
#if !IRONFRONT_NO_DIAGNOSTICS
#nullable disable

using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Every authoritative explosion this client was sent, in order. Phase-3D lane B, check 4
    /// (E10 — "grenade detonates at the same place on both clients").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this can grade, and what it deliberately cannot.</b> Both observers decode the
    /// SAME <c>S_EXPLOSION</c> through the same <c>Quantize.UnpackPos</c>, so their POSITIONS
    /// agree by construction — a check that only compared them could never go red, which is the
    /// failure `green-that-proves-nothing.md` is about. What can differ, and what this exists to
    /// catch, is <b>receipt</b>: interest management culling the blast for one client, a client
    /// whose presenter never instantiated, a blast the server never emitted at all. Those are
    /// real and this sees them.
    /// </para>
    /// <para>
    /// <b>The half it does NOT reach is the thrower's drawn position.</b>
    /// <c>ClientCombatEvents.PredictExplosion</c> (called from <c>ActorManager</c>) draws the
    /// blast locally from the client's own physics, and
    /// <c>NetClientExplosionPresenter.OnExplosion</c> then SUPPRESSES the server's confirmation
    /// so the flash is not doubled. Whether the predicted centre matched the authoritative one
    /// is therefore visible only inside the presenter, which exposes neither the drawn position
    /// nor the suppressor's verdict. Grading that needs a read-only accessor on shipped client
    /// code — phase-3d §6 — so check 4 is a PARTIAL until someone takes that decision. Ledger
    /// X-29.
    /// </para>
    /// <para>
    /// <b>Subscribed here rather than read off the presenter</b>, for two reasons: the presenter
    /// drops suppressed messages, so it is not a record of what ARRIVED; and adding a field to
    /// it would be a change to shipped client behaviour, which this phase does not make. The
    /// router event is already public and reading it changes nothing.
    /// </para>
    /// </remarks>
    public sealed class LaneBExplosionLog
    {
        /// <summary>One authoritative blast, as the wire delivered it.</summary>
        public readonly struct Entry
        {
            public readonly float Seconds;
            public readonly ushort SourceActorId;
            public readonly float X, Y, Z;
            public readonly float RadiusMetres;
            public readonly ExplosionKind Kind;

            internal Entry(float seconds, in ExplosionMessage message)
            {
                Seconds = seconds;
                SourceActorId = message.SourceActorId;
                X = Quantize.UnpackPos(message.PosX);
                Y = Quantize.UnpackPos(message.PosY);
                Z = Quantize.UnpackPos(message.PosZ);
                RadiusMetres = ExplosionEncoding.UnpackRadiusMetres(message.RadiusMetres);
                Kind = message.Kind;
            }
        }

        // Bounded. A run that somehow produces thousands of blasts must not turn the artifact
        // into a file nobody opens, and check 4 needs the FIRST few rather than the last few --
        // so this keeps the earliest and counts the rest, instead of a ring buffer that would
        // silently discard the grenade the check is about.
        private const int Capacity = 32;

        private readonly List<Entry> _entries = new List<Entry>(Capacity);
        private ClientMessageRouter _router;

        /// <summary>Every blast received, oldest first, capped at 32.</summary>
        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>How many arrived in total, including any past the cap.</summary>
        public int TotalReceived { get; private set; }

        /// <summary>Whether this log is actually listening. False means it is recording nothing.</summary>
        /// <remarks>
        /// Serialised beside the entries on purpose: an empty list from a log that never attached
        /// reads identically to an empty list from a run where nothing exploded, and those are
        /// opposite verdicts for check 4.
        /// </remarks>
        public bool Attached => _router != null;

        /// <summary>
        /// Starts listening on <paramref name="router"/>. Idempotent — attaching twice to the
        /// same router is a no-op rather than a double subscription.
        /// </summary>
        public void Attach(ClientMessageRouter router)
        {
            if (router == null || ReferenceEquals(router, _router)) return;

            Detach();
            _router = router;
            _router.OnExplosion += Record;
        }

        /// <summary>Stops listening. Safe to call when never attached.</summary>
        public void Detach()
        {
            if (_router == null) return;

            _router.OnExplosion -= Record;
            _router = null;
        }

        private void Record(ExplosionMessage message)
        {
            TotalReceived++;
            if (_entries.Count >= Capacity) return;

            _entries.Add(new Entry(Time.time, in message));
        }
    }
}
#endif
