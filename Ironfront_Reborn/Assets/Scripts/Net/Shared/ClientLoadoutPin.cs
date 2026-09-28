// #nullable disable, for the reason LaneBSpawnPin.cs states: this file is compiled twice, once
// by Unity (no nullable context) and once by Ironfront.Net.Replication.Tests through a
// <Compile Include> link (nullable warnings are errors). Annotating for the second emits CS8632
// in the first.
#nullable disable

using System;
using System.Collections.Generic;

namespace Ironfront.Net.Unity
{
    /// <summary>Which slot of the LOCAL player's chosen loadout a name is being asked for.</summary>
    public enum ClientLoadoutSlot
    {
        Primary,
        Secondary,
        Gear1,
    }

    /// <summary>
    /// Forces the local player's chosen loadout to named weapons, so a lane-B driver holds the
    /// weapon the run asked for instead of whatever the loadout screen last selected. Ledger
    /// <b>X-27</b>, second half.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a SECOND pin exists, when <c>PinnedLoadoutDirectory</c> already pins a loadout.</b>
    /// That one governs <c>AiActorController.GetLoadout</c>, and a claimed player body never
    /// reaches it: <c>Actor.SpawnLoadoutWeapons</c> reads
    /// <c>ResolveDeployLoadout() ?? controller.GetLoadout()</c>, and for a networked player the
    /// left operand is non-null — it is the five ids the client itself put in
    /// <c>SpawnRequestMessage</c>, stamped into <c>NetServerBindings</c> by
    /// <c>ServerCombatBridge.PlaceAtSpawn</c>. So the server-side directory pins BOTS, the
    /// client's own choice arms the DRIVER, and five runs asking for RK-44, SIGNAL DMR,
    /// SL-DEFENDER and BEU AW1 all deployed the identical <c>loadout 1/3/7/5/0</c> while the
    /// server log cheerfully reported the loadout pinned. Section 14 item 2 — hold the trigger
    /// on a SEMI-AUTOMATIC weapon — could not be graded at all, because no flag could put a
    /// semi-automatic weapon in the driver's hands.
    /// </para>
    /// <para>
    /// <b>Applied at the client's own <c>GetLoadout</c>, not at the wire.</b> Rewriting only the
    /// ids in the spawn request would arm the SERVER body with one weapon and leave the client
    /// rendering and predicting another — which is X-11, the disagreement the deploy handshake
    /// exists to close, reintroduced for exactly the runs that are being measured. One seam
    /// upstream of both readers keeps them agreeing.
    /// </para>
    /// <para>
    /// <b>An unresolvable name is LOUD and never silently swallowed.</b> A pin that quietly
    /// falls back is worse than no pin: the log says "pinned" while nothing is pinned, and every
    /// run taken under it is incomparable to every other without anyone being told. So an
    /// unmatched name keeps the drawn weapon — a body armed with the wrong gun still produces a
    /// readable artifact, whereas arming it with nothing is the unarmed-body defect (X-11) all
    /// over again — and <see cref="HasUnresolved"/> is set, <see cref="Describe"/> leads with
    /// <see cref="UnresolvedMarker"/>, and <c>run-lane-b.ps1</c> greps that marker and fails the
    /// run.
    /// </para>
    /// <para>
    /// <b>No UnityEngine here</b>, the same <c>&lt;Compile Include&gt;</c> arrangement
    /// <c>LaneBSpawnPin</c> uses: a <c>using UnityEngine;</c> would drop this out of
    /// <c>dotnet test</c>, which is the only coverage anything under <c>Assets/</c> gets. The
    /// name lookup is therefore a delegate the caller supplies rather than a direct
    /// <c>WeaponManager.EntryNamed</c> call, which is also what lets a test resolve names
    /// without a weapon catalogue.
    /// </para>
    /// </remarks>
    public sealed class ClientLoadoutPin
    {
        /// <summary>
        /// The prefix <see cref="Describe"/> leads with when a name did not resolve.
        /// <c>run-lane-b.ps1</c> greps every client log for this exact string, so changing it
        /// silently un-grades every future run — the marker and the gate move together.
        /// </summary>
        public const string UnresolvedMarker = "[lane-b] loadout pin UNRESOLVED";

        /// <summary>
        /// The prefix <see cref="Describe"/> leads with when every pinned name resolved. Also
        /// grepped: a run that asked for a weapon and produced NEITHER marker never reached the
        /// pin at all, which is its own failure and the one this whole row is about.
        /// </summary>
        public const string AppliedMarker = "[lane-b] loadout pin applied";

        /// <summary>
        /// The pin in force for this process, or <c>null</c> for the draw. Assigned by
        /// <c>LaneBHarness</c> on a lane-B client and by nothing else, so an ordinary build
        /// reads <c>null</c> here forever and behaves exactly as it did before this existed.
        /// </summary>
        /// <remarks>
        /// Cleared by <c>NetClientBindings.ResetOnLoad</c> for the reason that class states at
        /// length: a static survives a Play session when domain reload is off, and a pin left
        /// over from the previous run would arm the next one.
        /// </remarks>
        public static ClientLoadoutPin Active { get; set; }

        private readonly string _primary;
        private readonly string _secondary;
        private readonly string _gear1;

        private readonly List<string> _unresolved = new List<string>();
        private readonly List<string> _resolved = new List<string>();
        private bool _reported;

