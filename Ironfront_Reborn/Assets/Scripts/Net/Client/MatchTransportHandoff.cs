#nullable enable

using Ironfront.Net.Transport;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Carries an already-connected game transport across a scene load, from the shell scene
    /// that dialled it to the map scene's <c>NetClientBootstrap</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a static slot rather than a serialized reference.</b> The receiver does not exist
    /// yet when the offer is made — it is a component in a scene that has not loaded — and it
    /// reads the offer from its own <c>Awake</c>, which runs before the loader gets control
    /// back. There is no object to hold a reference on and no frame in which to assign one.
    /// This is the same shape as <c>NetClientBootstrap.Current</c> and
    /// <c>ServerTickLoop.Current</c>, which exist for the mirror-image reason.
    /// </para>
    /// <para>
    /// <b>It carries the <see cref="ConnectResult"/> as well as the socket, and it has to.</b>
    /// <c>NetClientBootstrap.OnConnected</c> is where the connection id, the server tick and the
    /// prediction clock's seed are taken from, and that event fired while the map scene was
    /// still loading — before the component that handles it existed. Handing over only the
    /// transport would leave a client whose link is up and whose <c>ConnectionId</c> is 0
    /// forever, which reads on every screen as a server that never answered.
    /// </para>
    /// <para>
    /// <b><see cref="TryTake"/> clears the slot, and that is the whole ownership rule.</b> The
    /// offer is consumed exactly once. A second map load in the same process — a rematch — dials
    /// again and offers again; a map scene opened directly in the Editor with nothing on offer
    /// finds an empty slot and dials for itself, which is the behaviour every existing
    /// single-scene workflow depends on.
    /// </para>
    /// </remarks>
    public static class MatchTransportHandoff
    {
        private static ITransportClient? _transport;
        private static ConnectResult _result;

        /// <summary>Whether a transport is waiting to be adopted.</summary>
        public static bool HasOffer => _transport != null;

        /// <summary>
        /// Offers a connected transport to the next map scene that comes up.
        /// </summary>
        /// <remarks>
        /// Overwrites any previous offer rather than refusing. An offer that was never taken
        /// belongs to a scene load that did not happen — a connect that failed after the accept,
        /// or a load cancelled by a disconnect — and keeping the stale one would hand the next
        /// match a socket belonging to the last.
        /// </remarks>
        public static void Offer(ITransportClient transport, in ConnectResult result)
        {
            _transport = transport;
            _result = result;
        }

        /// <summary>Takes the offer, clearing it. False when there is none.</summary>
        public static bool TryTake(out ITransportClient transport, out ConnectResult result)
        {
            if (_transport == null)
            {
                transport = null!;
                result = default;
                return false;
            }

            transport = _transport;
            result = _result;

            _transport = null;
            _result = default;
            return true;
        }

        /// <summary>
        /// Drops any pending offer without adopting it.
        /// </summary>
        /// <remarks>
        /// Called when a junction fails between the accept and the scene being ready. Without
        /// it the failed connection's socket would sit in the slot and be adopted by whatever
        /// map scene loaded next, including one the player reached by a completely different
        /// route.
        /// </remarks>
        public static void Clear()
        {
            _transport = null;
            _result = default;
        }
    }
}
