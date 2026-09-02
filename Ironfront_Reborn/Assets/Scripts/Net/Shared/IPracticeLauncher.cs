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
    /// <b>So this seam SHOWS a screen; it does not start a match.</b> That is the decision worth
    /// recording, because the alternative reads more natural and is wrong. A
    /// <c>Launch(scene, actorCount, botBalance, …)</c> signature would need the new Canvas to
    /// re-author every one of those controls, which is a fourth screen P15 does not scope
    /// (3.2 names three) and a second copy of a shipped screen — and criterion 5, "the
    /// bot-balance slider still splits the two teams", would then have to be re-proven against
    /// new controls instead of being true because nothing moved. Revealing the legacy menu keeps
    /// the offline game bit-identical to what ships today and keeps <c>MainMenu.cs</c> untouched,
    /// which 3.5 requires.
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
    }
}
