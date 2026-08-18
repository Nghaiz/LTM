using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// One thing the gate found wrong, with enough detail to fix it without re-running the gate.
    /// </summary>
    public sealed class GateFinding
    {
        public GateFinding(string ruleId, string filePath, int line, string message)
        {
            RuleId = ruleId;
            FilePath = filePath;
            Line = line;
            Message = message;
        }

        /// <summary>G1, G2, G3 or G4.</summary>
        public string RuleId { get; }

        public string FilePath { get; }

        /// <summary>One-based, so it matches what an editor shows.</summary>
        public int Line { get; }

        public string Message { get; }

        public override string ToString() =>
            Line > 0
                ? $"[{RuleId}] {FilePath}:{Line} - {Message}"
                : $"[{RuleId}] {Message}";
    }

    /// <summary>
    /// The four checks, as pure functions over a parsed syntax tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every detector here is static, takes a <see cref="SyntaxTree"/> plus the path it came from,
    /// and touches no disk. That is not tidiness - it is the only way the gate's own red paths can
    /// be exercised. A check nobody has watched fail is unproven, so <c>ClientWiringGateTests</c>
    /// calls these against in-memory fixture strings, including one fixture per rule that MUST be
    /// reported. See phase-v10 Task 11.
    /// </para>
    /// <para>
    /// Each detector applies its own scope and exclusion rules rather than trusting the caller to
    /// have filtered first, because the caller is sometimes a test passing an arbitrary path.
    /// </para>
    /// </remarks>
    public static class ClientWiringDetectors
    {
        /// <summary>
        /// Unity 6000.3 compiles C# 9. Pinned for the same reason <c>tools/UnitySyntaxCheck</c>
        /// pins it: parsing at the SDK default would make this tool agree with a compiler that
        /// never sees these files.
        /// </summary>
        public const LanguageVersion UnityLanguageVersion = LanguageVersion.CSharp9;

        /// <summary>
        /// Files whose contents must never count, each for a stated reason. Kept as one named
        /// array so "why does this file not count" has exactly one place to check.
        /// </summary>
        private static readonly (string Match, string Reason)[] ScanExclusions =
        {
            // Declarations and Invoke sites are not subscriptions. Without this the router would
            // report every one of its own events as wired, which is the exact false green the
            // gate exists to prevent.
            ("ClientMessageRouter.cs", "the declaration site is not a subscription"),

            // LOAD-BEARING, NOT TIDY. Ironfront.Net.Replication.Tests/ClientCombatTests.cs
            // subscribes OnHitConfirm, OnDeath and OnWeaponFire today; counting those would report
            // three of the dead events as wired. Those files live outside Assets/Scripts so the
            // default root already misses them - the exclusion is here for a caller who passes
            // paths explicitly, and for the fixture test that pins it.
            ("Tests.cs", "a test subscribing is not the game subscribing"),

            // Build output. A stale copy of a deleted file would keep an event looking wired.
            ("/obj/", "build output"),
            ("/bin/", "build output"),
        };

        /// <summary>
        /// The per-actor files G4 governs. A "per-actor path" is code that runs once per actor in
        /// the scene, so reaching a client-only singleton from it writes the local player's HUD or
        /// camera from a REMOTE player's event - recorded finding A16.
        /// </summary>
        /// <remarks>
        /// Everything under <c>Net/Client/</c> is in scope by construction: presenters are
        /// per-actor by definition. Outside it the scope is named file by file, because most of
        /// <c>Assembly-CSharp/</c> legitimately holds the local player's own rig
        /// (<c>FpsActorController</c>, <c>IngameUi</c>, <c>LoadoutUi</c>, <c>GameManager</c>,
        /// <c>NightVision</c>, <c>MinimapUi</c>, <c>Projectile</c>, and <c>AiActorController</c>,
        /// which reads the player's actor deliberately, as its own comments say). Widening this to
        /// the whole tree would produce dozens of findings that are all correct code, and a gate
        /// people learn to ignore is worse than no gate.
        /// </remarks>
        private static readonly string[] PerActorGuardScope =
        {
            "/Net/Client/",
            "/Actor.cs",
            "/TankTurret.cs",
            "/MountedTurret.cs",
        };

        /// <summary>
        /// Exemptions from <see cref="PerActorGuardScope"/>. ONE named, commented array on
        /// purpose: G4 is a judgement call, and a judgement call encoded as a silent regex rots.
        /// Anything added here needs a sentence saying why the local-actor guard is somewhere
        /// else, not absent. A null Member exempts the whole file.
        /// </summary>
        private static readonly (string PathMatch, string? Member, string Reason)[] PerActorGuardExemptions =
        {
            // This file IS the guard. IsLocalActor(Actor) is defined as "does
            // FpsActorController.instance's actor reference-equal this one", so demanding that it
            // guard that read with itself is circular.
            ("/NetClientPresenterGuard.cs", null,
                "defines IsLocalActor; guarding the definition with itself is circular"),

            // One-line HUD writers whose guard lives at every call site, because the callee has no
            // way to know whose HUD it is. Verified 2026-08-18: Actor.cs:894, :1183, :1199 and
            // :1243 are the only callers and all four sit inside an IsLocalActor branch.
            // RESIDUAL RISK, stated rather than hidden: both are public, so a future unguarded
            // caller would not be caught here. If one appears, make the method private and delete
            // this entry rather than widening it.
            ("/Actor.cs", "UpdateAmmoUi",
                "guarded at every call site; the callee cannot know whose HUD it is"),
            ("/Actor.cs", "UpdateHealthUi",
                "guarded at every call site; the callee cannot know whose HUD it is"),

            // Same shape one directory over: the sole caller
            // (NetClientCombatPresenter.cs:124) has already resolved the death as the local
            // player's, and this helper exists to fell that specific body. Its NAME is the
            // assertion; moving the guard inside would ask the question twice.
            ("/NetClientCombatPresenter.cs", "KnockOverLocalActor",
                "the caller has already resolved the victim as local; this fells that body"),

            // NOT a per-actor path at all. A blast shakes the camera of whoever is near it,
            // whatever actor caused it - including a world-sourced explosion belonging to no
            // actor. There is no local-actor question here to guard.
            ("/NetClientExplosionPresenter.cs", "ApplyScreenshake",
                "screenshake is this client's camera by definition; the blast has no owning actor"),
        };

        /// <summary>
        /// The client-only singletons G4 protects. Reaching either from a per-actor path is how a
        /// remote player's event ends up kicking your camera or rewriting your health bar.
        /// </summary>
        private static readonly string[] LocalOnlySingletons = { "FpsActorController", "IngameUi" };

        /// <summary>
        /// The predicate that makes a per-actor singleton touch legitimate:
        /// <c>NetClientPresenterGuard.IsLocalActor(...)</c>, matched on the method name so the
        /// fully-qualified call sites in <c>Assembly-CSharp/</c> are recognised too.
        /// </summary>
        private const string LocalActorGuardMethod = "IsLocalActor";

        /// <summary>
        /// Members that would do real damage or move the local camera if a cosmetics presenter
        /// called them (D7). <c>SpawnProjectile</c> sets <c>component.source = user</c>; a client
        /// drawing a remote player's muzzle flash must never reach it.
        /// </summary>
        private static readonly string[] DamagePathMembers = { "SpawnProjectile", "ApplyRecoil" };

        /// <summary>The directory G2 forbids those members in.</summary>
        private const string CosmeticsOnlyDirectory = "/Net/Client/";

        /// <summary>
        /// Members that would double-drive the win condition if a networked presenter called them
        /// (D11). <c>ScoreUi.AddScore</c> and <c>AddFlag</c> are delta-only with no getters, and
        /// <c>MatchStateMachine</c>'s state is get-only — so feeding the server's authoritative
        /// TOTALS through them re-enters <c>ScoreMultiplier</c> and drives the victory check a
        /// second time. <c>SetAuthoritativeState</c> exists precisely to bypass them.
        /// </summary>
        /// <remarks>
        /// A gate rather than a reviewer's memory because the tempting refactor — "the HUD
        /// already has AddScore, just call that" — reads as tidying up. It fails the build now.
        /// </remarks>
        private static readonly string[] DeltaScoreMembers = { "AddScore", "AddFlag" };

        /// <summary>
        /// Files G3 governs. Scoped rather than tree-wide: this legacy tree predates the project
        /// and a tree-wide empty-catch ban would be a refactor, not a gate. CapturePoint.cs is here
        /// because Task 9 defect 3 is a swallowed NullReferenceException on a path this phase is
        /// about to start driving from the network.
        /// </summary>
        private static readonly string[] EmptyCatchScope = { "/CapturePoint.cs" };

        /// <summary>Parses at the language version Unity uses, so fixtures and the real tree agree.</summary>
        public static SyntaxTree Parse(string source, string path) =>
            CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(UnityLanguageVersion), path);

        /// <summary>
        /// Whether this path's contents must be ignored entirely. See <see cref="ScanExclusions"/>.
        /// </summary>
        public static bool IsExcludedFromScan(string path)
        {
            string normalized = Normalize(path);

            foreach ((string match, string _) in ScanExclusions)
            {
                if (normalized.EndsWith(match, StringComparison.Ordinal)) return true;
                if (normalized.Contains(match, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// G1 - the names of every event this file subscribes to, via <c>Something.OnX += ...</c>.
        /// </summary>
        /// <remarks>
        /// Returns empty for an excluded path, so a subscription inside a test file or inside the
        /// router's own declaration does not count. Roslyn is what makes a commented-out or
        /// <c>#if</c>-disabled subscription invisible here: neither survives into the syntax tree
        /// as an assignment.
        /// </remarks>
        public static ISet<string> FindSubscribedEventNames(SyntaxTree tree, string path)
        {
            var subscribed = new HashSet<string>(StringComparer.Ordinal);
            if (IsExcludedFromScan(path)) return subscribed;

            foreach (AssignmentExpressionSyntax assignment in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<AssignmentExpressionSyntax>())
            {
                if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)) continue;

                // Router.OnDeath += H  gives a member access;  OnDeath += H  gives a bare name.
                string? name = assignment.Left switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => null,
                };

                if (name != null) subscribed.Add(name);
            }

            return subscribed;
        }

        /// <summary>
        /// G2 - references to a damage-or-camera member from the cosmetics-only client directory.
        /// </summary>
        public static IReadOnlyList<GateFinding> FindClientDamagePathReferences(SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();
            string normalized = Normalize(path);

            if (IsExcludedFromScan(path)) return findings;
            if (!normalized.Contains(CosmeticsOnlyDirectory, StringComparison.Ordinal)) return findings;

            // DescendantNodes does not descend into trivia, so an XML doc comment naming
            // SpawnProjectile is not a reference to it. That is the intended reading.
            foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                string text = name.Identifier.ValueText;
                if (Array.IndexOf(DamagePathMembers, text) < 0) continue;

                findings.Add(new GateFinding(
                    "G2", path, LineOf(name),
                    $"'{text}' is referenced from Net/Client. That directory draws other players' "
                    + "cosmetics; SpawnProjectile does real damage and ApplyRecoil kicks this "
                    + "client's own camera (D7)."));
            }

            return findings;
        }

        /// <summary>
        /// G5 - a networked presenter routing authoritative numbers through <c>ScoreUi</c>'s
        /// delta-only mutators (D11 / D12).
        /// </summary>
        public static IReadOnlyList<GateFinding> FindDeltaScoreReferences(SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();
            string normalized = Normalize(path);

            if (IsExcludedFromScan(path)) return findings;
            if (!normalized.Contains(CosmeticsOnlyDirectory, StringComparison.Ordinal)) return findings;

            foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                string text = name.Identifier.ValueText;
                if (Array.IndexOf(DeltaScoreMembers, text) < 0) continue;

                findings.Add(new GateFinding(
                    "G5", path, LineOf(name),
                    $"'{text}' is referenced from Net/Client. It is a DELTA mutator with no "
                    + "getter, so feeding the server's totals through it re-enters "
                    + "ScoreMultiplier and double-drives the win check. Use "
                    + "ScoreUi.SetAuthoritativeState (D11)."));
            }

            return findings;
        }

        /// <summary>
        /// G3 - <c>catch (Exception) { }</c> and <c>catch { }</c> in the files G3 governs.
        /// </summary>
        public static IReadOnlyList<GateFinding> FindEmptyCatchClauses(SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, EmptyCatchScope)) return findings;

            foreach (CatchClauseSyntax catchClause in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<CatchClauseSyntax>())
            {
                if (catchClause.Block.Statements.Count > 0) continue;

                findings.Add(new GateFinding(
                    "G3", path, LineOf(catchClause),
                    "empty catch. The exception swallowed here was a NullReferenceException that "
                    + "hid a real defect (Task 9 defect 3) - fix the cause or log it, but do not "
                    + "discard it."));
            }

            return findings;
        }

        /// <summary>
        /// G4 - a client-only singleton reached from a per-actor path with no <c>IsLocalActor</c>
        /// guard anywhere between it and the enclosing member.
        /// </summary>
        /// <remarks>
        /// A touch counts as guarded when an enclosing <c>if</c> / conditional / short-circuit
        /// condition mentions <see cref="LocalActorGuardMethod"/>, or when an early-return guard
        /// (<c>if (!IsLocalActor(x)) return;</c>) appears earlier in the same member. The detector
        /// deliberately does NOT model the guard's polarity: it answers "is there a guard at all",
        /// which is the failure A16 actually was. An inverted guard is a code-review problem, and
        /// claiming to catch it would be a green that proves nothing.
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnguardedLocalSingletonTouches(SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsPerActorGuardScoped(path)) return findings;

            foreach (MemberAccessExpressionSyntax access in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Name.Identifier.ValueText != "instance") continue;
                if (!(access.Expression is IdentifierNameSyntax type)) continue;

                string singleton = type.Identifier.ValueText;
                if (Array.IndexOf(LocalOnlySingletons, singleton) < 0) continue;

                if (IsMemberExempt(path, EnclosingMemberName(access))) continue;
                if (HasLocalActorGuard(access)) continue;

                findings.Add(new GateFinding(
                    "G4", path, LineOf(access),
                    $"'{singleton}.instance' is reached from a per-actor path with no "
                    + $"NetClientPresenterGuard.{LocalActorGuardMethod} guard. A remote player's "
                    + "event would write this client's own HUD or camera (finding A16)."));
            }

            return findings;
        }

        /// <summary>Whether G4 governs this file at all. See <see cref="PerActorGuardScope"/>.</summary>
        public static bool IsPerActorGuardScoped(string path)
        {
            if (!IsInScope(path, PerActorGuardScope)) return false;

            string normalized = Normalize(path);
            foreach ((string pathMatch, string? member, string _) in PerActorGuardExemptions)
            {
                if (member != null) continue;
                if (normalized.Contains(pathMatch, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        private static bool IsMemberExempt(string path, string? memberName)
        {
            if (memberName == null) return false;

            string normalized = Normalize(path);
            foreach ((string pathMatch, string? member, string _) in PerActorGuardExemptions)
            {
                if (member != memberName) continue;
                if (normalized.Contains(pathMatch, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static bool HasLocalActorGuard(SyntaxNode touch)
        {
            SyntaxNode? node = touch;
            SyntaxNode? child = null;

            while (node != null)
            {
                if (node is IfStatementSyntax ifStatement)
                {
                    if (child != null && child != ifStatement.Condition
                        && MentionsGuard(ifStatement.Condition)) return true;
                }
                else if (node is ConditionalExpressionSyntax conditional)
                {
                    if (child != null && child != conditional.Condition
                        && MentionsGuard(conditional.Condition)) return true;
                }
                else if (node is BinaryExpressionSyntax binary)
                {
                    // guard && Touch(...) - the short circuit is the guard.
                    bool shortCircuit = binary.IsKind(SyntaxKind.LogicalAndExpression)
                                        || binary.IsKind(SyntaxKind.LogicalOrExpression);
                    if (shortCircuit && child == binary.Right && MentionsGuard(binary.Left)) return true;
                }
                else if (node is MemberDeclarationSyntax member)
                {
                    return HasEarlyReturnGuardBefore(member, touch.SpanStart);
                }

                child = node;
                node = node.Parent;
            }

            return false;
        }

        /// <summary>
        /// <c>if (!IsLocalActor(x)) return;</c> at the top of a method guards everything after it.
        /// </summary>
        private static bool HasEarlyReturnGuardBefore(SyntaxNode member, int touchPosition)
        {
            foreach (IfStatementSyntax ifStatement in member.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (ifStatement.SpanStart >= touchPosition) continue;
                if (ifStatement.Else != null) continue;
                if (!MentionsGuard(ifStatement.Condition)) continue;

                StatementSyntax body = ifStatement.Statement;
                if (body is BlockSyntax block && block.Statements.Count == 1) body = block.Statements[0];

                if (body is ReturnStatementSyntax) return true;
            }

            return false;
        }

        private static bool MentionsGuard(SyntaxNode condition) =>
            condition.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => NameOfInvoked(invocation) == LocalActorGuardMethod);

        private static string? NameOfInvoked(InvocationExpressionSyntax invocation) =>
            invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => null,
            };

        private static string? EnclosingMemberName(SyntaxNode node)
        {
            for (SyntaxNode? current = node; current != null; current = current.Parent)
            {
                if (current is MethodDeclarationSyntax method) return method.Identifier.ValueText;
                if (current is PropertyDeclarationSyntax property) return property.Identifier.ValueText;
                if (current is ConstructorDeclarationSyntax constructor) return constructor.Identifier.ValueText;
            }

            return null;
        }

        private static bool IsInScope(string path, string[] scope)
        {
            string normalized = Normalize(path);

            foreach (string entry in scope)
                if (normalized.Contains(entry, StringComparison.Ordinal)) return true;

            return false;
        }

        /// <summary>
        /// Forward slashes, with a leading one, so a scope entry written as "/Actor.cs" matches a
        /// bare "Actor.cs" fixture path as well as a real Windows path.
        /// </summary>
        private static string Normalize(string path)
        {
            string forward = path.Replace('\\', '/');
            return forward.StartsWith("/", StringComparison.Ordinal) ? forward : "/" + forward;
        }

        private static int LineOf(SyntaxNode node) =>
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }
}
