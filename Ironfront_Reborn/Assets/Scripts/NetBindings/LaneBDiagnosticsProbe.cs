using System.Collections.Generic;
using Ironfront.Net.Unity.Diagnostics;
using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="IDiagnosticsProbe"/>: the scoreboard HUD,
    /// the offline scoreboard and the scene's capture points, read for the lane-B recorder's
    /// JSON. Phase C4d.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every read here is a copy-out.</b> Nothing hands a component across the seam, so an
    /// observer cannot write to the thing it is measuring — see <see cref="IDiagnosticsProbe"/>.
    /// </para>
    /// <para>
    /// <b>The scan and the sort moved together, deliberately.</b> They were one statement in the
    /// recorder and splitting them would have let the ordering drift from the scan that produced
    /// it — and two clients' arrays are diffed index for index, so a drifted order reports a flag
    /// flip that never happened.
    /// </para>
    /// </remarks>
    internal sealed class LaneBDiagnosticsProbe : IDiagnosticsProbe
    {
        /// <inheritdoc/>
        public bool TryReadHud(out HudReading hud)
        {
            hud = default;

            ScoreUi ui = ScoreUi.instance;
            if (ui == null) return false;

            hud = new HudReading(
                TextOf(ui.blueScoreText),
                TextOf(ui.redScoreText),
                TextOf(ui.blueFlagsText),
                TextOf(ui.redFlagsText),
                TextOf(ui.phaseText),
                TextOf(ui.phaseTimerText),
                IsVisible(ui.phaseTimerText),
                ui.victoryScreen != null && ui.victoryScreen.gameObject.activeInHierarchy);

            return true;
        }

        /// <inheritdoc/>
        public bool TryReadScoreboard(out ScoreboardReading scoreboard)
        {
            scoreboard = default;

            MatchScoreboard board = MatchScoreboard.Current;
            if (board == null) return false;

            scoreboard = new ScoreboardReading(
                board.BlueScore, board.RedScore, board.BlueFlags, board.RedFlags, board.GameEnded);

            return true;
        }

        /// <inheritdoc/>
        public void ReadCapturePoints(List<CapturePointReading> into)
        {
            if (into == null) return;

            CapturePoint[] points = Object.FindObjectsByType<CapturePoint>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (points == null) return;

            // Sorted by name so two clients' arrays line up index for index. Unity's scene order
            // is not a contract, and a diff comparing point 0 on one client against a different
            // point 0 on another would report a flip that never happened.
            System.Array.Sort(points, (a, b) => string.CompareOrdinal(
                a != null ? a.name : string.Empty, b != null ? b.name : string.Empty));

            for (int i = 0; i < points.Length; i++)
            {
                CapturePoint p = points[i];
                if (p == null) continue;

                into.Add(new CapturePointReading(p.name, p.owner));
            }
        }

        private static string TextOf(UnityEngine.UI.Text text)
            => text != null ? text.text : null;

        private static bool IsVisible(UnityEngine.UI.Text text)
            => text != null && text.gameObject.activeInHierarchy && text.enabled;
    }
}
