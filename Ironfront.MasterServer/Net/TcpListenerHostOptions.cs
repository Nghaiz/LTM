using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;

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

        /// <summary>
        /// The clock the timeout sweep measures against. Defaults to the real one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This exists for the tests, and it earns its place because the alternative was
        /// measurably worse. <see cref="UnauthenticatedTimeout"/> and
        /// <see cref="HeartbeatTimeout"/> are 30 s and 45 s in production; a test that waited
        /// those out is a test nobody runs, so the tests shrank them to a few hundred
        /// milliseconds and paced themselves with <c>Task.Delay</c>. On a shared CI runner a
        /// thread-pool continuation can be stalled for an unbounded time while the real clock
        /// keeps moving, which drifts the test's pacing away from the server's deadline in
        /// whichever direction the runner happens to stall — reaping a connection the test
        /// expected to survive, and, worse, reaping one the test expected to survive for the
        /// WRONG reason so a security test goes green without proving anything.
        /// </para>
        /// <para>
        /// Substituting the clock removes the race instead of widening it: a held clock cannot
        /// be advanced by a stall, so what the sweep sees is exactly what the test set. The
        /// window widening in the heartbeat test (300 ms, then 1500 ms, still flaking) is the
        /// approach this replaces.
        /// </para>
        /// <para>
        /// Only the <b>expiry comparison</b> reads this. The logic loop's tick cadence stays on
        /// the real clock on purpose — see <see cref="TcpListenerHost.RunAsync"/>.
        /// </para>
        /// </remarks>
        public TimeProvider Clock { get; set; } = TimeProvider.System;

        /// <summary>
        /// The certificate presented to clients. <c>null</c> means plaintext.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Null by default, which is the honest default for a LAN test run and for every test
        /// in this repository — but it is <b>not</b> an acceptable production setting (D-AD-6).
        /// The wire carries a password hash and a session token, and to this server the hash
        /// <i>is</i> the password, so anybody on the path who captures it can log in as that
        /// account. Client-side hashing protects the user's original secret, which they reuse
        /// elsewhere. It does nothing for this account. Only TLS does.
        /// </para>
        /// <para>
        /// The game server's connection is the same listener, so a certificate here also
        /// covers <c>GS_REGISTER</c> — which carries the shared secret in plaintext otherwise.
        /// </para>
        /// </remarks>
        public X509Certificate2? ServerCertificate { get; set; }
    }
}
