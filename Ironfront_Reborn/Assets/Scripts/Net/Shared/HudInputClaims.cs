namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Input a HUD element has taken for itself this moment, so the gameplay code that would act on
    /// the same input leaves it alone.
    /// </summary>
    /// <remarks>
    /// The mouse wheel switches weapons. While the Tab board is open the wheel turns its pages
    /// (owner's list of 2026-10-09, item 2), and a page turn that also changed the gun in the
    /// player's hands would be the board disarming them. The board sets the claim; the weapon wheel
    /// in <c>FpsActorController</c> reads it, as it reads the map's own claim.
    /// </remarks>
    public static class HudInputClaims
    {
        /// <summary>The scoreboard is open and turns its pages with the mouse wheel.</summary>
        public static bool ScoreboardOwnsWheel { get; set; }
    }
}
