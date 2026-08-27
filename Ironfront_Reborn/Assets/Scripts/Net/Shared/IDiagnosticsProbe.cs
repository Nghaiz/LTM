using System.Collections.Generic;

namespace Ironfront.Net.Unity
{
    /// <summary>The scoreboard labels as the checkpoint recorder serialises them.</summary>
    /// <remarks>
    /// Strings and bools, never <c>Text</c> components. The recorder wants what the player can
    /// READ, and a component reference would let it start driving the HUD it is supposed to be
    /// observing.
    /// </remarks>
    public readonly struct HudReading
    {
        public readonly string BlueScore;
        public readonly string RedScore;
        public readonly string BlueFlags;
        public readonly string RedFlags;
        public readonly string Phase;
        public readonly string PhaseTimer;
        public readonly bool PhaseTimerVisible;
        public readonly bool VictoryVisible;

        public HudReading(
            string blueScore, string redScore, string blueFlags, string redFlags,
            string phase, string phaseTimer, bool phaseTimerVisible, bool victoryVisible)
        {
            BlueScore         = blueScore;
            RedScore          = redScore;
            BlueFlags         = blueFlags;
            RedFlags          = redFlags;
            Phase             = phase;
            PhaseTimer        = phaseTimer;
            PhaseTimerVisible = phaseTimerVisible;
            VictoryVisible    = victoryVisible;
        }
    }

    /// <summary>The offline scoreboard's counters.</summary>
    public readonly struct ScoreboardReading
    {
        public readonly int BlueScore;
        public readonly int RedScore;
        public readonly int BlueFlags;
        public readonly int RedFlags;
        public readonly bool GameEnded;

        public ScoreboardReading(
            int blueScore, int redScore, int blueFlags, int redFlags, bool gameEnded)
        {
            BlueScore = blueScore;
            RedScore  = redScore;
            BlueFlags = blueFlags;
            RedFlags  = redFlags;
            GameEnded = gameEnded;
        }
    }

    /// <summary>One capture point, as check 3 (E9) compares it across two clients.</summary>
    /// <remarks>
    /// <b>Owner only, and the capture bar is deliberately absent.</b> <c>CapturePoint.control</c>
    /// is private and the bar it drives lives behind <c>IngameUi.SetFlagIndicator</c>. Making
    /// that field public to grade it would be a change to shipped client code for a harness's
    /// convenience — which the lane-B plan's § 6 forbids, and which this seam must not become a
    /// back door for. The honest cost, recorded before this refactor and unchanged by it, is that
    /// half of one check is graded by eye against the screenshot pair.
    /// </remarks>
    public readonly struct CapturePointReading
    {
        public readonly string Name;
        public readonly int Owner;

        public CapturePointReading(string name, int owner)
        {
            Name  = name;
            Owner = owner;
        }
    }

    /// <summary>
    /// The legacy state the lane-B recorder observes: the scoreboard HUD, the offline
    /// scoreboard, and the scene's capture points. Phase C4d.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One seam for three reads, because they are one job.</b> <c>Net/Diagnostics</c> is an
    /// observer: it serialises state into JSON that two clients' runs are diffed against. Every
    /// legacy type it still named — <c>ScoreUi</c>, <c>MatchScoreboard</c>, <c>CapturePoint</c> —
    /// was named for exactly that, so they arrive through one registered probe rather than three
    /// interfaces that would always be registered together.
    /// </para>
    /// <para>
    /// <b>Readings are snapshots, never components.</b> The probe hands back strings, ints and
    /// bools. That is not tidiness: an observer holding a <c>Text</c> or a <c>CapturePoint</c>
    /// could write to the thing it is measuring, and a harness that perturbs its subject reports
    /// numbers nobody can trust.
    /// </para>
    /// <para>
    /// <b>Absent is a normal answer.</b> A headless run has no HUD and a netcode match has no
    /// offline scoreboard; both already rendered as <c>"absent"</c> in the JSON before this seam
    /// existed, and both still do.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT routed through <c>ICapturePointDirectory</c></b>, though that seam
    /// already exists in <c>Ironfront.Net.Unity.Shared</c> and would have saved a binding. It can
    /// SET an authoritative owner but cannot report one, so reuse would have meant extending it —
    /// and, worse, switching the recorder from a scene scan to the bound directory changes WHICH
    /// POINTS IT SEES when nothing has bound one. Changing what a measurement instrument measures,
    /// inside a refactor whose acceptance criteria forbid changing behaviour, is not a saving.
    /// </para>
    /// </remarks>
    public interface IDiagnosticsProbe
    {
        /// <summary>The scoreboard HUD, when this build has one in the scene.</summary>
        bool TryReadHud(out HudReading hud);

        /// <summary>The offline scoreboard, when one exists.</summary>
        bool TryReadScoreboard(out ScoreboardReading scoreboard);

        /// <summary>
        /// Appends every capture point in the scene to <paramref name="into"/>, sorted by name.
        /// </summary>
        /// <remarks>
        /// <b>Sorted by name, and the sort belongs to the probe.</b> Two clients' arrays are
        /// diffed index for index, and Unity's scene order is not a contract — an unsorted pair
        /// would report a flip that never happened. Keeping the sort on this side means the
        /// ordering cannot drift from the scan that produced it.
        /// </remarks>
        void ReadCapturePoints(List<CapturePointReading> into);
    }
}
