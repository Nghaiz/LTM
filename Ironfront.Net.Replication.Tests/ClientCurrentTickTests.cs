using System;
using System.IO;
using System.Linq;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Projectiles;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A client's "now" is the server's latest tick, not the tick it connected at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>v3.1.1 live, 2026-10-01.</b> <c>NetContext.CurrentTick</c> was written once on a client, in
    /// <c>NetClientBootstrap.OnConnected</c>, and never again. <see cref="ClientProjectileTracker"/>
    /// takes a 16-bit distance from it, so 2^15 ticks (18.2 minutes at 30 Hz) into a session every
    /// launch read as eighteen minutes old: both long matches that evening logged
    /// "grenade N ... Ignore, age 32640 ticks" from minute 18 on, and no client drew another grenade.
    /// </para>
    /// <para>
    /// The tracker's arithmetic is right, and the first test pins why the clock fed to it must run.
    /// The second is a source scan, for <c>SeatedDeathTests</c>' reason: the bootstrap is Unity code no
    /// test assembly compiles.
    /// </para>
    /// </remarks>
    public sealed class ClientCurrentTickTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        [Fact]
        public void ALaunchHalfAWrapAfterAFrozenClockIsDroppedAndAgainstTheLiveClockIsDrawn()
        {
            var config = new ProjectileConfig(
                20f, 3f, 100f, balanceDamage: 0f, impactForce: 0f,
                dropoffEnd: 0f, piercing: false, dropoffTable: Array.Empty<float>());
            var catalog = new ProjectileCatalog();
            catalog.Set(ProjectileKind.Bullet, in config);
            var tracker = new ClientProjectileTracker(catalog, Tick);

            // The figures from the 2026-10-01 Forest Lake log: connected at 384921, the launch
            // eighteen minutes and change later.
            const uint connectedAt = 384921;
            const uint launchedAt = connectedAt + 32790;

            ProjectileApplyResult frozen = tracker.Apply(Launch(5, launchedAt), connectedAt);
            Assert.Equal(ProjectileApplyAction.Ignore, frozen.Action);

            ProjectileApplyResult live = tracker.Apply(Launch(6, launchedAt), launchedAt + 2);
            Assert.Equal(ProjectileApplyAction.Spawn, live.Action);
            Assert.Equal(2, live.FastForwardedTicks);
        }

        [Fact]
        public void TheClientPublishesEverySnapshotTickAsItsCurrentTick()
        {
            SyntaxNode bootstrap = Parse("Net/Client/NetClientBootstrap.cs");

            MethodDeclarationSyntax applied = bootstrap.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "OnSnapshotApplied");

            Assert.Contains(applied.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "NetContext.CurrentTick=serverTick");
        }

        private static ProjectileSpawnMessage Launch(ushort id, uint tick)
            => new ProjectileSpawnMessage(
                id, 1, ProjectileKind.Bullet,
                Quantize.PackPos(0f), Quantize.PackPos(50f), Quantize.PackPos(0f),
                Quantize.PackVel16(0f), Quantize.PackVel16(0f), Quantize.PackVel16(20f),
                unchecked((ushort)tick),
                ProjectileSpawnMessage.PackRemainingLifetime(3f));

        private static string Normalized(SyntaxNode node)
            => string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));

        private static SyntaxNode Parse(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
