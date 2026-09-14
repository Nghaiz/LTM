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
    /// What the player is told when the master link dies, in the terms of the link that died.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fault these grade is a real one and it cost a play session.</b> The fly.io master
    /// carries a <c>tls</c> handler on both of its MSP ports, so a client with
    /// <c>IRONFRONT_CLIENT_MASTER_TLS=0</c> completes its TCP connect against the edge, reports
    /// itself connected, and then has its first MSP frame dropped for not being a TLS
    /// ClientHello. Every one of those deaths read as "Lost the connection to the master
    /// server." on a filled-in create-account form — the same sentence a genuine mid-session
    /// drop produces, with nothing naming the certificate the client was not presenting.
    /// Reproduced against the live master with the real <c>MasterClient</c>: plaintext to
    /// <c>:443</c> and to <c>:27000</c> both answer
    /// <c>IOException("Master connection closed.")</c> on the first request, and the same call
    /// over TLS registers an account.
    /// </para>
    /// <para>
    /// <b>Both directions are asserted, and that is the point of the file.</b> A message that
    /// blamed TLS whenever a link failed would be a worse lie than the generic one, because it
    /// would send an operator whose master merely restarted off to configure certificates. So
    /// "the master answered, then the link died" must keep the plain wording, and
    /// <see cref="TheTlsAdviceIsWithheldOnceTheMasterHasAnswered"/> is what stops the
    /// suspicion from widening into every failure.
    /// </para>
    /// </remarks>
    public sealed class MasterSessionLinkFailureTests
    {
        private const string PlainWording = "Lost the connection to the master server.";

        private const string SilentWording =
            "The master server closed the connection without answering.";

        private const string TlsWording =
            "The master server closed the connection without answering. A public master "
            + "expects TLS — set IRONFRONT_CLIENT_MASTER_TLS=1.";

        private sealed class Harness
        {
            public readonly FakeMasterClient Master = new FakeMasterClient();
            public readonly FakeTransportClient Game = new FakeTransportClient();
            public readonly GameFlowController Flow = new GameFlowController();
            public readonly MasterSession Session;

            public Harness()
            {
                Session = new MasterSession(Master, Flow, Game, _ => 1);
                Flow.Transition(GameFlowState.LoginScreen);
            }

            /// <summary>Dials the way <c>MenuScreenController</c> does, with or without TLS.</summary>
            public async Task<Harness> Dialled(bool tls)
            {
                Assert.True(await Session.ConnectAsync(
                    "master.example.net",
                    tls ? 443 : 27000,
                    tls ? new MasterClientTlsOptions { Enabled = true } : null));

                return this;
            }

            public void LinkDiesOnNextCall() =>
                Master.ThrowOnNextCall = new IOException("Master connection closed.");
        }

        /// <summary>
        /// A plaintext link that closes before the master has said anything names TLS.
        /// </summary>
        /// <remarks>
        /// This is the screenshot: username typed, password typed, CREATE ACCOUNT pressed, and
        /// a red line that did not name a cause. The three facts that make naming one safe are
        /// all asserted by the setup — we dialled the link, it is plaintext, and no call on it
        /// has been answered.
        /// </remarks>
        [Fact]
        public async Task APlaintextLinkThatDiesBeforeAnyAnswerNamesTls()
        {
            Harness h = await new Harness().Dialled(tls: false);
            h.LinkDiesOnNextCall();

            Assert.False(await h.Session.RegisterAsync("tester", "hunter2", string.Empty));

            Assert.Equal(TlsWording, h.Session.LastError);
        }

        /// <summary>
        /// Once the master has answered anything, the link is proven and the advice is dropped.
        /// </summary>
        /// <remarks>
        /// <b>The companion assertion.</b> Without it the suspicion has no upper bound and
        /// every later failure — a master restart, a laptop lid, a NAT rebind — would be
        /// reported as a TLS misconfiguration. A successful login is the cheapest proof that
        /// the transport is agreed, so anything after it gets the plain wording.
        /// </remarks>
        [Fact]
        public async Task TheTlsAdviceIsWithheldOnceTheMasterHasAnswered()
        {
            Harness h = await new Harness().Dialled(tls: false);

            Assert.True(await h.Session.LoginAsync("tester", "hunter2"));

            h.LinkDiesOnNextCall();
            Assert.False(await h.Session.RefreshRoomsAsync());

            Assert.Equal(PlainWording, h.Session.LastError);
        }

        /// <summary>
        /// A refusal is an answer, so a refused login also proves the transport.
        /// </summary>
        /// <remarks>
        /// The master reaches this client through <c>ErrorPush</c>, which faults the pending
        /// request as a <see cref="MasterServerException"/> rather than returning a result — a
        /// path that is easy to miss when marking "the link works", and one a player hits
        /// constantly, because a mistyped password is the most common thing that happens on
        /// this screen.
        /// </remarks>
        [Fact]
        public async Task ARefusedRequestCountsAsTheMasterAnswering()
        {
            Harness h = await new Harness().Dialled(tls: false);
            h.Master.ThrowOnNextCall = new MasterServerException(
                (int)ErrorCode.WrongCredentials, "Wrong username or password.");

            Assert.False(await h.Session.LoginAsync("tester", "wrong"));

            h.LinkDiesOnNextCall();
            Assert.False(await h.Session.RegisterAsync("tester", "hunter2", string.Empty));

            Assert.Equal(PlainWording, h.Session.LastError);
        }

        /// <summary>
        /// A TLS link that dies unanswered reports the silence without blaming TLS for it.
        /// </summary>
        /// <remarks>
        /// The client is already presenting a certificate, so "set TLS=1" would be advice it
        /// has taken. What is still worth saying is the observation itself: the master closed
        /// the link having answered nothing, which is a different fault from a drop mid-session
        /// and points at the master rather than at this client's transport.
        /// </remarks>
        [Fact]
        public async Task ATlsLinkThatDiesBeforeAnyAnswerReportsTheSilenceOnly()
        {
            Harness h = await new Harness().Dialled(tls: true);
            h.LinkDiesOnNextCall();

            Assert.False(await h.Session.RegisterAsync("tester", "hunter2", string.Empty));

            Assert.Equal(SilentWording, h.Session.LastError);
        }

        /// <summary>
        /// A session that never dialled keeps the plain wording.
        /// </summary>
        /// <remarks>
        /// <b>Why the flag is tri-state rather than a bool.</b> A harness — and the shell in a
        /// scene wired without a bootstrap — can drive this object without ever opening a link,
        /// and a two-state "is it TLS" would read that absence as "plaintext" and hand out
        /// transport advice about a socket nobody opened. The existing link-failure cases in
        /// <c>MasterSessionTests</c> and <c>MasterSessionRegisterTests</c> are exactly that
        /// shape, which is why they still assert the plain sentence.
        /// </remarks>
        [Fact]
        public async Task AnUndialledSessionKeepsThePlainWording()
        {
            var h = new Harness();
            h.LinkDiesOnNextCall();

            Assert.False(await h.Session.RegisterAsync("tester", "hunter2", string.Empty));

            Assert.Equal(PlainWording, h.Session.LastError);
        }
    }
}
