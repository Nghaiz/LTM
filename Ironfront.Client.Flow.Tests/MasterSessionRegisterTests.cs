using System;
using System.IO;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// <c>MasterSession.RegisterAsync</c>: the wrapper P15 3.1 adds, and the hashing agreement
    /// criterion 4 grades.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own file rather than more cases in <c>MasterSessionTests</c>,</b> because the
    /// question here is not "does register work" but "do register and login agree", and that is
    /// a relationship between two calls rather than a property of either. Putting it beside the
    /// login cases invites the two to be read as independent, which is the exact mistake risk
    /// 3 in the phase's table describes: an account created that cannot log in, with both halves
    /// individually passing.
    /// </para>
    /// </remarks>
    public sealed class MasterSessionRegisterTests
    {
        private sealed class Harness
        {
            public readonly FakeMasterClient Master = new FakeMasterClient();
            public readonly FakeTransportClient Game = new FakeTransportClient();
            public readonly GameFlowController Flow = new GameFlowController();
            public readonly MasterSession Session;

            public Harness()
            {
                Session = new MasterSession(Master, Flow, Game, _ => 1);
            }

            /// <summary>Where a player registering actually is: on the login screen.</summary>
            public Harness AtLoginScreen()
            {
                Flow.Transition(GameFlowState.LoginScreen);
                return this;
            }
        }

        // ------------------------------------------------------------------ criterion 4

        /// <summary>
        /// The hash register sends is byte-identical to the one login sends for the same
        /// credentials. Criterion 4, and the mitigation for risk 3.
        /// </summary>
        /// <remarks>
        /// <b>Asserted as an equality between the two calls, not against a literal.</b> A test
        /// pinning each call's output to an expected hex string would keep passing if BOTH were
        /// changed together to something the master does not accept — it would be grading this
        /// test's copy of the algorithm rather than the two call sites' agreement. Comparing them
        /// to each other cannot be satisfied that way: the only way to make it pass is to use the
        /// same function, which is the whole requirement.
        /// </remarks>
        [Fact]
        public async Task RegisterAndLoginSendTheSameHashForTheSameCredentials()
        {
            var h = new Harness().AtLoginScreen();

            await h.Session.RegisterAsync("Tester", "hunter2", "Tester");
            await h.Session.LoginAsync("Tester", "hunter2");

            Assert.NotNull(h.Master.LastRegisterPasswordHash);
            Assert.Equal(h.Master.LastPasswordHash, h.Master.LastRegisterPasswordHash);
        }

        /// <summary>
        /// The salt is the username, so the same password under two usernames does not produce
        /// the same bytes.
        /// </summary>
        /// <remarks>
        /// The companion to the test above: equality alone is also satisfied by a register and a
        /// login that both hash to a constant. This pins that the value actually varies with the
        /// input the hasher claims to salt with.
        /// </remarks>
        [Fact]
        public async Task TheRegisterHashIsSaltedWithTheUsername()
        {
            var h = new Harness().AtLoginScreen();

            await h.Session.RegisterAsync("alice", "hunter2", string.Empty);
            string? alice = h.Master.LastRegisterPasswordHash;

            await h.Session.RegisterAsync("bob", "hunter2", string.Empty);
            string? bob = h.Master.LastRegisterPasswordHash;

            Assert.NotNull(alice);
            Assert.NotEqual(alice, bob);
        }

        /// <summary>
        /// The username is lowercased before salting, so a capitalised login still matches an
        /// account registered in lower case.
        /// </summary>
        /// <remarks>
        /// <c>PasswordHasher</c> lowercases because protocol-spec.md § 13 defines a username as
        /// <c>a-z0-9_</c>. This asserts the register path inherits that rather than re-deriving
        /// it — the failure it forbids is "registered as tester, typed Tester, wrong password".
        /// </remarks>
        [Fact]
        public async Task CasingOfTheUsernameDoesNotChangeTheHash()
        {
            var h = new Harness().AtLoginScreen();

            await h.Session.RegisterAsync("tester", "hunter2", string.Empty);
            string? lower = h.Master.LastRegisterPasswordHash;

            await h.Session.RegisterAsync("TESTER", "hunter2", string.Empty);
            string? upper = h.Master.LastRegisterPasswordHash;

            Assert.Equal(lower, upper);
        }

        /// <summary>The plaintext password never reaches the master client.</summary>
        [Fact]
        public async Task ThePlaintextPasswordIsNeverSent()
        {
            var h = new Harness().AtLoginScreen();

            await h.Session.RegisterAsync("tester", "hunter2", string.Empty);

            Assert.NotEqual("hunter2", h.Master.LastRegisterPasswordHash);
            Assert.DoesNotContain("hunter2", h.Master.LastRegisterPasswordHash);
        }

        // ------------------------------------------------------------------ the flow contract

        /// <summary>
        /// A successful register does not move the flow and does not log the player in. 3.1's
        /// recorded answer.
        /// </summary>
        /// <remarks>
        /// Both halves matter. Not moving is what makes the register screen a sub-view of
        /// <c>LoginScreen</c> rather than an eleventh state; not logging in is the post-condition
        /// a caller is most likely to assume the other way, because a register response carries
        /// no session token for <c>IsLoggedIn</c> to read.
        /// </remarks>
        [Fact]
        public async Task ASuccessfulRegisterStaysOnTheLoginScreenAndDoesNotLogIn()
        {
            var h = new Harness().AtLoginScreen();

            bool ok = await h.Session.RegisterAsync("tester", "hunter2", "Tester");

            Assert.True(ok);
            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.False(h.Session.IsLoggedIn);
        }

        /// <summary>The display name is forwarded, and blank is forwarded as blank.</summary>
        /// <remarks>
        /// The blank case is the one worth pinning: the client deliberately does NOT substitute
        /// the username, so that a master which decides otherwise and this client cannot disagree
        /// about the player's own name.
        /// </remarks>
        [Fact]
        public async Task TheDisplayNameIsForwardedVerbatimIncludingBlank()
        {
            var h = new Harness().AtLoginScreen();

            await h.Session.RegisterAsync("tester", "hunter2", "Ace");
            Assert.Equal("Ace", h.Master.LastRegisterDisplayName);

            await h.Session.RegisterAsync("tester", "hunter2");
            Assert.Equal(string.Empty, h.Master.LastRegisterDisplayName);
        }

        // ------------------------------------------------------------------ failures

        /// <summary>
        /// A refused register reports the master's own sentence and leaves the flow alone.
        /// </summary>
        /// <remarks>
        /// <c>UsernameTaken</c> is the code a player actually meets, and the text is
        /// <c>MasterErrorText</c>'s — asserted against that function rather than a literal, so a
        /// reworded table stays green and a register that stopped consulting the table does not.
        /// </remarks>
        [Fact]
        public async Task ARefusedRegisterReportsTheMastersReasonAndDoesNotMoveTheFlow()
        {
            var h = new Harness().AtLoginScreen();
            h.Master.NextRegister = new RegisterResult(false, (int)ErrorCode.UsernameTaken);

            string? raised = null;
            h.Session.OnError += message => raised = message;

            bool ok = await h.Session.RegisterAsync("tester", "hunter2", string.Empty);

            Assert.False(ok);
            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.Equal(MasterErrorText.Describe(ErrorCode.UsernameTaken), h.Session.LastError);
            Assert.Equal(h.Session.LastError, raised);
        }

        /// <summary>
        /// An <c>ok=false</c> with no code does not render as "OK." on the register screen.
        /// </summary>
        /// <remarks>
        /// The reason <c>DescribeFailure</c> exists rather than <c>Describe</c>: code 0 means
        /// success in the table, and a master answering false without filling the code in would
        /// otherwise put the word "OK." in red under a form that just failed.
        /// </remarks>
        [Fact]
        public async Task ARefusalWithNoCodeDoesNotSayOk()
        {
            var h = new Harness().AtLoginScreen();
            h.Master.NextRegister = new RegisterResult(false, (int)ErrorCode.Ok);

            await h.Session.RegisterAsync("tester", "hunter2", string.Empty);

            Assert.Equal(MasterErrorText.Unknown, h.Session.LastError);
        }

        /// <summary>A thrown <c>MasterServerException</c> is described, not propagated.</summary>
        [Fact]
        public async Task AThrownMasterErrorIsDescribedRatherThanEscaping()
        {
            var h = new Harness().AtLoginScreen();
            h.Master.ThrowOnNextCall = new MasterServerException((int)ErrorCode.RateLimited, "slow down");

            bool ok = await h.Session.RegisterAsync("tester", "hunter2", string.Empty);

            Assert.False(ok);
            Assert.Equal(MasterErrorText.Describe(ErrorCode.RateLimited), h.Session.LastError);
        }

        /// <summary>
        /// A dead link is reported as a lost connection rather than escaping to the UI callback.
        /// </summary>
        /// <remarks>
        /// The register form calls this from a Unity button handler, which is <c>async void</c>:
        /// an escaping exception there is not caught by Unity's handler and takes the frame down.
        /// So the link-failure catch is not tidiness, it is the difference between an error line
        /// and a dead menu.
        /// </remarks>
        [Fact]
        public async Task ADeadLinkIsReportedRatherThanThrown()
        {
            var h = new Harness().AtLoginScreen();
            h.Master.ThrowOnNextCall = new IOException("socket closed");

            bool ok = await h.Session.RegisterAsync("tester", "hunter2", string.Empty);

            Assert.False(ok);
            Assert.Equal("Lost the connection to the master server.", h.Session.LastError);
        }

        /// <summary>
        /// A register that succeeds clears a previous failure's error line.
        /// </summary>
        /// <remarks>
        /// Without this the "that username is already taken" from the first attempt is still on
        /// screen after the second one worked, which reads as the account not having been
        /// created.
        /// </remarks>
        [Fact]
        public async Task ASuccessfulRegisterClearsTheEarlierError()
        {
            var h = new Harness().AtLoginScreen();

            h.Master.NextRegister = new RegisterResult(false, (int)ErrorCode.UsernameTaken);
            await h.Session.RegisterAsync("tester", "hunter2", string.Empty);
            Assert.NotEqual(string.Empty, h.Session.LastError);

            h.Master.NextRegister = new RegisterResult(true, 0);
            await h.Session.RegisterAsync("tester2", "hunter2", string.Empty);

            Assert.Equal(string.Empty, h.Session.LastError);
        }
    }
}
