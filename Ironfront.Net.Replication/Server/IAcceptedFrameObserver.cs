using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Notified for every input frame <see cref="InputAuthority"/> accepted. phase-05 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This interface is the root fix.</b> Before it existed,
    /// <see cref="InputAuthority.ApplyPendingInput"/> converted each frame to a
    /// <see cref="MoveInput"/> — which carries Jump, Sprint and Crouch and nothing else — and
    /// then dropped the original. Fire, Reload and Aim were discarded at that conversion, so
    /// every combat class the server owns had no caller and the server's ammo count never
    /// moved. The observer hands the frame over intact, before anything narrows it.
    /// </para>
    /// <para>
    /// <b>An interface rather than an <c>Action</c>.</b> A capturing lambda allocates a
    /// delegate per call and this is the 30 Hz path — conventions.md § 3.2. The Unity seam
    /// implements this on a component it already holds, so the reference is a field assigned
    /// once at construction and the whole path stays allocation-free.
    /// </para>
    /// <para>
    /// <b>Per accepted frame, not per tick.</b> Grading on acceptance is what keeps the
    /// rapid-fire check meaningful: <c>ServerFireResolver.CheckCanFire</c> measures against the
    /// server clock, so a client that sends ten frames in one tick gets one shot and nine
    /// <see cref="Combat.FireRejection.OnCooldown"/> rejections, and
    /// <c>FireRateViolations</c> moves. Batching to one call per tick would silently discard
    /// nine tenths of that evidence.
    /// </para>
    /// </remarks>
    public interface IAcceptedFrameObserver
    {
        /// <summary>
        /// One accepted frame, after its movement has been applied.
        /// </summary>
        /// <param name="session">The player the frame came from.</param>
        /// <param name="frameTick">The client tick the frame claims. Already vetted.</param>
        /// <param name="frame">The frame exactly as it arrived, buttons intact.</param>
        /// <param name="input">
        /// The normalized movement intent derived from it, so an implementer that wants the
        /// clamped axes does not have to re-derive them.
        /// </param>
        void OnAcceptedFrame(
            ClientSession session, uint frameTick, in InputFrame frame, in MoveInput input);
    }
}
