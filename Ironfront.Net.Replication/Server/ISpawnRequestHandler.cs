namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Handles an accepted C_SPAWN_REQUEST (0x23). phase-05 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Respawning puts an actor back in the world, which is an engine operation — a transform
    /// to move, a collider to re-enable, a ragdoll to put away. So the router decodes the
    /// message and this interface carries it to whoever can act on it, keeping the decode in
    /// CI and the engine work out of it.
    /// </para>
    /// <para>
    /// <b>An implementer must consult <c>ServerRespawnGate</c> and silently drop an early
    /// request.</b> A client whose clock is slightly fast asking half a tick early is not a
    /// protocol violation and must not be thrown for, disconnected over, or counted as
    /// malformed — it is the single most common thing this message will ever do.
    /// </para>
    /// </remarks>
    public interface ISpawnRequestHandler
    {
        /// <summary>The session asked to respawn. Grant it, or drop it, per the gate.</summary>
        void OnSpawnRequested(ClientSession session);
    }
}
