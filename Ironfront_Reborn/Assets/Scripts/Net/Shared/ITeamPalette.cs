namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The colour a team is drawn in. P15 3.3, contracts § 6.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a seam and not a constant.</b> The mapping lives in <c>ColorScheme.TeamColor</c>,
    /// which is <c>Assembly-CSharp</c> — a name <c>Net/Client</c> may not write down
    /// (contracts § 6.1). The obvious dodge, a hardcoded blue and red in the UI, is worse than
    /// the coupling it avoids: it is a second copy of a mapping the game already owns, and the
    /// first time somebody re-themes a side the netcode UI keeps drawing the old colours with
    /// nothing failing.
    /// </para>
    /// <para>
    /// <b>Built in P15 although P15 barely uses it.</b> The Title, Login and Register screens
    /// need one team colour between them. P16's roster columns and P17's scoreboard rows need it
    /// per row. Declaring it here is what stops each of those inventing its own blue/red while
    /// the other two are unwritten — the same reason the screen-switching mechanism is built now
    /// rather than when the second screen arrives.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> Nothing registered means the caller falls back to its
    /// own neutral colour; see <c>NetClientBindings.TeamColour</c>, which answers rather than
    /// throws. The Menu scene can load before any binding has registered, so a seam that threw
    /// would take the menu down on exactly the frame it is most visible.
    /// </para>
    /// </remarks>
    public interface ITeamPalette
    {
        /// <summary>
        /// The colour for <paramref name="team"/>, packed as <c>0xRRGGBB</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An int rather than a <c>UnityEngine.Color</c>, on purpose.</b> <c>Color</c> would
        /// cross the seal perfectly well — it is an engine type — but returning one invites the
        /// implementation to hand back <c>ColorScheme</c>'s own struct field and the interface to
        /// grow alpha, gamma and HDR range questions that belong to whoever is drawing. A packed
        /// RGB is the whole answer to "which team is this", and every caller converts it the same
        /// way.
        /// </para>
        /// <para>
        /// <paramref name="team"/> is the protocol's team byte widened to <c>int</c>, so an
        /// unknown or unassigned team (<c>-1</c> is what <c>ILocalPlayerRig</c> reports when
        /// absent) reaches the implementation rather than being filtered here. The implementation
        /// decides what an unknown team looks like; this interface only promises it will not
        /// throw.
        /// </para>
        /// </remarks>
        int TeamColourRgb(int team);
    }
}
