namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// A one-shot countdown measured in simulation ticks rather than wall clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not <c>WaitForSeconds</c>.</b> Where a wait gates authoritative state, a
    /// wall-clock coroutine is a race: it cannot be cancelled, it re-samples the world when it
    /// wakes rather than reacting to the change that should have cancelled it, and its length
    /// moves with <c>Time.timeScale</c> and with a paused server. The shipped
    /// <c>Actor.ReactivateCollisionsWith</c> is all three at once — it holds hitbox layer state
    /// across half a second and then asks whether the actor happens to be seated, which any
    /// seat change arriving inside the window silently decides.
    /// </para>
    /// <para>
    /// A tick count fires on the same tick on every peer, and cancelling it is a state change
    /// rather than a hope.
    /// </para>
    /// </remarks>
    public struct TickTimer
    {
        /// <summary>Ticks left before the timer fires. Zero means disarmed.</summary>
        public int TicksRemaining;

        /// <summary>True while a fire is still pending.</summary>
        public bool IsArmed
        {
            get { return TicksRemaining > 0; }
        }

        /// <summary>
        /// Arms the timer for <paramref name="ticks"/> ticks, replacing any pending countdown.
        /// A non-positive count disarms it, so <c>Arm(0)</c> never fires.
        /// </summary>
        public void Arm(int ticks)
        {
            TicksRemaining = ticks > 0 ? ticks : 0;
        }

        /// <summary>Disarms without firing.</summary>
        public void Cancel()
        {
            TicksRemaining = 0;
        }

        /// <summary>
        /// Advances one tick.
        /// </summary>
        /// <returns>
        /// <c>true</c> exactly once, on the tick the countdown reaches zero. <c>false</c>
        /// before that and on every call to an already-expired or never-armed timer.
        /// </returns>
        public bool Tick()
        {
            if (TicksRemaining <= 0)
                return false;

            TicksRemaining--;
            return TicksRemaining == 0;
        }
    }
}
