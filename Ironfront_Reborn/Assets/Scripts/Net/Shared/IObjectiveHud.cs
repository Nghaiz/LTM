namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The match scoreboard: tickets, phase, timer, and whether it is showing live numbers or
    /// stale ones. Phase C4b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="SetAlpha"/> is one call, not six <c>Text</c> references.</b> The dimming it
    /// replaces reached through <c>ScoreUi.instance</c> for six named label fields and set the
    /// alpha on each. Exporting those would have put the scoreboard's own layout into the
    /// netcode and made <c>UnityEngine.UI</c> a dependency of it. Worse, the presenter's own
    /// comment already records that dimming only SOME of the labels is worse than not dimming at
    /// all — and a per-field seam is precisely how that regression gets reintroduced, one
    /// forgotten field at a time. Which labels exist, and which of them dim, is the HUD's
    /// business.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> No HUD registered means the match state is computed
    /// and dropped, which is what a headless client does today.
    /// </para>
    /// </remarks>
    public interface IObjectiveHud
    {
        /// <summary>
        /// Writes the authoritative match state.
        /// </summary>
        /// <param name="secondsRemaining">
        /// <c>-1</c> means "no timer this phase". The HUD hides the timer element on that value
        /// rather than rendering a zero — a sentinel the caller relies on, so it is documented
        /// here and not only at the call site.
        /// </param>
        void SetAuthoritativeState(
            int phase, int tickets0, int tickets1, int secondsRemaining, int humanPlayerCount);

        /// <summary>
        /// Sets the scoreboard's opacity, dimming every label it owns.
        /// </summary>
        /// <remarks>
        /// Used to mark the numbers stale when authority has stopped arriving. A no-op when no
        /// scoreboard is in the scene.
        /// </remarks>
        void SetAlpha(float alpha);
    }
}
