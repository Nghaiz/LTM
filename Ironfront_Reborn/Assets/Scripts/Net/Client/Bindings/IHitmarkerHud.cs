namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The one HUD call the client netcode makes: a hitmarker, at a severity. Phase C4a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to <c>IngameUi.Hit(int)</c>, and stays an <c>int</c> across the seam for the reason
    /// <c>ScoreUi</c>'s own remark already records: the severity is an enum in the replication
    /// library, and widening the HUD's signature to that enum would make <c>Assembly-CSharp</c>
    /// take a dependency on the replication library for a cosmetic.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> A build with no HUD — a headless client, an EditMode
    /// test — registers nothing, and the presenter's hit path becomes silent rather than
    /// throwing. That is the pre-existing behaviour: <c>IngameUi.Hit</c> already no-ops without
    /// an instance.
    /// </para>
    /// </remarks>
    public interface IHitmarkerHud
    {
        /// <summary>
        /// Shows a hitmarker at <paramref name="severity"/>.
        /// </summary>
        /// <remarks>
        /// The newest hit wins, including a quieter one — that is <c>HitmarkerModel</c>'s
        /// documented semantics, and it is settled on the netcode side before this is called.
        /// </remarks>
        void ShowHit(int severity);
    }
}
