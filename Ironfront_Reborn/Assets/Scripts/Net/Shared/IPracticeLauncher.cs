namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The way into the offline bot match, which is entirely legacy. P15 3.3, contracts § 6.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What Practice actually is.</b> <c>MainMenu.StartLevel</c> reads its OWN authored
    /// controls — the assault/reverse/night/no-vehicles toggles, the victory-score and actor-count
    /// fields, and the bot-balance slider that splits <c>ActorManager.team0Bots</c> and
    /// <c>team1Bots</c> — then loads the map. Every one of those names is
    /// <c>Assembly-CSharp</c>, so <c>Net/Client</c> cannot say any of them (contracts § 6.1).
    /// </para>
    /// <para>
    /// <b>The practice screen now chooses the match</b> (owner, 2026-10-08: "everything there says
    /// IN DEVELOPMENT"). P15 kept this seam to showing the legacy screen so that
    /// <c>MainMenu.cs</c> stayed untouched; the new screen has since replaced that screen, and it
    /// passes a <see cref="PracticeSettings"/> that the implementation writes over what
    /// <c>MainMenu.StartLevel</c> read from its hidden controls, in the same frame and before the
    /// map loads. <c>MainMenu.cs</c> is still untouched, and the two legacy toggles the new screen
    /// does not offer (assault and reverse) keep their authored values.
    /// </para>
    /// <para>
    /// <b>Why hiding is a method and not <c>SetActive</c> at the call site.</b>
    /// <c>MainMenu.Update</c> re-asserts <c>menuContent.SetActive(!OptionsUi.IsOpen())</c> every
    /// frame, so deactivating the content is undone on the next one. Only deactivating the object
    /// that carries <c>MainMenu</c> itself keeps it down, and which object that is, is the
    /// implementation's business — a caller that guessed would be guessing at a hierarchy it
    /// cannot name.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> A build with no legacy menu in the scene — a headless
    /// client, a test, a future build that has retired it — registers nothing, and
    /// <see cref="IsAvailable"/> answers false so the Title screen can disable the Practice
    /// button rather than offering a dead one.
    /// </para>
    /// </remarks>
    public interface IPracticeLauncher
    {
        /// <summary>
        /// Whether this build has a legacy practice menu to show.
        /// </summary>
        /// <remarks>
        /// Separate from "is anything registered", because a binding can be present in the scene
        /// and still have lost its target — the legacy menu destroyed, or never authored in this
        /// scene. Both cases must disable the button rather than show one that does nothing, and
        /// only the implementation can tell them apart.
        /// </remarks>
        bool IsAvailable { get; }

        /// <summary>Reveals the legacy practice menu. A no-op when it is already up.</summary>
        void ShowPracticeMenu();

        /// <summary>
        /// Hides the legacy practice menu again, for the Back button.
        /// </summary>
        /// <remarks>
        /// Idempotent, and safe when the menu was never shown: a player who reaches Back by any
        /// route must not have the call depend on how they got there.
        /// </remarks>
        void HidePracticeMenu();

        /// <summary>
        /// Launches one scene selected from the authoritative map catalogue, played by
        /// <paramref name="settings"/>: the practice screen's mode, victory rule, points, bots,
        /// side, vehicles and respawn time (owner, 2026-10-08).
        /// </summary>
        void LaunchMap(string sceneName, in PracticeSettings settings);
    }
}
