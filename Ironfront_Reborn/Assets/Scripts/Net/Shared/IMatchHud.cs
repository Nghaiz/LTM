namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The in-match readout: which side you are on, who is killing whom, and the screen that
    /// comes up when you die. P17 3.1, 3.2 and 3.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A registry seam although both ends live in <c>Ironfront.Net.Unity.Client</c>.</b> The
    /// eleven seams beside this one exist to cross the assembly seal (contracts § 6); this one
    /// does not, and the reason it is still a seam is scene topology. The presenters sit on the
    /// <c>NetClient</c> object, authored into the map scene. The HUD sits on
    /// <c>Ingame UI Container.prefab</c>, which <c>GameManager.StartGame</c> instantiates at
    /// runtime and only when <c>LocalClient.Exists</c>. Neither can hold a serialized reference
    /// to the other — one does not exist when the other is authored — so the presenters would
    /// otherwise reach for the HUD with a per-frame scene search. Registration is what
    /// <c>NetClientBindings.Hud</c> already does for the hitmarker, for exactly this reason.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> A dedicated server instantiates no HUD, an EditMode
    /// test has no scene, and the offline game's HUD registers nothing because the component
    /// disables itself when <c>NetContext.IsClient</c> is false. Every call site therefore reads
    /// the slot and returns rather than branching on a role a second time.
    /// </para>
    /// <para>
    /// <b>The killfeed is written by REWRITE, not per frame.</b> The IMGUI stopgap this replaces
    /// allocated its strings on every <c>OnGUI</c> and its own remark called that "the honest
    /// cost of the stopgap ... the replacement does not have this problem". So the caller pushes
    /// only when the visible feed has actually changed, and the two calls below are ordered:
    /// <see cref="SetKillfeedLineCount"/> first, then <see cref="SetKillfeedLine"/> for each
    /// index below that count. A HUD must tolerate a count with no following lines — that is the
    /// clear, which is the common case.
    /// </para>
    /// <para>
    /// <b>Names and teams arrive already resolved.</b> The name table is
    /// <c>NetClientCombatPresenter.Names</c> and the team comes off the decoded snapshot; both
    /// are the caller's, and handing the HUD an actor id instead would give it a second way to
    /// answer "who is that" for the sake of a narrower signature.
    /// </para>
    /// </remarks>
    public interface IMatchHud
    {
        /// <summary>
        /// The side this client is on, as the protocol's team byte widened to <c>int</c>.
        /// </summary>
        /// <remarks>
        /// <c>TeamId.None</c> means the snapshot has not answered yet, and the HUD renders
        /// <b>blank</b> rather than a side. Stating one would be the fabricated zero
        /// <c>ScoreUi</c> already refuses for a human count that has not arrived — and this
        /// element exists precisely so that a wrong team is visible, which it cannot be if the
        /// unknown state looks like team 0.
        /// </remarks>
        void SetLocalTeam(int team);

        /// <summary>How many killfeed lines follow. Zero clears the feed.</summary>
        void SetKillfeedLineCount(int count);

        /// <summary>
        /// One killfeed line. Called after <see cref="SetKillfeedLineCount"/>, for each index
        /// below it, newest first.
        /// </summary>
        /// <param name="killerTeam">
        /// <c>TeamId.None</c> when the killer is the world, or when the snapshot does not carry
        /// that actor — a normal outcome for a kill outside this client's interest radius. The
        /// HUD colours those neutrally rather than guessing a side.
        /// </param>
        void SetKillfeedLine(
            int index, string killerName, int killerTeam, string victimName, int victimTeam,
            bool headshot);

        /// <summary>Raises the deploy screen and names who killed you.</summary>
        void ShowDeploy(string killerName, int killerTeam);

        /// <summary>
        /// The respawn clock, once a frame while the screen is up.
        /// </summary>
        /// <param name="canDeploy">
        /// Whether the server would accept a respawn request now. The HUD makes its Deploy
        /// control interactable on this and nothing else, so a press cannot arrive before
        /// <c>ServerRespawnGate</c> would take it.
        /// </param>
        void TickDeploy(float secondsUntilRespawn, bool canDeploy);

        /// <summary>
        /// Takes the deploy screen down.
        /// </summary>
        /// <remarks>
        /// Driven by the caller's alive signal, never by the Deploy control being pressed — a
        /// respawn the player did not ask for (a server force-respawn, a match reset) has to
        /// clear this screen too, and a screen closed by its own button survives exactly that
        /// case and blocks the player (P17 criterion 5).
        /// </remarks>
        void HideDeploy();

        /// <summary>
        /// Whether the Deploy control was pressed since this was last asked, clearing the edge.
        /// </summary>
        /// <remarks>
        /// A consuming read, the shape <c>IInputSource.RespawnPressed</c> already uses for the
        /// scripted respawn — the caller polls it beside the keyboard in the same condition, so
        /// the button and the spacebar reach <c>C_SPAWN_REQUEST</c> through one path rather than
        /// two.
        /// </remarks>
        bool ConsumeDeployPressed();
    }
}
