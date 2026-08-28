using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The half of X-52 a test assembly can reach: what
    /// <see cref="NetContext.IsDeclaredClient"/> means, and that it is not
    /// <see cref="NetContext.IsClient"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The behaviour itself is pinned by gate rule G14, not here, and the reason is the same
    /// one <c>DedicatedServerDeclinesLocalClientTests</c> paid for.</b> The thing worth pinning
    /// is that <c>NetServerBootstrap.Awake</c> returns before it starts a server on a declared
    /// client — and Unity does not run <c>Awake</c> on <c>AddComponent</c> outside play mode, so
    /// a fixture built around one reports PASS while executing nothing. That fixture is not
    /// re-written here; the gate grades the shipped file directly
    /// (<c>ClientWiringGateTests.TheShippedServerBootstrapIsGuarded</c>) and cannot go vacuously
    /// green, because its own red paths run on every <c>dotnet test</c>.
    /// </para>
    /// <para>
    /// <b>What went wrong, so a later reader knows what G14 is protecting.</b> X-10 gave a
    /// shipped process a way to declare itself a client, and <c>NetServerBootstrap</c> read that
    /// declaration — but only to decline to CLAIM the role, never to decline to START. So
    /// <c>tools/play-lan.ps1</c> launched two human clients that each logged
    /// <c>[net] role = Client</c> and then hosted a full sixteen-slot authority anyway. The first
    /// took UDP 27015 and reported <c>16 player slots will not fit: 51 actors are already
    /// registered</c>; the second threw an unhandled <c>SocketException</c> out of <c>Awake</c>
    /// because the first held the port. <c>architecture.md</c> AD-1 (<i>"server-authoritative, no
    /// host/listen-server"</i>) forbids both halves; until X-50 and this row, nothing enforced
    /// either.
    /// </para>
    /// </remarks>
    public sealed class DeclaredClientDeclinesLocalServerTests
    {
        private NetRole _role;
        private bool _dedicated;
        private bool _declaredClient;

        [SetUp]
        public void CaptureGlobals()
        {
            _role = NetContext.Role;
            _dedicated = NetContext.IsDedicatedServer;
            _declaredClient = NetContext.IsDeclaredClient;
        }

        [TearDown]
        public void RestoreGlobals()
        {
            NetContext.Clear();
            NetContext.SetRole(_role);
            if (_dedicated) NetContext.DeclareDedicatedServer();
            if (_declaredClient) NetContext.DeclareClientProcess();
        }

        [Test]
        public void TheFlagIsOffUntilSomethingDeclaresIt()
        {
            NetContext.Clear();

            Assert.That(NetContext.IsDeclaredClient, Is.False,
                "a process is not a declared client until something that knows says so; "
                + "defaulting to true would stop the Editor sandbox and offline single-player "
                + "hosting at all, which is the no-declaration default X-10 deliberately kept.");
        }

        [Test]
        public void DeclaringItSurvivesUntilCleared()
        {
            NetContext.Clear();
            NetContext.DeclareClientProcess();

            Assert.That(NetContext.IsDeclaredClient, Is.True);

            NetContext.Clear();

            Assert.That(NetContext.IsDeclaredClient, Is.False,
                "Clear is the teardown path every fixture uses; leaving the flag set would leak a "
                + "client identity into the next test and silence a server that should host.");
        }

        /// <summary>
        /// The distinction the whole fix rests on. If these two ever became synonyms, the guard in
        /// <c>Awake</c> would start depending on which bootstrap's <c>Awake</c> ran first.
        /// </summary>
        [Test]
        public void TheClientROLEDoesNotMakeTheProcessADeclaredClient()
        {
            NetContext.Clear();
            NetContext.SetRole(NetRole.Client);

            Assert.That(NetContext.IsClient, Is.True, "precondition");
            Assert.That(NetContext.IsDeclaredClient, Is.False,
                "the ROLE is settled by whichever of the two bootstraps wakes first, since each "
                + "defers to the other -- so reading it as 'this process was launched to join' "
                + "would make an Editor Play session stop hosting depending on component order, "
                + "which is the race X-9 closed.");
        }

        /// <summary>And the mirror: declaring the process does not silently claim the role.</summary>
        [Test]
        public void DeclaringTheProcessDoesNotClaimTheRole()
        {
            NetContext.Clear();
            NetContext.DeclareClientProcess();

            Assert.That(NetContext.Role, Is.EqualTo(NetRole.Offline),
                "the declaration says what the PROCESS is, not what has started; NetClientBootstrap "
                + "still claims the role when it wakes, and a flag that pre-claimed it would make "
                + "the server's own deferral line read a role nothing had established yet.");
        }

        /// <summary>
        /// The two declarations are independent, and a process is never both. Asserted rather
        /// than assumed because <c>Clear</c> resets them together and a copy-paste that reset
        /// only one would leak whichever was forgotten into the next fixture.
        /// </summary>
        [Test]
        public void TheTwoProcessDeclarationsAreIndependent()
        {
            NetContext.Clear();
            NetContext.DeclareClientProcess();

            Assert.That(NetContext.IsDedicatedServer, Is.False,
                "declaring a client must not imply a dedicated server; NetClientBootstrap reads "
                + "that flag to decline to dial, and a client that stopped dialling would join "
                + "nothing at all.");

            NetContext.Clear();
            NetContext.DeclareDedicatedServer();

            Assert.That(NetContext.IsDeclaredClient, Is.False,
                "and the mirror: a dedicated server that read as a declared client would decline "
                + "to start the very server it was launched to run.");
        }
    }
}