        /// <param name="primary">Name to force into the primary slot, or null/empty to defer.</param>
        /// <param name="secondary">Name to force into the secondary slot, or null/empty to defer.</param>
        /// <param name="gear1">Name to force into the first gear slot, or null/empty to defer.</param>
        /// <exception cref="ArgumentException">
        /// Every slot deferred. A pin that overrides nothing is indistinguishable from no pin,
        /// and installing one would read in a log as "the loadout is pinned" while pinning
        /// nothing — the same shape as the defect this class closes, and the same refusal
        /// <c>PinnedLoadoutDirectory</c> makes one assembly over.
        /// </exception>
        public ClientLoadoutPin(string primary, string secondary = null, string gear1 = null)
        {
            if (string.IsNullOrWhiteSpace(primary)
                && string.IsNullOrWhiteSpace(secondary)
                && string.IsNullOrWhiteSpace(gear1))
            {
                throw new ArgumentException(
                    "a ClientLoadoutPin that overrides no slot pins nothing and would still "
                    + "report itself as installed. Pass at least one name.",
                    nameof(primary));
            }

            _primary = Normalize(primary);
            _secondary = Normalize(secondary);
            _gear1 = Normalize(gear1);
        }

        /// <summary>The name forced into <paramref name="slot"/>, or null to keep the draw.</summary>
        public string NameFor(ClientLoadoutSlot slot)
        {
            switch (slot)
            {
                case ClientLoadoutSlot.Primary: return _primary;
                case ClientLoadoutSlot.Secondary: return _secondary;
                case ClientLoadoutSlot.Gear1: return _gear1;

                // A slot this pin has never heard of defers rather than throwing: a new
                // ClientLoadoutSlot value must not turn a pinned lane-B run into a crash on a
                // path the pin has no opinion about.
                default: return null;
            }
        }

        /// <summary>Whether any pinned name failed to resolve to a weapon.</summary>
        public bool HasUnresolved => _unresolved.Count > 0;

        /// <summary>
        /// The pinned weapon for <paramref name="slot"/>, or <paramref name="drawn"/> when this
        /// pin has no opinion about the slot or <paramref name="lookup"/> could not find the
        /// name.
        /// </summary>
        /// <remarks>
        /// Generic over the entry type so this file can stay free of <c>UnityEngine</c> and of
        /// <c>Assembly-CSharp</c>'s <c>WeaponEntry</c> — the same inversion
        /// <c>ILoadoutDirectory</c> is built around, one assembly further out. A
        /// <paramref name="lookup"/> that returns null is recorded as unresolved and reported
        /// ONCE by <see cref="Describe"/>; it does not throw, because a body holding the wrong
        /// gun still yields a readable run and a body holding nothing does not.
        /// </remarks>
        /// <param name="drawn">What the loadout screen selected. Returned on every deferral.</param>
        /// <param name="slot">Which slot is being armed.</param>
        /// <param name="lookup">Maps a weapon name to an entry, or null when no weapon has it.</param>
        public T PinnedOr<T>(T drawn, ClientLoadoutSlot slot, Func<string, T> lookup)
            where T : class
        {
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));

            string forced = NameFor(slot);
            if (forced == null) return drawn;

            T entry = lookup(forced);
            if (entry == null)
            {
                Record(_unresolved, $"{slot}='{forced}'");
                return drawn;
            }

            Record(_resolved, $"{slot}='{forced}'");
            return entry;
        }

        /// <summary>
        /// The single line describing what this pin did, or <c>null</c> before any slot has been
        /// asked for.
        /// </summary>
        /// <remarks>
        /// Leads with <see cref="UnresolvedMarker"/> when anything failed and
        /// <see cref="AppliedMarker"/> otherwise, because those two strings are what
        /// <c>run-lane-b.ps1</c> greps — a reader should not have to infer the outcome from
        /// <c>weaponId</c> in a checkpoint record after the fact, which is precisely what the
        /// old server-side pin left them to do.
        /// </remarks>
        public string Describe()
        {
            if (_resolved.Count == 0 && _unresolved.Count == 0) return null;

            string resolved = _resolved.Count > 0 ? string.Join(", ", _resolved) : "none";

            if (!HasUnresolved)
            {
                return $"{AppliedMarker} - {resolved} - the driver's own loadout now carries "
                       + "these, so the body it renders and the body the server arms hold the "
                       + "same weapons (X-27).";
            }

            return $"{UnresolvedMarker} - no weapon is named {string.Join(", ", _unresolved)} - "
                   + $"resolved: {resolved}. WeaponManager.EntryNamed matches the name EXACTLY, "
                   + "case and spacing included. The unmatched slot(s) kept the loadout screen's "
                   + "own draw, so this run is NOT the experiment it was asked for and is not "
                   + "comparable to any other (X-27).";
        }

        /// <summary>
        /// <see cref="Describe"/> exactly once per pin, so a caller on the respawn path logs the
        /// outcome without repeating it on every life.
        /// </summary>
        public bool TryTakeReport(out string line)
        {
            line = _reported ? null : Describe();
            if (line == null) return false;

            _reported = true;
            return true;
        }

        // Each slot is asked once per spawn and there is one life after another, so without this
        // the lists -- and the line built from them -- would grow for the length of the run.
        private static void Record(List<string> into, string entry)
        {
            if (!into.Contains(entry)) into.Add(entry);
        }

        private static string Normalize(string name)
            => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }
}
