// Diagnostics are compiled OUT of a shipping client build. Sense inverted for the reason
// ScriptedAim.cs states at length: extraScriptingDefines can only ADD a symbol.
#if !IRONFRONT_NO_DIAGNOSTICS
// #nullable disable, for the reason ScriptedInputProgramme.cs states: this file is compiled
// twice, once by Unity's Assembly-CSharp (no nullable context) and once by
// Ironfront.Net.Replication.Tests through a <Compile Include> link (nullable warnings are
// errors). Annotating for the second emits CS8632 in the first.
#nullable disable

using System;
using System.Globalization;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Decides whether the lane-B spawn pin can be installed yet, from the requested index and
    /// what the spawn directory currently reports. Ledger <b>X-22</b>, second half.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why "yet" is the whole problem.</b> X-22's fix installed the pin from
    /// <c>LaneBHarness.OnSceneLoaded</c> and validated the index against
    /// <c>ISpawnPointDirectory.Count</c> there. The directory answers that count out of
    /// <c>ActorManager.instance.spawnPoints</c>, and that array is filled by
    /// <c>ActorManager.StartGame()</c> — reached from <c>GameManager.OnLevelLoaded</c>, another
    /// subscriber to the SAME <c>sceneLoaded</c> event. So the harness asked before the array
    /// existed, read <c>0</c>, and rejected every index with "outside the scene's 0 spawn
    /// point(s)" while the scene had six. Both runs that claimed a pinned spawn
    /// (<c>x20-occlusion-01</c>, <c>x25-torso-aim-01</c>) carry that line and placed their
    /// actors on points 2/2/1 and 5/2/0 — the coin flip X-22 was closed for.
    /// </para>
    /// <para>
    /// <b>So a count of zero is a RETRY, not an answer</b> — until the deadline, which is the
    /// "server ready" line. Nothing can join before it, so nothing can spawn before it, so a
    /// pin installed by then is installed in time. A count of zero AT the deadline is a real
    /// failure and says so.
    /// </para>
    /// <para>
    /// <b>An index outside a non-empty directory is an answer immediately.</b> Retrying that
    /// would turn a typo into a silent unpinned run reported only at the deadline, which is the
    /// same class of quiet as the bug above.
    /// </para>
    /// <para>
    /// <b>No UnityEngine here</b>, same <c>&lt;Compile Include&gt;</c> arrangement as
    /// <see cref="ScriptedAim"/>: a <c>using UnityEngine;</c> would drop this out of
    /// <c>dotnet test</c>, which is the only coverage anything under <c>Assets/</c> gets.
    /// </para>
    /// </remarks>
    public static class LaneBSpawnPin
    {
        /// <summary>What the caller should do about the pin this frame.</summary>
        public enum Outcome
        {
            /// <summary>No index was asked for. Selection is left alone.</summary>
            NotRequested,

            /// <summary>Install the pin at the returned index.</summary>
            Pinned,

            /// <summary>Not yet knowable. Ask again next frame; say nothing.</summary>
            Retry,

            /// <summary>Cannot be pinned. Report <c>message</c> once and run unpinned.</summary>
            Failed,
        }

        /// <summary>The tail every failure carries, so an artifact names the row.</summary>
        public const string CoinFlipTail =
            "The spawn is NOT pinned and this run is a coin flip again (X-22).";

        /// <summary>
        /// Decides the pin from the requested value and the directory's current state.
        /// </summary>
        /// <param name="requested">Raw <c>IRONFRONT_LANEB_SPAWN_INDEX</c>, as read.</param>
        /// <param name="directoryInstalled">Whether an <c>ISpawnPointDirectory</c> exists yet.</param>
        /// <param name="directoryCount">What that directory reports RIGHT NOW.</param>
        /// <param name="final">
        /// True on the last attempt — the frame the server announces its slots, after which a
        /// client can join and spawn. A <see cref="Outcome.Retry"/> is impossible here.
        /// </param>
        /// <param name="index">The validated index, on <see cref="Outcome.Pinned"/> only.</param>
        /// <param name="message">The line to log, on <see cref="Outcome.Failed"/> only.</param>
        public static Outcome Evaluate(
            string requested,
            bool directoryInstalled,
            int directoryCount,
            bool final,
            out int index,
            out string message)
        {
            Outcome outcome = EvaluatePerTeam(
                requested, directoryInstalled, directoryCount, final, out int[] slots, out message);

            index = slots != null && slots.Length > 0 ? slots[0] : -1;
            return outcome;
        }

        /// <summary>
        /// The per-team form. <c>"3"</c> pins slot 3 for every team; <c>"3,7"</c> pins 3 for
        /// team 0 and 7 for team 1.
        /// </summary>
        /// <remarks>
        /// <b>Ledger X-63.</b> One slot for both teams cannot work on a map whose every spawn
        /// point is team-owned: <c>ChooseSpawnIndex</c> returns -1 for the team that does not
        /// own it and that actor is never placed, so the option stopped pinning runs and started
        /// voiding them. The single-value form is kept — it is still correct on a map with
        /// neutral spawn points, and <c>PinnedSpawnPointDirectory</c> refuses at construction
        /// when it is not.
        /// </remarks>
        public static Outcome EvaluatePerTeam(
            string requested,
            bool directoryInstalled,
            int directoryCount,
            bool final,
            out int[] indices,
            out string message)
        {
            indices = null;
            message = null;

            if (string.IsNullOrWhiteSpace(requested)) return Outcome.NotRequested;

            string[] parts = requested.Split(',');
            var parsedSlots = new int[parts.Length == 1 ? 2 : parts.Length];
            int parsed = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(
                        parts[i].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int slot))
                {
                    message = $"IRONFRONT_LANEB_SPAWN_INDEX='{requested}' is not an integer, or "
                              + "a comma-separated integer per team. " + CoinFlipTail;
                    return Outcome.Failed;
                }

                // Negative is a request nothing can satisfy, so it does not wait for a count.
                if (slot < 0)
                {
                    message = $"IRONFRONT_LANEB_SPAWN_INDEX={slot} is negative. " + CoinFlipTail;
                    return Outcome.Failed;
                }

                parsedSlots[i] = slot;
                if (slot > parsed) parsed = slot;
            }

            // One value means "the same slot for every team", which is what it has always meant.
            if (parts.Length == 1) parsedSlots[1] = parsedSlots[0];

            if (!directoryInstalled)
            {
                if (!final) return Outcome.Retry;

                message = $"IRONFRONT_LANEB_SPAWN_INDEX={parsed} but no ISpawnPointDirectory is "
                          + "installed by the time the server announces its slots, so there is "
                          + "nothing to pin. A scene with no ActorManager installs none. "
                          + CoinFlipTail;
                return Outcome.Failed;
            }

            if (directoryCount <= 0)
            {
                if (!final) return Outcome.Retry;

                message = $"IRONFRONT_LANEB_SPAWN_INDEX={parsed} but the spawn directory still "
                          + "reports 0 points at the ready line — ActorManager.StartGame() never "
                          + "filled the array. " + CoinFlipTail;
                return Outcome.Failed;
            }

            if (parsed >= directoryCount)
            {
                message = $"IRONFRONT_LANEB_SPAWN_INDEX={parsed} is outside the scene's "
                          + $"{directoryCount} spawn point(s). " + CoinFlipTail;
                return Outcome.Failed;
            }

            indices = parsedSlots;
            return Outcome.Pinned;
        }

        /// <summary>
        /// The rotation form. <c>"3"</c> pins slot 3 for every team, as a one-element rotation;
        /// <c>"3,7"</c> pins 3 for team 0 and 7 for team 1, each a one-element rotation;
        /// <c>"3|4|5,7|8"</c> rotates team 0 through 3, 4, 5 and team 1 through 7, 8 — one slot
        /// per placement, in order. Ledger <b>X-28</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A rotation, not a wider pin.</b> A single shared slot puts every same-team player
        /// on the exact same point, which is how three same-team clients end up in each other's
        /// fire before any check that names an ENEMY has a chance to matter. Comma still
        /// separates teams and now pipe separates that team's ordered slots — a new separator
        /// rather than overloading comma, so the existing single-value and two-value forms keep
        /// meaning exactly what they have always meant (one-element rotations).
        /// </para>
        /// <para>
        /// <b>A separate method from <see cref="EvaluatePerTeam"/>, deliberately.</b> That
        /// method's <c>out int[] indices</c> shape is exercised by
        /// <c>LaneBSpawnPinTests</c> (a project this diagnostics folder does not own) and is
        /// still what <see cref="Evaluate"/> is built from; changing its shape to carry a
        /// rotation would have broken that suite's compile for no behavioural gain. This method
        /// carries the new shape (<c>int[][]</c>) instead of widening the old one.
        /// </para>
        /// <para>
        /// Retry/failure semantics are identical to <see cref="EvaluatePerTeam"/> — see that
        /// method's own remark for why a directory that has not filled its spawn-point array yet
        /// is a RETRY rather than a FAILED, up to the ready line.
        /// </para>
        /// </remarks>
        /// <param name="requested">Raw <c>IRONFRONT_LANEB_SPAWN_INDEX</c>, as read.</param>
        /// <param name="directoryInstalled">Whether an <c>ISpawnPointDirectory</c> exists yet.</param>
        /// <param name="directoryCount">What that directory reports RIGHT NOW.</param>
        /// <param name="final">
        /// True on the last attempt — the frame the server announces its slots, after which a
        /// client can join and spawn. A <see cref="Outcome.Retry"/> is impossible here.
        /// </param>
        /// <param name="rotationsByTeam">
        /// One ordered rotation of slots per team, on <see cref="Outcome.Pinned"/> only.
        /// </param>
        /// <param name="message">The line to log, on <see cref="Outcome.Failed"/> only.</param>
        public static Outcome EvaluateRotationsPerTeam(
            string requested,
            bool directoryInstalled,
            int directoryCount,
            bool final,
            out int[][] rotationsByTeam,
            out string message)
        {
            rotationsByTeam = null;
            message = null;

            if (string.IsNullOrWhiteSpace(requested)) return Outcome.NotRequested;

            string[] teamParts = requested.Split(',');
            var parsedTeams = new int[teamParts.Length == 1 ? 2 : teamParts.Length][];
            int maxSlot = -1;

            for (int team = 0; team < teamParts.Length; team++)
            {
                string[] slotParts = teamParts[team].Split('|');
                var slots = new int[slotParts.Length];

                for (int s = 0; s < slotParts.Length; s++)
                {
                    if (!int.TryParse(
                            slotParts[s].Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int slot))
                    {
                        message = $"IRONFRONT_LANEB_SPAWN_INDEX='{requested}' is not an integer, "
                                  + "or a '|'-separated rotation, per comma-separated team. "
                                  + CoinFlipTail;
                        return Outcome.Failed;
                    }

                    // Negative is a request nothing can satisfy, so it does not wait for a count.
                    if (slot < 0)
                    {
                        message = $"IRONFRONT_LANEB_SPAWN_INDEX={slot} is negative. " + CoinFlipTail;
                        return Outcome.Failed;
                    }

                    slots[s] = slot;
                    if (slot > maxSlot) maxSlot = slot;
                }

                parsedTeams[team] = slots;
            }

            // One team's worth of rotation means "the same rotation for every team", the same
            // meaning a single value has always carried.
            if (teamParts.Length == 1) parsedTeams[1] = (int[])parsedTeams[0].Clone();

            if (!directoryInstalled)
            {
                if (!final) return Outcome.Retry;

                message = $"IRONFRONT_LANEB_SPAWN_INDEX={maxSlot} but no ISpawnPointDirectory is "
                          + "installed by the time the server announces its slots, so there is "
                          + "nothing to pin. A scene with no ActorManager installs none. "
                          + CoinFlipTail;
                return Outcome.Failed;
            }

            if (directoryCount <= 0)
            {
                if (!final) return Outcome.Retry;

                message = $"IRONFRONT_LANEB_SPAWN_INDEX={maxSlot} but the spawn directory still "
                          + "reports 0 points at the ready line — ActorManager.StartGame() never "
                          + "filled the array. " + CoinFlipTail;
                return Outcome.Failed;
            }

            if (maxSlot >= directoryCount)
            {
                message = $"IRONFRONT_LANEB_SPAWN_INDEX={maxSlot} is outside the scene's "
                          + $"{directoryCount} spawn point(s). " + CoinFlipTail;
                return Outcome.Failed;
            }

            rotationsByTeam = parsedTeams;
            return Outcome.Pinned;
        }
    }
}
#endif
