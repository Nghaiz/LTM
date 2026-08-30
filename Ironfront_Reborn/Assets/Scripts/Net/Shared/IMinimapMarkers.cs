using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where a replicated body puts its minimap icon. P3 task 3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a seam and not a <c>MinimapUi</c> call.</b> <c>MinimapUi</c>, <c>ColorScheme</c>
    /// and <c>MinimapMarker</c> all compile into <c>Assembly-CSharp</c>, which is compiled last
    /// and which no assembly definition may reference — the same constraint that produced
    /// <see cref="ICapturePointDirectory"/> and <see cref="IDecalSink"/>. The registries that
    /// own the replicated bodies live in <c>Ironfront.Net.Unity.Client</c>, so they cannot name
    /// the minimap at all; they name this.
    /// </para>
    /// <para>
    /// <b>The team crosses as an <c>int</c>, and the colour does not.</b> Which hue a team wears
    /// is presentation, it already has exactly one answer in <c>ColorScheme.TeamColor</c>, and
    /// sending a <c>Color</c> across would put that decision in the netcode where a second
    /// answer could grow. The spelling is <c>SpawnPoint.owner</c>'s — 0, 1, or -1 for "no team
    /// known yet" — which is what <c>CapturePointOwnership.ToSpawnPointOwner</c> already
    /// converts the wire's <c>TeamId</c> byte into on this side of the seam.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> A dedicated server and an EditMode test register
    /// nothing; every call is then a no-op and no body draws an icon, which is what a build with
    /// no HUD already did.
    /// </para>
    /// <para>
    /// <b>Nothing here registers an <c>Actor</c>.</b> Ledger A-2 stands: a replicated proxy must
    /// never reach <c>ActorManager.Register</c>, because that assignment repoints
    /// <c>ActorManager.Player</c> at somebody else's body. Keying icons by <c>Transform</c> is
    /// how a proxy gets drawn without becoming an <c>Actor</c>.
    /// </para>
    /// </remarks>
    public interface IMinimapMarkers
    {
        /// <summary>
        /// Draws — or recolours — the icon following <paramref name="subject"/>.
        /// </summary>
        /// <remarks>
        /// Idempotent by subject: called again for a transform that already has an icon it
        /// recolours in place rather than stacking a second one. Callers rely on that, because
        /// a body's team arrives with the snapshot rather than with the spawn, so this is
        /// written every frame a snapshot is sampled.
        /// </remarks>
        /// <param name="subject">The body's transform. The icon reads its position each frame.</param>
        /// <param name="team">0, 1, or -1 for "no team known yet".</param>
        void SetBodyMarker(Transform subject, int team);

        /// <summary>
        /// Drops <paramref name="subject"/>'s icon. Safe for a subject that never had one.
        /// </summary>
        /// <remarks>
        /// Call this BEFORE the transform is destroyed or returned to a pool. The icon table is
        /// keyed by the transform, and a destroyed object is not a usable dictionary key on
        /// Unity's Mono runtime — so an entry dropped late is one nothing can ever remove, and
        /// a pooled transform handed to the next occupant would arrive still wearing the
        /// previous one's team colour.
        /// </remarks>
        void RemoveMarker(Transform subject);
    
        /// <summary>
        /// Installs an extra "hold the map open" signal, OR'd with the player's keyboard.
        /// Null clears it.
        /// </summary>
        /// <remarks>
        /// <b>Ledger X-61.</b> The map opens only while a key is held, and a scripted lane-B
        /// client has no keyboard — so no run could ever open it, and the icons this interface
        /// draws have never been proven to render. Routed through this seam rather than named
        /// directly because <c>Net/Diagnostics</c> is an asmdef and <c>MinimapUi</c> lives in
        /// the predefined assembly, which no asmdef may reference.
        /// </remarks>
        void SetHoldSource(System.Func<bool> source);

        /// <summary>
        /// How far the map is open, 0 closed to 1 open, or -1 when no map exists.
        /// </summary>
        /// <remarks>
        /// Without this the hold above proves nothing: a request that had no effect and a
        /// request never made render identically. -1 rather than 0 for a missing HUD, because
        /// zero is a real value meaning "closed".
        /// </remarks>
        float Openness { get; }

        /// <summary>
        /// Whether the installed hold source is asking for the map right now.
        /// </summary>
        /// <remarks>
        /// The EFFECTIVE request, read from the same place the map reads it — not the
        /// programme's intent re-derived by the recorder. Those two differ exactly when the
        /// wiring is broken, which is the case worth catching.
        /// </remarks>
        bool HoldRequested { get; }
}
}
