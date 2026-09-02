using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <c>Assembly-CSharp</c> half of the two seams P15 declares: the team palette and the
    /// way into the legacy practice menu. Contracts § 6.3.
    /// </summary>
    /// <remarks>
    /// One file for both, the same call <c>ClientSceneBindings</c> makes: each is a thin forward
    /// over a legacy static or a scene object, and a file each would be two files of a dozen
    /// lines. <c>MenuSceneBindings</c> at the bottom is the component that registers them.
    /// </remarks>
    internal sealed class LegacyTeamPalette : ITeamPalette
    {
        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// <c>ColorScheme.TeamColor</c> is the project's own mapping and the only one — blue for
        /// 0, red for 1, a half-grey for anything else, including the <c>-1</c> that means "no
        /// team". Packing it to <c>0xRRGGBB</c> here rather than returning the <c>Color</c> keeps
        /// alpha and colour-space questions on this side of the seam, where the type that answers
        /// them lives.
        /// </para>
        /// <para>
        /// The channels are read through <c>Mathf.Clamp01</c> before scaling because
        /// <c>Color</c> is not bounded to 0-1 — an HDR or over-driven colour would otherwise
        /// wrap through the byte cast and produce a completely unrelated hue rather than a
        /// clipped one.
        /// </para>
        /// </remarks>
        public int TeamColourRgb(int team)
        {
            Color colour = ColorScheme.TeamColor(team);

            int r = Mathf.RoundToInt(Mathf.Clamp01(colour.r) * 255f);
            int g = Mathf.RoundToInt(Mathf.Clamp01(colour.g) * 255f);
            int b = Mathf.RoundToInt(Mathf.Clamp01(colour.b) * 255f);

            return (r << 16) | (g << 8) | b;
        }
    }

    /// <summary>
    /// Shows and hides the legacy offline menu, which is what "Practice" means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It reveals a screen; it does not start a match.</b> <c>MainMenu.StartLevel</c> reads
    /// the toggles, the victory-score and actor-count fields and the bot-balance slider off its
    /// OWN authored controls, so the offline game is unchanged by this phase precisely because
    /// nothing reproduces it. Criterion 5 — "the bot-balance slider still splits the two teams" —
    /// is true here because the slider is the same slider.
    /// </para>
    /// <para>
    /// <b>The object deactivated is the one carrying <c>MainMenu</c>, not its
    /// <c>menuContent</c>.</b> <c>MainMenu.Update</c> re-asserts
    /// <c>menuContent.SetActive(!OptionsUi.IsOpen())</c> every frame, so deactivating the content
    /// is undone on the next one; only stopping <c>Update</c> from running keeps the legacy menu
    /// down. This is the whole reason <c>IPracticeLauncher</c> has a <c>HidePracticeMenu</c>
    /// method instead of the caller doing <c>SetActive</c> — the caller cannot name the object,
    /// and would pick the wrong one if it could.
    /// </para>
    /// <para>
    /// <b>Resolved lazily and re-resolved on a miss.</b> The Menu scene reloads every time a
    /// match ends, and this binding is registered from a component in it — but the registry is
    /// static and survives, so a cached reference from the previous load would be a destroyed
    /// object that Unity reports as null on first touch. Looking it up when asked keeps
    /// <see cref="IsAvailable"/> honest across scene loads.
    /// </para>
    /// </remarks>
    internal sealed class LegacyPracticeLauncher : IPracticeLauncher
    {
        private MainMenu _menu;

        /// <inheritdoc/>
        public bool IsAvailable => Resolve() != null;

        /// <inheritdoc/>
        public void ShowPracticeMenu() => SetMenuActive(true);

        /// <inheritdoc/>
        public void HidePracticeMenu() => SetMenuActive(false);

        private void SetMenuActive(bool active)
        {
            MainMenu menu = Resolve();
            if (menu == null) return;

            GameObject root = menu.gameObject;
            if (root.activeSelf != active) root.SetActive(active);
        }

        /// <summary>
        /// The legacy menu in the current scene, or null on a build or scene without one.
        /// </summary>
        /// <remarks>
        /// <c>includeInactive</c> is required and is the point: this phase's builder leaves the
        /// legacy menu DEACTIVATED so the new Canvas is what a player lands on, and a search that
        /// skipped inactive objects would report the practice menu missing exactly when it is
        /// waiting to be shown.
        /// </remarks>
        private MainMenu Resolve()
        {
            if (_menu != null) return _menu;

            _menu = Object.FindAnyObjectByType<MainMenu>(FindObjectsInactive.Include);
            return _menu;
        }
    }

    /// <summary>
    /// Registers the two seams P15 declares. Contracts § 6.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A static installer, the same shape as <c>IronfrontNetBindings</c>,</b> and not a scene
    /// component. It cannot be a scene component: the Editor script that authors the menu Canvas
    /// lives in <c>Ironfront.Net.Unity.EditorHarness</c>, an asmdef — and an asmdef cannot
    /// reference the predefined <c>Assembly-CSharp</c> this file compiles into, so the builder
    /// could not add the component even though it adds every other one. Rather than split the
    /// authoring across two scripts on opposite sides of the seal, the registration happens
    /// where it needs no authoring at all.
    /// </para>
    /// <para>
    /// <b>Process-wide is correct here because the practice launcher re-resolves.</b> The worry
    /// with a process-wide registration would be <c>IsAvailable</c> answering true in a match
    /// scene that has no legacy menu — but <c>LegacyPracticeLauncher.Resolve</c> caches through a
    /// <c>MainMenu</c> reference, and a destroyed one compares null under Unity's overloaded
    /// <c>==</c>. Leaving the Menu scene therefore drops the cache and the next
    /// <c>IsAvailable</c> re-searches and answers false, with no teardown to forget.
    /// </para>
    /// <para>
    /// <c>BeforeSceneLoad</c> rather than <c>SubsystemRegistration</c>, because
    /// <c>NetClientBindings.ResetOnLoad</c> clears every slot at subsystem registration.
    /// Registering there would be registering into a table that is about to be wiped — and the
    /// symptom is a Title screen whose Practice button is dead for reasons nothing logs.
    /// </para>
    /// </remarks>
    public static class MenuSceneBindings
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            NetClientBindings.TeamPalette = new LegacyTeamPalette();
            NetClientBindings.Practice = new LegacyPracticeLauncher();
        }
    }
}
