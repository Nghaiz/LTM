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
            index = -1;
            message = null;

            if (string.IsNullOrWhiteSpace(requested)) return Outcome.NotRequested;

            if (!int.TryParse(
                    requested.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed))
            {
                message = $"IRONFRONT_LANEB_SPAWN_INDEX='{requested}' is not an integer. "
                          + CoinFlipTail;
                return Outcome.Failed;
            }

            // Negative is a request nothing can satisfy, so it does not wait for a count.
            if (parsed < 0)
            {
                message = $"IRONFRONT_LANEB_SPAWN_INDEX={parsed} is negative. " + CoinFlipTail;
                return Outcome.Failed;
            }

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

            index = parsed;
            return Outcome.Pinned;
        }
    }
}
#endif
