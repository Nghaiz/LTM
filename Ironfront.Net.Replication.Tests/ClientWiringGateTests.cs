using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Tools.ClientWiringGate;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V10 task 11 — the gate's own red paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A check nobody has watched fail is unproven.</b> The client-wiring gate exists because
    /// six of nine router events reached production with no subscriber; if the gate itself can
    /// only ever report green, it has replaced one silent hole with a louder one. So the
    /// detectors are pure functions over a parsed tree, and every rule is exercised here against
    /// a fixture that MUST be reported — the failing direction, on every CI run, not just the
    /// passing one.
    /// </para>
    /// <para>
    /// The fixtures are strings rather than files on purpose: a fixture on disk under
    /// <c>Assets/Scripts</c> would be scanned by the real gate and would fail it.
    /// </para>
    /// </remarks>
    public sealed class ClientWiringGateTests
    {
        private const string ProductionPath = "Ironfront_Reborn/Assets/Scripts/Net/Client/Presenter.cs";
        private const string ActorPath = "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs";
        private const string CapturePointPath =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/CapturePoint.cs";

        [Fact]
        public void TheGateFindsASubscriptionInAFixture()
        {
            ISet<string> subscribed = Subscriptions(
                @"class P { void E(R r) { r.OnDeath += H; } void H(D d) { } }", ProductionPath);

            Assert.Contains("OnDeath", subscribed);
        }

        [Fact]
        public void TheGateReportsAnUnsubscribedEventInAFixture()
        {
            // The red path, exercised every run: a file that merely NAMES the event in a
            // declaration is not a subscriber. This is the exact shape all six dead events had.
            ISet<string> subscribed = Subscriptions(
                @"class R { public event System.Action<int> OnHitConfirm; }", ProductionPath);

            Assert.DoesNotContain("OnHitConfirm", subscribed);
        }

        [Fact]
        public void TheGateIgnoresACommentedOutSubscription()
        {
            // The false green Roslyn exists to close. A naive `grep '+='` would call this wired,
            // and the gate would then certify a dead event as live — worse than no gate.
            ISet<string> subscribed = Subscriptions(
                @"class P { void E(R r) { /* r.OnWeaponFire += H; */ } }", ProductionPath);

            Assert.Empty(subscribed);
        }

        [Fact]
        public void TheGateIgnoresAPreprocessorDisabledSubscription()
        {
            // Same failure, second shape: a subscription inside a false #if is not compiled and
            // must not count. Roslyn drops it from the tree; a text scan would not.
            ISet<string> subscribed = Subscriptions(
                "class P { void E(R r) {\n#if NEVER_DEFINED\n r.OnExplosion += H;\n#endif\n } }",
                ProductionPath);

            Assert.Empty(subscribed);
        }

        [Fact]
        public void TheGateIgnoresATestFileSubscription()
        {
            // Load-bearing, not tidy: ClientCombatTests.cs subscribes OnHitConfirm, OnDeath and
            // OnWeaponFire today. Counting them would report three of the dead events as wired.
            const string body = @"class T { void E(R r) { r.OnDeath += H; } }";

            Assert.True(ClientWiringDetectors.IsExcludedFromScan("Some/Path/FooTests.cs"));
            Assert.Empty(Subscriptions(body, "Some/Path/FooTests.cs"));

            // And the same text at a production path still counts, so the exclusion is doing the
            // discriminating rather than the fixture being unparseable.
            Assert.Contains("OnDeath", Subscriptions(body, ProductionPath));
        }

        [Fact]
        public void TheGateIgnoresTheRoutersOwnDeclarations()
        {
            Assert.True(ClientWiringDetectors.IsExcludedFromScan(
                "Ironfront.Net.Replication/Client/ClientMessageRouter.cs"));
        }

        [Fact]
        public void TheGateFlagsAnUnguardedLocalSingletonTouch()
        {
            // G4's red path — recorded finding A16 in miniature: a per-actor method writing the
            // local HUD with no identity check, which is how a remote player's damage moved
            // YOUR health bar.
            IReadOnlyList<GateFinding> findings = ClientWiringDetectors
                .FindUnguardedLocalSingletonTouches(
                    Parse(@"class Actor { void Hurt() { IngameUi.instance.SetHealth(1f); } }", ActorPath),
                    ActorPath);

            GateFinding finding = Assert.Single(findings);
            Assert.Equal("G4", finding.RuleId);
        }

        [Fact]
        public void TheGateAcceptsAGuardedLocalSingletonTouch()
        {
            // The green twin. Without it, G4 could be passing by flagging everything — and a
            // rule that fires on correct code is a rule people delete.
            Assert.Empty(ClientWiringDetectors.FindUnguardedLocalSingletonTouches(
                Parse(
                    @"class Actor { void Hurt() { if (NetClientPresenterGuard.IsLocalActor(this))"
                    + @" { IngameUi.instance.SetHealth(1f); } } }",
                    ActorPath),
                ActorPath));

            Assert.Empty(ClientWiringDetectors.FindUnguardedLocalSingletonTouches(
                Parse(
                    @"class Actor { void Hide() { if (!NetClientPresenterGuard.IsLocalActor(this))"
                    + @" return; IngameUi.instance.Hide(); } }",
                    ActorPath),
                ActorPath));
        }

        [Fact]
        public void TheGateFlagsAnEmptyCatch()
        {
            // G3's red path. The real one swallowed a NullReferenceException on the objectives
            // path for long enough that somebody wrapped exactly one call in it.
            IReadOnlyList<GateFinding> findings = ClientWiringDetectors.FindEmptyCatchClauses(
                Parse(
                    @"class CapturePoint { void S() { try { M(); } catch (System.Exception) { } } }",
                    CapturePointPath),
                CapturePointPath);

            GateFinding finding = Assert.Single(findings);
            Assert.Equal("G3", finding.RuleId);
        }

        [Fact]
        public void TheGateFlagsACosmeticsPathReachingDamage()
        {
            // G2's red path (D7). SpawnProjectile sets `source = user`, so a cosmetics presenter
            // that reached it would do REAL DAMAGE from a client drawing a muzzle flash.
            IReadOnlyList<GateFinding> findings = ClientWiringDetectors
                .FindClientDamagePathReferences(
                    Parse(@"class P { void F(Weapon w) { w.SpawnProjectile(dir); } }", ProductionPath),
                    ProductionPath);

            GateFinding finding = Assert.Single(findings);
            Assert.Equal("G2", finding.RuleId);
        }

        [Fact]
        public void TheHudNeverRoutesTicketsThroughAddScore()
        {
            // G5's red path (D11). AddScore and AddFlag are delta-only with no getters, so
            // feeding the server's authoritative TOTALS through them re-enters ScoreMultiplier
            // and drives the victory check a second time. The tempting refactor — "ScoreUi
            // already has AddScore, just call that" — reads as tidying up, which is exactly why
            // it fails the build rather than a review.
            IReadOnlyList<GateFinding> findings = ClientWiringDetectors.FindDeltaScoreReferences(
                Parse(@"class P { void U() { ScoreUi.AddScore(1, 0); } }", ProductionPath),
                ProductionPath);

            GateFinding finding = Assert.Single(findings);
            Assert.Equal("G5", finding.RuleId);

            // The green twin: the authoritative entry point is the sanctioned route.
            Assert.Empty(ClientWiringDetectors.FindDeltaScoreReferences(
                Parse(
                    @"class P { void U() { ScoreUi.SetAuthoritativeState(2, 300, 300, -1, 8); } }",
                    ProductionPath),
                ProductionPath));
        }

        [Fact]
        public void TheGateFailsWhenItScansZeroFiles()
        {
            // A gate that passes because it looked at nothing is worse than no gate: it reports
            // green forever from the wrong working directory. 2 means "cannot tell", not "clean".
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = GateRunner.Run(
                GateRunner.RouterEventNames(), Array.Empty<string>(), output, error);

            Assert.Equal(2, exit);
        }

        [Fact]
        public void TheGateEnumeratesEveryRouterEvent()
        {
            // The count is asserted so a renamed or deleted event changes the gate's input
            // loudly rather than shrinking its coverage silently.
            IReadOnlyList<string> names = GateRunner.RouterEventNames();

            Assert.Equal(GateRunner.ExpectedRouterEventCount, names.Count);
            Assert.Contains("OnDeath", names);
            Assert.Contains("OnCapturePoint", names);
            Assert.Contains("OnSnapshotApplied", names);
        }

        [Fact]
        public void PerActorGuardExemptions_HasNoStaleEntries()
        {
            // The companion pinned-baseline-test-companion.md asks for. Each G4 exemption is a
            // stored judgement about code that moves underneath it: rename the file, delete the
            // member, or refactor the singleton touch away, and the entry keeps suppressing
            // nothing while still reading as deliberate. Nothing else in the suite would notice.
            //
            // Asserted by IDENTITY, not by count - a count is satisfied by any five entries,
            // including five entirely different ones.
            string scripts = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts");
            Assert.True(Directory.Exists(scripts), "scripts tree not found at " + scripts);

            string[] allFiles = Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(allFiles);

            foreach ((string pathMatch, string? member, string reason)
                     in ClientWiringDetectors.PerActorGuardExemptionsView)
            {
                string label = pathMatch + (member == null ? " (whole file)" : "." + member);

                string[] matches = allFiles
                    .Where(f => Slash(f).Contains(pathMatch, StringComparison.Ordinal))
                    .ToArray();

                Assert.True(matches.Length > 0,
                    $"STALE G4 EXEMPTION: {label} matches no file under Assets/Scripts. The file "
                    + "was renamed or deleted and the entry now suppresses nothing. DELETE the "
                    + "entry - do not repoint it at whatever file looks closest, and do not leave "
                    + $"it because the reason still reads well (\"{reason}\").");

                foreach (string file in matches)
                {
                    string rel = Slash(file);

                    Assert.True(ClientWiringDetectors.IsInPerActorGuardScopeIgnoringExemptions(rel),
                        $"POINTLESS G4 EXEMPTION: {label} names {rel}, which G4 does not govern "
                        + "even without the exemption. Exempting a file from a rule that never "
                        + "applied to it is a no-op that reads as a considered decision. DELETE "
                        + "the entry.");

                    IReadOnlyList<string> touches = ClientWiringDetectors.LocalOnlySingletonTouchMembers(
                        ClientWiringDetectors.Parse(File.ReadAllText(file), rel));

                    if (member == null)
                    {
                        Assert.True(touches.Count > 0,
                            $"STALE G4 EXEMPTION: {label} exempts the whole of {rel}, which no "
                            + "longer touches a local-only singleton at all. The reason it was "
                            + "written for is gone. DELETE the entry - do NOT keep it as "
                            + "insurance against a touch coming back.");
                        continue;
                    }

                    Assert.True(touches.Contains(member),
                        $"STALE G4 EXEMPTION: {label} exempts a member that no longer reaches a "
                        + $"local-only singleton in {rel}. Either the member was renamed or "
                        + "deleted, or the touch was refactored away - in every case the "
                        + "exemption now hides nothing. DELETE the entry. Do NOT re-point it at "
                        + "whichever member currently does touch one: that would silently move a "
                        + "judgement made about different code onto code nobody judged.");
                }
            }
        }

        [Fact]
        public void TheGateAcceptsTheScriptedRespawnExemption()
        {
            // The green half of the entry added for X-23, as a fixture rather than against the
            // real file, so it keeps its meaning if that file changes. Same shape as the file
            // the exemption names: one per-actor path (which is why G4 scopes the file at all)
            // and one local-only helper reached from Update().
            const string path =
                "Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs";

            IReadOnlyList<GateFinding> findings =
                ClientWiringDetectors.FindUnguardedLocalSingletonTouches(
                    Parse(
                        "class D { void OnSpawnActor(M m) { } "
                        + "static bool ScriptedRespawnPressed() "
                        + "{ return FpsActorController.instance != null; } }",
                        path),
                    path);

            Assert.Empty(findings);
        }

        [Fact]
        public void TheGateStillFlagsAnUnexemptedMemberInTheSameFile()
        {
            // The red twin, and the one that matters: the exemption is per MEMBER, so it must not
            // hand the whole file a pass. Without this, narrowing the entry to a file would look
            // identical on every run.
            const string path =
                "Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs";

            IReadOnlyList<GateFinding> findings =
                ClientWiringDetectors.FindUnguardedLocalSingletonTouches(
                    Parse(
                        "class D { void OnSpawnActor(M m) "
                        + "{ IngameUi.instance.SetHealth(1f); } }",
                        path),
                    path);

            GateFinding finding = Assert.Single(findings);
            Assert.Equal("G4", finding.RuleId);
        }

        /// <summary>
        /// Path separators as the detector normalizes them. Written through
        /// <see cref="Path.DirectorySeparatorChar"/> rather than a literal so the test reads the
        /// same on Windows and Linux, where CI runs both.
        /// </summary>
        private static string Slash(string path)
            => path.Replace(Path.DirectorySeparatorChar, '/');

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }

        private static SyntaxTree Parse(string source, string path)
            => ClientWiringDetectors.Parse(source, path);

        private static ISet<string> Subscriptions(string source, string path)
            => ClientWiringDetectors.FindSubscribedEventNames(Parse(source, path), path);
    }
}
