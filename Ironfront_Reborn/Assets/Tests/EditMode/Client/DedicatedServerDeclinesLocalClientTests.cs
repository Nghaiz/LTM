using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The half of AD-1 a test assembly can reach: what
    /// <see cref="NetContext.IsDedicatedServer"/> means, and that it is not
    /// <see cref="NetContext.IsServer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The behaviour itself is pinned by gate rule G11, not here, and that was measured rather
    /// than assumed.</b> The thing worth pinning is that <c>NetClientBootstrap.Awake</c> returns
    /// early on a dedicated server. This fixture first tried to prove it the obvious way — build
    /// the component on an inactive <c>GameObject</c>, install a fake transport, activate — and
    /// it reported PASS while proving nothing: Unity does not run <c>Awake</c> outside play mode,
    /// so nothing ever executed. <c>NetServerActorSeamTests</c> had already written that down
    /// (<c>"Unity does not run Awake on AddComponent outside play mode"</c>) and this fixture
    /// re-discovered it at cost. What caught it was the control case — "an ordinary client still
    /// dials" — failing in the opposite direction, which is exactly the both-directions
    /// discipline <c>pinned-baseline-test-companion.md</c> asks for and the reason it was
    /// written. A single-direction fixture here would have shipped green and empty.
    /// </para>
    /// <para>
    /// <b>What went wrong, so a later reader knows what G11 is protecting.</b> Every map scene
    /// carries an active <c>NetServer</c> AND an active <c>NetClient</c>, so any process that
    /// loads one is a listen server. The lane-B harness strips the half it is not; the shipped
    /// dedicated server stripped nothing, so on the first deployment whose log anybody read it
    /// logged <c>[net] conn 1 joined as actor 41 (127.0.0.1:59244)</c> — the server had joined
    /// its own match, holding one of sixteen player slots and one connection, with the congestion
    /// controller reacting to its own loopback traffic.
    /// </para>
    /// </remarks>
    public sealed class DedicatedServerDeclinesLocalClientTests
    {
        private NetRole _role;
        private bool _dedicated;

        [SetUp]
        public void CaptureGlobals()
        {
            _role = NetContext.Role;
            _dedicated = NetContext.IsDedicatedServer;
        }

        [TearDown]
        public void RestoreGlobals()
        {
            NetContext.Clear();
            NetContext.SetRole(_role);
            if (_dedicated) NetContext.DeclareDedicatedServer();
        }

        [Test]
        public void TheFlagIsOffUntilSomethingDeclaresIt()
        {
            NetContext.Clear();

            Assert.That(NetContext.IsDedicatedServer, Is.False,
                "a process is not a dedicated server until the one caller that knows says so; "
                + "defaulting to true would silence the client on every machine.");
        }

        [Test]
        public void DeclaringItSurvivesUntilCleared()
        {
            NetContext.Clear();
            NetContext.DeclareDedicatedServer();

            Assert.That(NetContext.IsDedicatedServer, Is.True);

            NetContext.Clear();

            Assert.That(NetContext.IsDedicatedServer, Is.False,
                "Clear is the teardown path every fixture uses; leaving the flag set would leak a "
                + "dedicated-server identity into the next test and silence a client that should "
                + "dial.");
        }

        /// <summary>
        /// The distinction the whole fix rests on. If these two ever became synonyms, the guard in
        /// <c>Awake</c> would start depending on which bootstrap's <c>Awake</c> ran first.
        /// </summary>
        [Test]
        public void TheServerROLEDoesNotMakeTheProcessDedicated()
        {
            NetContext.Clear();
            NetContext.SetRole(NetRole.Server);

            Assert.That(NetContext.IsServer, Is.True, "precondition");
            Assert.That(NetContext.IsDedicatedServer, Is.False,
                "the ROLE is settled by whichever of the two bootstraps wakes first, since each "
                + "defers to the other -- so reading it as 'this process hosts' would make an "
                + "Editor Play session's behaviour depend on component order, which is the race "
                + "X-9 closed.");
        }

        /// <summary>And the mirror: declaring the process does not silently claim the role.</summary>
        [Test]
        public void DeclaringTheProcessDoesNotClaimTheRole()
        {
            NetContext.Clear();
            NetContext.DeclareDedicatedServer();

            Assert.That(NetContext.Role, Is.EqualTo(NetRole.Offline),
                "the declaration says what the PROCESS is, not what has started; NetServerBootstrap "
                + "still claims the role when it wakes, and a flag that pre-claimed it would make "
                + "the client's own deferral line read a role nothing had established yet.");
        }
    }
}
