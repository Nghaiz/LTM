namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The conquest score rule: how a kill turns into points, and when a team has won.
    /// The ONE copy, called by both runtimes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this type exists.</b> The offline match (<c>MatchScoreboard</c>, Assembly-CSharp)
    /// and the networked match (<c>MatchStateMachine</c>, Ironfront.Net.Replication) were
    /// playing two different games: one ascended on kills and ended on a 200-point MARGIN, the
    /// other descended 200 tickets, charged the victim's own side, and ended when either side
    /// hit zero. Phase P11 makes the networked match play the game the project actually has,
    /// and this is the single place that rule is written down.
    /// </para>
    /// <para>
    /// <b>It cannot be "just call <c>MatchScoreboard</c>".</b> Predefined assemblies compile
    /// last and no asmdef references back (<c>tools/check-net-layering.ps1</c>), so
    /// Ironfront.Net.Replication can never see a type in Assembly-CSharp. The rule therefore
    /// moves DOWN, not sideways, and both runtimes call it.
    /// </para>
    /// <para>
    /// <b>Why Ironfront.Net.Protocol and not Ironfront.Net.Replication.Match.</b> The plan
    /// (<c>plans/00-shared/team-multiplayer-contracts.md</c> § 1.3) names
    /// <c>Ironfront.Net.Replication/Match/</c>, and its own § 2.2 requires
    /// <see cref="MatchStateMessage.WinningTeam"/> — which lives HERE, in Protocol — to be
    /// <see cref="Decide"/>. Protocol does not and must not reference Replication, so those two
    /// halves cannot both hold. Protocol wins: <see cref="Decide"/> returns a
    /// <see cref="TeamId"/>, which is a protocol value declared in this assembly, and putting
    /// the rule anywhere else would force <c>WinningTeam</c> to grow a second copy of the
    /// margin test — the exact duplication P11 exists to remove. No game-design NUMBER moves
    /// here: <c>victoryPoints</c> and the per-kill award stay parameters, owned by
    /// <c>MatchRules</c> and <c>GameManager</c>.
    /// </para>
    /// <para>
    /// <b>Pure statics, no state, no events.</b> The two runtimes keep their own state — the
    /// <c>Win(bool)</c> latch and the <c>Changed</c>/<c>Scored</c>/<c>Ended</c> events stay on
    /// <c>MatchScoreboard</c>, the phase machine stays on <c>MatchStateMachine</c>. Only the
    /// arithmetic is shared, because only the arithmetic was duplicated.
    /// </para>
    /// </remarks>
    public static class ConquestScoreRule
    {
        /// <summary>A team's score multiplier at this capture-point count.</summary>
        /// <remarks>
        /// The identity function, faithfully: a team holding zero capture points scores zero
        /// per kill. <b>That is the rule, not a hazard</b> — it is what makes holding ground
        /// worth more than trading kills, and it is reachable only by losing every point
        /// mid-match. Both shipped maps open one point per side (Dustbowl
        /// <c>1, -1, -1, 0, -1, -1</c>; Island <c>0, 1, -1, -1, -1</c>), so a match opens 1/1 at
        /// x1; a map that hands neither side a base is caught loudly by
        /// <c>MatchController</c>'s opening-ownership check.
        /// </remarks>
        public static int ScoreMultiplier(int flags) => flags;

        /// <summary>What <paramref name="points"/> kills are worth to a team holding
        /// <paramref name="flags"/> capture points.</summary>
        public static int Award(int points, int flags) => points * ScoreMultiplier(flags);

        /// <summary>
        /// Who has won, or <see cref="TeamId.None"/> while neither margin is met.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Victory is a MARGIN, not a total</b> — <c>own &gt;= other + victoryPoints</c>. A
        /// match does not end when someone is ahead; it ends when someone is ahead by
        /// <paramref name="victoryPoints"/>. The tie case needs no branch: neither margin is
        /// met, so it falls out as <see cref="TeamId.None"/>.
        /// </para>
        /// <para>
        /// Team 0 is tested first, exactly as <c>MatchScoreboard.AddScore</c> tests blue first.
        /// The order is only observable at <paramref name="victoryPoints"/> ≤ 0, where every
        /// state satisfies both margins — and that is deliberately NOT special-cased, because
        /// the offline rule does not special-case it either and a divergence here would be a
        /// second rule wearing this type's name. A zero victory target is a mis-authored match
        /// setting, and it ends the round instantly in both runtimes identically.
        /// </para>
        /// </remarks>
        public static byte Decide(int score0, int score1, int victoryPoints)
            => score0 >= score1 + victoryPoints ? TeamId.Team0
             : score1 >= score0 + victoryPoints ? TeamId.Team1
             : TeamId.None;
    }
}
