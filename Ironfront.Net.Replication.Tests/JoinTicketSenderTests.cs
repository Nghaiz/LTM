using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Issue #151 — a Unity client could never join a game server that had a shared secret
    /// configured, and the log blamed a signature rather than the absence of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was actually wrong, on both sides.</b> The client handed over
    /// <c>PendingJoin.CreateUnsignedTicket()</c> unconditionally — 64 zero bytes — on the
    /// argument that admitting them was the server's decision. It is, and the server decides no:
    /// <c>JoinTicket.Verify</c> returns <c>BadSignature</c> from exactly one branch, the HMAC
    /// compare, so a zero ticket can produce nothing else once a secret is set. Meanwhile the
    /// server's <c>IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS</c> flag was read only on the
    /// branch where the secret is MISSING, so an operator who set it got the opposite of what
    /// they asked for and nothing said so.
    /// </para>
    /// <para>
    /// <b>Half of this file is a source scan, and that is deliberate.</b> The two files that
    /// changed live under <c>Ironfront_Reborn/Assets/Scripts</c>, which no gate in this
    /// repository compiles — <c>dotnet build</c> reported 0 errors on a Unity layering violation
    /// during phase 3C and only the Editor caught it. Pinning only what executes here would
    /// leave a green that proves nothing about the two lines the bug was in, which is exactly
    /// the shape <c>green-that-proves-nothing</c> warns about. Same technique
    /// <c>ClientInputSenderTests</c> established.
    /// </para>
    /// </remarks>
    public class JoinTicketSenderTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-shared-secret");

        // ------------------------------------------------------------ the executable half

        /// <summary>
        /// A ticket minted from a client's own configuration verifies against the secret the
        /// server holds.
        /// </summary>
        /// <remarks>
        /// The whole of #151 in one assertion: before the fix this same call site produced a
        /// 64-byte zero ticket, and <see cref="TicketVerifyResult.BadSignature"/> was the only
        /// answer the server could give it.
        /// </remarks>
        [Fact]
        public void ATicketMintedFromClientConfigurationVerifies()
        {
            var config = new GameClientConfig();

            byte[] ticket = MintAsTheClientDoes(config);

            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now()));
        }

        /// <summary>
        /// The name reaches the other side intact, which is what makes a killfeed line readable.
        /// </summary>
        /// <remarks>
        /// Not cosmetic. <c>phase-3-harness.md</c> § 2 check 1 grades a killfeed line <b>with a
        /// name</b>, and the name travels in the ticket's <c>displayName</c> — a field only a
        /// signed ticket carries a meaningful value in. A server-only fix that merely admitted
        /// unsigned tickets would let Lane B connect and still leave that check unpassable.
        /// </remarks>
        [Fact]
        public void TheDisplayNameSurvivesTheRoundTrip()
        {
            var config = new GameClientConfig { PlayerId = 7, DisplayName = "lane-b-two" };

            byte[] ticket = MintAsTheClientDoes(config);

            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now()));
            Assert.True(JoinTicket.TryReadFields(
                ticket, out uint playerId, out _, out _, out _, out _, out string displayName));

            Assert.Equal(7u, playerId);
            Assert.Equal("lane-b-two", displayName);
        }

        /// <summary>
        /// Three clients configured with three ids present three distinct, individually valid
        /// tickets.
        /// </summary>
        /// <remarks>
        /// The server enforces one session per player once a secret is configured, so instances
        /// sharing an id have every join after the first rejected — and the rejection is
        /// reported as a bare <c>InvalidTicket</c>, which reads as a full server. Lane B runs
        /// three clients (two observers and a driver, for check 7), so this is the exact case.
        /// <c>JoinTicketSource.Mint</c> makes the same argument one project over.
        /// </remarks>
        [Fact]
        public void ThreeConfiguredClientsMintThreeDistinctValidTickets()
        {
            uint[] ids = Enumerable.Range(1, 3)
                .Select(i => MintAsTheClientDoes(
                    new GameClientConfig { PlayerId = (uint)i, DisplayName = $"lane-b-{i}" }))
                .Select(ticket =>
                {
                    Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now()));
                    Assert.True(JoinTicket.TryReadFields(
                        ticket, out uint playerId, out _, out _, out _, out _, out _));
                    return playerId;
                })
                .ToArray();

            Assert.Equal(3, ids.Distinct().Count());
            Assert.DoesNotContain(0u, ids);
        }

        /// <summary>The two variables the runner sets per instance actually land on the config.</summary>
        [Fact]
        public void TheEnvironmentSuppliesTheIdAndTheName()
        {
            var read = new Dictionary<string, string?>
            {
                [EnvRegistry.ClientPlayerId.Name]     = "3",
                [EnvRegistry.ClientDisplayName.Name]  = "lane-b-three",
            };

            GameClientConfig config = new GameClientConfig()
                .ApplyEnvironment(name => read.TryGetValue(name, out string? v) ? v : null);

            Assert.Equal(3u, config.PlayerId);
            Assert.Equal("lane-b-three", config.DisplayName);
        }

        /// <summary>
        /// The default id stays out of the range the load harness numbers its clients from.
        /// </summary>
        /// <remarks>
        /// It used to be 1, and <c>JoinTicketSource.Mint</c> gives its first synthetic client
        /// <c>clientIndex + 1</c> = 1 — so the shipped default collided with the shipped harness
        /// by construction. The first two-client run against a real server lost a client to
        /// <c>AlreadyConnected</c> for exactly that reason, and Lane B runs three rendered
        /// clients that would all have claimed 1 together.
        /// </remarks>
        [Fact]
        public void TheDefaultPlayerIdAvoidsTheHarnessRange()
        {
            var config = new GameClientConfig();

            Assert.True(config.PlayerId > GameClientConfig.ReservedIdCeiling,
                $"default playerId {config.PlayerId} is inside the reserved range "
                + $"(<= {GameClientConfig.ReservedIdCeiling}), where the load harness numbers "
                + "its synthetic clients from. A collision there reads as a full server.");

            Assert.NotEqual(0u, config.PlayerId);
        }

        /// <summary>
        /// A configured id of 0 is refused where it is read, not three layers away at the join.
        /// </summary>
        /// <remarks>
        /// 0 is the one value the server's one-session-per-player claim cannot represent, and a
        /// client that shipped it would be told <c>InvalidTicket</c> with nothing naming the
        /// cause.
        /// </remarks>
        [Fact]
        public void APlayerIdOfZeroIsRefusedAtParseTime()
        {
            var read = new Dictionary<string, string?> { [EnvRegistry.ClientPlayerId.Name] = "0" };

            Assert.ThrowsAny<Exception>(() => new GameClientConfig()
                .ApplyEnvironment(name => read.TryGetValue(name, out string? v) ? v : null));
        }

        // ------------------------------------------------------------ the source-scan half

        /// <summary>
        /// <c>NetClientBootstrap</c> mints a signed ticket rather than handing over zeroes.
        /// </summary>
        /// <remarks>
        /// The <c>wired-not-just-present</c> half. Every assertion above would have passed on the
        /// day #151 was filed: <c>JoinTicket.Issue</c> worked perfectly and nothing on the Unity
        /// client called it.
        /// </remarks>
        [Fact]
        public void TheClientBootstrapMintsASignedTicket()
        {
            ISet<string> invoked = InvokedNames(UnitySource("Net/Client/NetClientBootstrap.cs"));

            Assert.Contains("Issue", invoked);
        }

        /// <summary>
        /// And it still keeps the unsigned path for a server with no secret.
        /// </summary>
        /// <remarks>
        /// The companion direction, per <c>pinned-baseline-test-companion</c>: a fix that
        /// deleted the placeholder outright would break every existing development flow that
        /// runs with no secret and <c>ACCEPT_UNSIGNED_TICKETS=1</c>, and no assertion above
        /// would have noticed.
        /// </remarks>
        [Fact]
        public void TheClientBootstrapStillHasAnUnsignedPath()
        {
            ISet<string> invoked = InvokedNames(UnitySource("Net/Client/NetClientBootstrap.cs"));

            Assert.Contains("CreateUnsignedTicket", invoked);
        }

        /// <summary>
        /// The mint is guarded by the secret's presence, not run unconditionally.
        /// </summary>
        /// <remarks>
        /// Reading <c>EnvRegistry.SharedSecret.Name</c> is what distinguishes "mints when it
        /// can" from "always mints" — the latter would throw on every no-secret run, converting
        /// #151 into its mirror image.
        /// </remarks>
        [Fact]
        public void TheMintIsConditionalOnASecretBeingReachable()
        {
            string source = UnitySource("Net/Client/NetClientBootstrap.cs").ToString();

            Assert.Contains("SharedSecret", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The server says out loud that it is ignoring the accept-unsigned flag.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The flag stays ignored — a server holding a real secret admitting unsigned tickets is
        /// a server anyone can join as anyone, and that method's contract is fail-closed. What
        /// this pins is that the contradiction is REPORTED. It was silent, and the only evidence
        /// was a per-connection <c>BadSignature</c> naming the symptom instead of the cause.
        /// </para>
        /// <para>
        /// <c>LogError</c> and not <c>LogWarning</c>: a warning in a start-up log is scrolled
        /// past, which is how this survived long enough to consume phase 3B.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheServerReportsAnIgnoredAcceptUnsignedFlag()
        {
            SyntaxNode server = UnitySource("Net/Server/NetServerBootstrap.cs");

            bool reported = server.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == "LogError")
                .Any(i => i.ArgumentList.ToString()
                    .Contains("GameServerAcceptUnsignedTickets", StringComparison.Ordinal));

            Assert.True(reported,
                "NetServerBootstrap does not LogError when a shared secret makes "
                + "IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS inert, so an operator who sets "
                + "the flag still gets the opposite of what they asked for in silence. #151.");
        }

        // ------------------------------------------------------------------------ helpers

        /// <summary>
        /// Mints exactly as <c>NetClientBootstrap.BuildJoinTicket</c> does.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately not a call into the Unity bootstrap</b>, which no gate here compiles.
        /// The parameters come off <see cref="GameClientConfig"/> — the same object the Unity
        /// side is pinned above to read — so what this grades is the shape of the ticket the
        /// client presents, while the source scans grade that the client presents it at all.
        /// </remarks>
        private static byte[] MintAsTheClientDoes(GameClientConfig config)
        {
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            int written = JoinTicket.Issue(
                ticket,
                playerId: config.PlayerId,
                serverId: 0,
                roomId: 0,
                expiresAtUnixMs: Now() + JoinTicket.ValidityMs,
                team: 0,
                displayName: config.DisplayName,
                sharedSecret: Secret);

            Assert.Equal(ProtocolConstants.JOIN_TICKET_SIZE, written);

            return ticket;
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static ISet<string> InvokedNames(SyntaxNode root)
            => root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression is MemberAccessExpressionSyntax member
                    ? member.Name.Identifier.ValueText
                    : i.Expression.ToString())
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// Parses one Unity source file, relative to <c>Assets/Scripts</c>. A missing file FAILS
        /// rather than reporting an empty scan.
        /// </summary>
        private static SyntaxNode UnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
