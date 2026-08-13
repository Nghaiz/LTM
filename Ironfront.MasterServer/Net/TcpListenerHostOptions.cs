using System;
using System.Net;

namespace Ironfront.MasterServer.Net
{
    /// <summary>
    /// Tunables for <see cref="TcpListenerHost"/>. The defaults are the numbers phase 00
    /// specifies; they are settable so the tests can exercise a 30-second timeout without
    /// taking 30 seconds.
    /// </summary>
    public sealed class TcpListenerHostOptions
    {
        /// <summary>
        /// The address to bind. IPv4 by default, which is what
        /// <see cref="ClientConnection.ToIpKey"/> keys the per-IP limit on. Turning this into
        /// a dual-stack IPv6 socket is a phase 03 deployment decision, not a default.
        /// </summary>
        public IPAddress BindAddress { get; set; } = IPAddress.Any;

        /// <summary>
        /// The TCP port. <c>0</c> asks the OS for an ephemeral port and
        /// <see cref="TcpListenerHost.Port"/> reports which one it got — that is how the tests
        /// avoid fighting each other, and each other's leftovers, over port 27000.
        /// </summary>
        public int Port { get; set; }

        /// <summary>The accept backlog handed to <c>listen(2)</c>.</summary>
        public int Backlog { get; set; } = 128;

        /// <summary>
        /// Anti connection-flood: phase 00 security table, "too many connections from one IP".
        /// </summary>
        public int MaxConnectionsPerIp { get; set; } = 5;

        /// <summary>
        /// Total accepted connections held at once, across every address. 0 disables the cap.
        /// </summary>
        /// <remarks>
        /// The per-IP limit alone bounds one attacker on one address; it does nothing about a
        /// botnet, an IPv4 range, or a NAT gateway, each of which arrives as a stream of
        /// distinct addresses that are individually under the per-IP limit. Every accepted
        /// connection costs a socket, a pooled receive buffer and a task, so with no ceiling
        /// the process runs out of handles rather than refusing anybody. 256 is roughly two
        /// orders of magnitude above a full lobby and still far below any file-descriptor
        /// limit, so it only ever fires on something that is not real traffic.
        /// </remarks>
        public int MaxTotalConnections { get; set; } = 256;

        /// <summary>
        /// Slowloris defense: connect, say nothing, hold a slot forever. Until a connection
        /// has authenticated it gets this long and no longer.
        /// </summary>
        public TimeSpan UnauthenticatedTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The half-open detector (D7). A client whose network is unplugged sends no FIN and
        /// no RST, and the OS keepalive default is two hours — so liveness has to be measured
        /// at the application level: <c>0x00F0 HEARTBEAT</c> every 15 s, three missed in a row
        /// and the connection is gone.
        /// </summary>
        public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>
        /// How often the single logic thread wakes to drain the queue and check timeouts.
        /// 20 Hz is generous for a lobby whose traffic is a few messages per minute per client.
        /// </summary>
        public TimeSpan LogicTickInterval { get; set; } = TimeSpan.FromMilliseconds(50);
    }
}
