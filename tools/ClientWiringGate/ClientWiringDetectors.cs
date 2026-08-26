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

            // NOT on a per-actor path. This file is scoped because OnSpawnActor IS one, but
            // ScriptedRespawnPressed is reached only from Update(), reading THIS client's own
            // input to decide whether the local player asked to respawn - the local singleton is
            // the target, not a leak from a remote player's event. It is resolved per frame
            // rather than cached precisely because the body is spawned and killed independently
            // of this component, so a cached reference would go stale at a death, which is the
            // one moment the read matters. G4 scopes by file and cannot see the call path.
            // RESIDUAL RISK, stated rather than hidden: if a second caller ever reaches this
            // helper from OnSpawnActor or another per-actor path, G4 will not catch it. The
            // companion PerActorGuardExemptions_HasNoStaleEntries pins that the exemption is
            // still suppressing a real touch; it does not pin the call path. If a per-actor
            // caller appears, delete this entry and guard the read instead of widening it.
            ("/NetClientLocalCombatDriver.cs", "ScriptedRespawnPressed",
                "reached only from Update(), a local-only per-frame path; the local player IS the "
                + "subject of the read"),
        };

        /// <summary>
        /// The G4 exemptions, exposed read-only so the companion test can re-check every entry
        /// against the tree it claims to describe.
        /// </summary>
        /// <remarks>
        /// An exemption is a stored judgement about code that moves underneath it. Without a
        /// companion, an entry whose file was renamed, whose member was deleted, or whose touch
        /// was refactored away keeps suppressing nothing and reads forever as deliberate - which
        /// is how an allow-list becomes a graveyard nobody re-checks
        /// (<c>pinned-baseline-test-companion.md</c>).
        /// </remarks>
        public static IReadOnlyList<(string PathMatch, string? Member, string Reason)>
            PerActorGuardExemptionsView => PerActorGuardExemptions;

        /// <summary>
        /// Whether <paramref name="path"/> is inside <see cref="PerActorGuardScope"/>, IGNORING
        /// the exemptions. <see cref="IsPerActorGuardScoped"/> answers the question G4 asks;
        /// this answers the one the companion asks - "would this file be governed at all, were
        /// the exemption not there?" - so an entry exempting an out-of-scope file is reported as
        /// the no-op it is.
        /// </summary>
        public static bool IsInPerActorGuardScopeIgnoringExemptions(string path)
            => IsInScope(path, PerActorGuardScope);

        /// <summary>
        /// The enclosing member name of every local-only-singleton <c>.instance</c> touch in
        /// <paramref name="tree"/>, using G4's own notions of "touch" and "enclosing member" so
        /// the companion cannot drift from the rule it guards.
        /// </summary>
        public static IReadOnlyList<string> LocalOnlySingletonTouchMembers(SyntaxTree tree)
        {
            var members = new List<string>();

            foreach (MemberAccessExpressionSyntax access in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Name.Identifier.ValueText != "instance") continue;
                if (!(access.Expression is IdentifierNameSyntax type)) continue;
                if (Array.IndexOf(LocalOnlySingletons, type.Identifier.ValueText) < 0) continue;

                string? member = EnclosingMemberName(access);
                if (member != null) members.Add(member);
            }

            return members;
        }

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

        /// <summary>
        /// The engine-side projectile damage call sites G7 governs — the three files ledger C-1
        /// names as the ones that still apply a projectile's damage from the scene.
        /// </summary>
        private static readonly string[] EngineProjectileDamageScope =
        {
            "/Projectile.cs",
            "/ExplodingProjectile.cs",
            "/GrenadeProjectile.cs",
        };

        /// <summary>
        /// The calls that APPLY a projectile's damage, as (required receiver, method) pairs. A
        /// null receiver matches any.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>ProjectileHit</c> subtracts health through a <c>Hitbox</c> resolved into a local,
        /// so its receiver is a variable and cannot be pinned. <c>ActorManager.Explode</c> runs
        /// the blast loop and MUST be pinned to its type, because <c>Explode</c> is also the name
        /// of the two overridable wrappers that CONTAIN it — <c>ExplodingProjectile.Explode</c>
        /// and <c>GrenadeProjectile.Explode</c>. An unqualified match reported both wrappers'
        /// own call sites, which are not damage and would have had to be guarded twice or
        /// exempted; observed on the rule's first run against the real tree.
        /// </para>
        /// </remarks>
        private static readonly (string? Receiver, string Method)[] EngineProjectileDamageCalls =
        {
            (null, "ProjectileHit"),
            ("ActorManager", "Explode"),
        };

        /// <summary>
        /// The properties a guarded call site may consult, either of which satisfies the rule.
        /// </summary>
        /// <remarks>
        /// <b>Two, not one, because the sites are asking different questions.</b>
        /// <c>Projectile.Travel</c>'s sweep asks "should I apply damage at all"
        /// (<c>EngineAppliesProjectileDamage</c>). The two <c>ActorManager.Explode</c> sites ask
        /// the narrower "is somebody else about to" (<c>LibraryOwnsProjectileDamage</c>), because
        /// that same call also applies the corpse ragdoll impulse a client must keep (AD-4).
        /// Accepting only the first would have forced those two to switch the client's corpses
        /// off to satisfy a gate.
        /// </remarks>
        /// <remarks>
        /// <b>This is the half the library test cannot prove.</b>
        /// <c>ProjectileDamageOwnershipTests</c> proves the partition — that engine and library
        /// are never both the owner — but nothing in a netstandard assembly can see whether
        /// <c>Assembly-CSharp</c> actually asks. Without this rule the flag could be flipped in
        /// Phase 5 against three unguarded call sites and every hit would do double damage, with
        /// a fully green test suite. Ledger C-1, debt-closure phase 2 task 2e.
        /// </remarks>
        private static readonly string[] ProjectileDamageGuardMembers =
        {
            "EngineAppliesProjectileDamage",
            "LibraryOwnsProjectileDamage",
        };

        /// <summary>
        /// The one file G8 governs - <c>Actor</c>, where every damage source in the game funnels
        /// into a single <c>Damage</c> method and therefore into a single ownership guard.
        /// </summary>
        /// <remarks>
        /// Scoped to one file on purpose. The guard is deliberately NOT spread across the six
        /// callers (<c>Hitbox</c>, <c>MeleeWeapon</c>, <c>ExplodingProjectile</c>,
        /// <c>ActorManager</c>, <c>Vehicle</c>, <c>AiActorController</c>), so there is exactly
        /// one place to grade and widening this would report correct code.
        /// </remarks>
        private static readonly string[] HealthOwnershipScope = { "/Actor.cs" };

        /// <summary>The type G8's messages name, for a reader who has only the CI log.</summary>
        private const string HealthOwnershipOwner = "Actor";

        /// <summary>The method that funnels every damage source in the game.</summary>
        private const string HealthOwnershipMethod = "Damage";

        /// <summary>The local whose value, and whose polarity, is the guard.</summary>
        private const string HealthOwnershipLocal = "ownsHealth";

        /// <summary>The member the guard negates. See <see cref="NegatesClientTest"/>.</summary>
        private const string ClientTestMember = "IsClient";

        /// <summary>The field only the health's owner may subtract from.</summary>
        private const string HealthField = "health";

        /// <summary>The call only the health's owner may make.</summary>
        private const string DeathMethod = "Die";

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

        /// <summary>
        /// G7 - an engine-side projectile damage call that does not consult
        /// <c>NetProjectileAuthority.EngineAppliesProjectileDamage</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rule exists for one flag. <c>ServerProjectileBridge.AuthoritativeFlight</c> turns
        /// on the library's ballistic stepper, which applies damage through
        /// <c>IActorDamageSink</c>. These three files apply the SAME damage from the scene. Turn
        /// the flag on with any of them unguarded and every hit lands twice — and every test in
        /// the solution still passes, because no netstandard assembly can see a Unity file.
        /// </para>
        /// <para>
        /// Scoped to the three files by name rather than to a directory: most of
        /// <c>Assembly-CSharp</c> legitimately calls <c>Explode</c> (<c>Vehicle</c>,
        /// <c>ExplosiveProp</c>, the AI), and those are not projectile damage. Widening this
        /// would produce findings that are all correct code, and a gate people learn to ignore is
        /// worse than no gate.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnguardedEngineProjectileDamage(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, EngineProjectileDamageScope)) return findings;

            foreach (InvocationExpressionSyntax invocation in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                string? invoked = NameOfInvoked(invocation);
                if (invoked == null) continue;
                if (!IsEngineProjectileDamageCall(invocation, invoked)) continue;
                if (HasGuardAbove(invocation, MentionsProjectileDamageGuard)) continue;

                findings.Add(new GateFinding(
                    "G7", path, LineOf(invocation),
                    $"'{invoked}(...)' applies a projectile's damage from the engine with no "
                    + "NetProjectileAuthority."
                    + string.Join("/", ProjectileDamageGuardMembers) + " guard. Flipping "
                    + "ServerProjectileBridge.AuthoritativeFlight would then run the library "
                    + "stepper AND this call, and every hit would do double damage (ledger C-1)."));
            }

            return findings;
        }

        /// <summary>Whether this invocation is one of the damage calls, receiver included.</summary>
        private static bool IsEngineProjectileDamageCall(
            InvocationExpressionSyntax invocation, string invoked)
        {
            foreach ((string? receiver, string method) in EngineProjectileDamageCalls)
            {
                if (method != invoked) continue;
                if (receiver == null) return true;

                if (invocation.Expression is MemberAccessExpressionSyntax member
                    && member.Expression is IdentifierNameSyntax type
                    && type.Identifier.ValueText == receiver) return true;
            }

            return false;
        }

        /// <summary>
        /// G8 - <c>Actor.Damage</c>'s <c>ownsHealth</c> guard: present, correctly polarised, and
        /// actually covering the two operations only the health's owner may perform. Ledger X-6.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is not another G7.</b> <see cref="HasGuardAbove"/> deliberately does not
        /// model polarity - it answers "is there a guard at all", because for G7 an inverted
        /// guard still leaves exactly one side applying damage. Here polarity IS the fault.
        /// <c>ownsHealth</c> is <c>!NetContext.IsClient</c>; drop the <c>!</c> and a client
        /// subtracts health the server already subtracted and calls <c>Die()</c> for a death
        /// <c>S_DEATH</c> is about to announce. Nothing else in the tree would notice: the
        /// declaration still exists, both call sites are still guarded, and G7 stays green.
        /// </para>
        /// <para>
        /// <b>Why a gate and not a test.</b> <c>Actor</c> compiles into <c>Assembly-CSharp</c>,
        /// which no test assembly can reference (ledger <b>E-11b</b>), so the guard cannot be
        /// exercised from NUnit at all. That is the same reason G7 exists, and it is why X-6 sat
        /// unpinned: the obvious instrument was unavailable and no second one was built.
        /// </para>
        /// <para>
        /// <b>Three clauses because three different edits break it.</b> Removing the negation
        /// (clause 1) inverts ownership; unguarding the subtraction (clause 2) double-subtracts
        /// on a client; unguarding the death branch (clause 3) kills a body locally that the
        /// server has not killed. Each was mutated and observed RED separately - a single
        /// combined assertion would have passed on two of the three.
        /// </para>
        /// <para>
        /// <b>This is the pin Phase 5 rests on.</b> The cutover decides
        /// <c>AuthoritativeFlight</c> on the strength of "damage applies exactly once", and that
        /// sentence is only true while a client applies none. Ledger C-1.
        /// </para>
        /// </remarks>
        /// <summary>Whether G8 governs this file at all. See <see cref="HealthOwnershipScope"/>.</summary>
        public static bool IsHealthOwnershipScoped(string path) =>
            !IsExcludedFromScan(path) && IsInScope(path, HealthOwnershipScope);

        public static IReadOnlyList<GateFinding> FindUnpinnedHealthOwnershipGuard(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, HealthOwnershipScope)) return findings;

            MethodDeclarationSyntax? damage = tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == HealthOwnershipMethod);

            if (damage == null)
            {
                findings.Add(new GateFinding(
                    "G8", path, 0,
                    $"No '{HealthOwnershipMethod}' method in {HealthOwnershipOwner}. G8 grades the "
                    + $"'{HealthOwnershipLocal}' guard inside it; if the method was renamed, move "
                    + "this rule with it in the same commit rather than letting the guard go "
                    + "ungraded (ledger X-6)."));
                return findings;
            }

            // Clause 1 - the declaration exists and negates a client test.
            VariableDeclaratorSyntax? declarator = damage.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == HealthOwnershipLocal);

            if (declarator == null)
            {
                findings.Add(new GateFinding(
                    "G8", path, LineOf(damage),
                    $"{HealthOwnershipOwner}.{HealthOwnershipMethod} declares no "
                    + $"'{HealthOwnershipLocal}' local. Every damage source in the game funnels "
                    + "through this method, so this local is the only thing stopping a client "
                    + "from writing health the server owns (ledger X-6, D5)."));
            }
            else if (!NegatesClientTest(declarator.Initializer?.Value))
            {
                findings.Add(new GateFinding(
                    "G8", path, LineOf(declarator),
                    $"'{HealthOwnershipLocal}' is not '!<...>.{ClientTestMember}'. Its polarity IS "
                    + "the guard: unnegated, a client subtracts health the server already "
                    + "subtracted and calls Die() for a death S_DEATH is about to announce. "
                    + "Phase 5's 'damage applies exactly once' is only true while a client "
                    + "applies none (ledger X-6, C-1)."));
            }

            // Clauses 2 and 3 - the two owner-only operations are actually under it.
            bool subtractionSeen = false;

            foreach (AssignmentExpressionSyntax assignment in damage.DescendantNodes()
                         .OfType<AssignmentExpressionSyntax>())
            {
                if (!assignment.IsKind(SyntaxKind.SubtractAssignmentExpression)) continue;
                if (assignment.Left is not IdentifierNameSyntax left) continue;
                if (left.Identifier.ValueText != HealthField) continue;

                subtractionSeen = true;
                if (HasGuardAbove(assignment, MentionsHealthOwnershipGuard)) continue;

                findings.Add(new GateFinding(
                    "G8", path, LineOf(assignment),
                    $"'{HealthField} -= ...' runs with no '{HealthOwnershipLocal}' guard above it. "
                    + "On a client this field is written from snapshots, so subtracting here "
                    + "double-counts every hit the server already applied (ledger X-6, D5)."));
            }

            if (!subtractionSeen)
            {
                findings.Add(new GateFinding(
                    "G8", path, LineOf(damage),
                    $"No '{HealthField} -= ...' in {HealthOwnershipOwner}."
                    + $"{HealthOwnershipMethod}. G8 cannot grade a guard over an operation that is "
                    + "no longer there; re-point this rule at wherever health is now subtracted, "
                    + "in the same commit (ledger X-6)."));
            }

            bool deathSeen = false;

            foreach (InvocationExpressionSyntax invocation in damage.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (NameOfInvoked(invocation) != DeathMethod) continue;

                deathSeen = true;
                if (HasGuardAbove(invocation, MentionsHealthOwnershipGuard)) continue;

                findings.Add(new GateFinding(
                    "G8", path, LineOf(invocation),
                    $"'{DeathMethod}(...)' runs with no '{HealthOwnershipLocal}' guard above it. "
                    + "A client's copy of health genuinely reaches zero from snapshots, so this "
                    + "kills the body locally for a death S_DEATH is about to announce (ledger "
                    + "X-6, D5)."));
            }

            if (!deathSeen)
            {
                findings.Add(new GateFinding(
                    "G8", path, LineOf(damage),
                    $"No '{DeathMethod}(...)' in {HealthOwnershipOwner}.{HealthOwnershipMethod}. "
                    + "The death branch is half of what the guard protects; if it moved, move "
                    + "this rule with it in the same commit (ledger X-6)."));
            }

            return findings;
        }

        /// <summary>
        /// Whether <paramref name="initializer"/> is a logical NOT of something ending in
        /// <see cref="ClientTestMember"/>.
        /// </summary>
        /// <remarks>
        /// Matched on the member NAME rather than its fully-qualified receiver, so the guard may
        /// be written <c>!NetContext.IsClient</c> or
        /// <c>!Ironfront.Net.Unity.NetContext.IsClient</c> without the rule caring. What it does
        /// care about is the <c>!</c>.
        /// </remarks>
        private static bool NegatesClientTest(ExpressionSyntax? initializer)
        {
            if (initializer is not PrefixUnaryExpressionSyntax unary) return false;
            if (!unary.IsKind(SyntaxKind.LogicalNotExpression)) return false;

            return unary.Operand.DescendantNodesAndSelf()
                .OfType<SimpleNameSyntax>()
                .Any(name => name.Identifier.ValueText == ClientTestMember);
        }

        /// <summary>G8's predicate: the ownership local by name.</summary>
        private static bool MentionsHealthOwnershipGuard(SyntaxNode condition) =>
            condition.DescendantNodesAndSelf()
                .OfType<SimpleNameSyntax>()
                .Any(name => name.Identifier.ValueText == HealthOwnershipLocal);

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
            => HasGuardAbove(touch, MentionsLocalActorGuard);

        /// <summary>
        /// Whether any enclosing condition, short-circuit, or earlier early-return in the same
        /// member satisfies <paramref name="mentionsGuard"/>.
        /// </summary>
        /// <remarks>
        /// Extracted from G4 when G7 needed the identical walk over a different predicate. Like
        /// G4 it deliberately does NOT model polarity: it answers "is there a guard at all", and
        /// claiming to catch an inverted one would be a green that proves nothing.
        /// </remarks>
        /// <summary>Files G9 grades. See <see cref="FindUnpinnedLevelBoundsCall"/>.</summary>
        private static readonly string[] LevelBoundsScope = { "/Vehicle.cs" };

        /// <summary>The method the bounds call has to live inside.</summary>
        private const string LevelBoundsCaller = "KeepInsideLevelBounds";

        /// <summary>The call itself, and the role guard it must sit behind.</summary>
        private const string LevelBoundsMethod = "ClampInside";

        /// <summary>Whether G9 governs this file at all. See <see cref="LevelBoundsScope"/>.</summary>
        public static bool IsLevelBoundsScoped(string path) =>
            !IsExcludedFromScan(path) && IsInScope(path, LevelBoundsScope);

        /// <summary>
        /// <b>G9</b> — the play-area boundary is still enforced, and still only by the server.
        /// Ledger <b>E-6</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An absence rule, because the fault E-6 records is an absence.</b>
        /// <c>LevelBounds.IsInside</c> shipped with zero callers, and nothing said so for the
        /// whole of V5 — a body past the wire's ±2048 m is clamped silently by
        /// <c>Quantize.PackPos</c> while the server keeps simulating the true position, so
        /// clients and server disagree permanently with no exception and no log line. Deleting
        /// the call restores exactly that, and no test can catch it: <c>Vehicle</c> and
        /// <c>LevelBounds</c> both compile into <c>Assembly-CSharp</c>, which no test assembly
        /// can reference (<b>E-11b</b>) — the same wall G7 and G8 were built against.
        /// </para>
        /// <para>
        /// <b>Two clauses, because two different edits break it.</b> Clause 1 is the call's
        /// existence. Clause 2 is that it sits behind a server-role test: the clamp writes a
        /// rigidbody position, and a client running it would fight its own snapshot corrections
        /// at the boundary — a correction loop that looks like the very rubber-band this closes.
        /// One assertion would have passed the second edit.
        /// </para>
        /// <para>
        /// <b>What it deliberately does not claim.</b> This resolves no symbols, so it says a
        /// call spelled <c>ClampInside</c> appears under a server guard — not that the volume is
        /// authored, nor that the clamp is correct. Whether the authored box fits the wire is a
        /// different question with a different owner: <c>PlayVolume.FitsOnTheWire</c>, asserted
        /// in <c>PlayVolumeTests</c> and reported by <c>LevelBounds.SetupBounds</c> at runtime.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnpinnedLevelBoundsCall(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, LevelBoundsScope)) return findings;

            MethodDeclarationSyntax? caller = tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == LevelBoundsCaller);

            if (caller == null)
            {
                findings.Add(new GateFinding(
                    "G9", path, 0,
                    $"No '{LevelBoundsCaller}' method. LevelBounds.IsInside had zero callers for "
                    + "the whole of V5 and a body past the wire's ±2048 m desynced silently; if "
                    + "this method was renamed, move G9 with it in the same commit rather than "
                    + "letting the boundary go ungraded (ledger E-6)."));
                return findings;
            }

            // Clause 1 - the call exists.
            InvocationExpressionSyntax? clamp = caller.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax member
                                     && member.Name.Identifier.ValueText == LevelBoundsMethod);

            if (clamp == null)
            {
                findings.Add(new GateFinding(
                    "G9", path, LineOf(caller),
                    $"{LevelBoundsCaller} calls no LevelBounds.{LevelBoundsMethod}. Without it a "
                    + "vehicle leaving the play area keeps being simulated at a position the "
                    + "snapshot encoder clamps, so every client sees it pinned to the boundary "
                    + "forever and nothing is logged (ledger E-6)."));
                return findings;
            }

            // Clause 2 - and it is behind a server-role test.
            if (!HasGuardAbove(clamp, MentionsServerRoleTest))
                findings.Add(new GateFinding(
                    "G9", path, LineOf(clamp),
                    $"LevelBounds.{LevelBoundsMethod} runs with no server-role guard above it. "
                    + "The clamp writes a rigidbody position; on a client it fights the snapshot "
                    + "corrections arriving for the same body, which presents as the rubber-band "
                    + "this rule exists to prevent (ledger E-6)."));

            return findings;
        }

        /// <summary>True for a node that tests the server role.</summary>
        /// <remarks>
        /// Text, not symbols, and deliberately narrow: <c>NetContext.IsServer</c> is the one
        /// spelling in the Unity tree. A negated CLIENT test would read as equivalent to a human
        /// and is not accepted, because <c>IsClient</c> and <c>IsServer</c> are not complements
        /// — <c>NetRole.Offline</c> is neither, and offline is exactly the role this clamp is
        /// meant to leave alone.
        /// </remarks>
        private static bool MentionsServerRoleTest(SyntaxNode node)
            => node.ToString().Contains("IsServer", StringComparison.Ordinal);

        private static bool HasGuardAbove(SyntaxNode touch, Func<SyntaxNode, bool> mentionsGuard)
        {
            SyntaxNode? node = touch;
            SyntaxNode? child = null;

            while (node != null)
            {
                if (node is IfStatementSyntax ifStatement)
                {
                    if (child != null && child != ifStatement.Condition
                        && mentionsGuard(ifStatement.Condition)) return true;
                }
                else if (node is ConditionalExpressionSyntax conditional)
                {
                    if (child != null && child != conditional.Condition
                        && mentionsGuard(conditional.Condition)) return true;
                }
                else if (node is BinaryExpressionSyntax binary)
                {
                    // guard && Touch(...) - the short circuit is the guard.
                    bool shortCircuit = binary.IsKind(SyntaxKind.LogicalAndExpression)
                                        || binary.IsKind(SyntaxKind.LogicalOrExpression);
                    if (shortCircuit && child == binary.Right && mentionsGuard(binary.Left)) return true;
                }
                else if (node is MemberDeclarationSyntax member)
                {
                    return HasEarlyReturnGuardBefore(member, touch.SpanStart, mentionsGuard);
                }

                child = node;
                node = node.Parent;
            }

            return false;
        }

        /// <summary>
        /// <c>if (!IsLocalActor(x)) return;</c> at the top of a method guards everything after it.
        /// </summary>
        private static bool HasEarlyReturnGuardBefore(
            SyntaxNode member, int touchPosition, Func<SyntaxNode, bool> mentionsGuard)
        {
            foreach (IfStatementSyntax ifStatement in member.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (ifStatement.SpanStart >= touchPosition) continue;
                if (ifStatement.Else != null) continue;
                if (!mentionsGuard(ifStatement.Condition)) continue;

                StatementSyntax body = ifStatement.Statement;
                if (body is BlockSyntax block && block.Statements.Count == 1) body = block.Statements[0];

                if (body is ReturnStatementSyntax) return true;
            }

            return false;
        }

        /// <summary>G4's predicate: a call to <c>IsLocalActor(...)</c>.</summary>
        private static bool MentionsLocalActorGuard(SyntaxNode condition) =>
            condition.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => NameOfInvoked(invocation) == LocalActorGuardMethod);

        /// <summary>
        /// G7's predicate: the ownership property by NAME, matched as a plain identifier rather
        /// than an invocation because it is a property and not a method.
        /// </summary>
        private static bool MentionsProjectileDamageGuard(SyntaxNode condition) =>
            condition.DescendantNodesAndSelf()
                .OfType<SimpleNameSyntax>()
                .Any(name => Array.IndexOf(
                    ProjectileDamageGuardMembers, name.Identifier.ValueText) >= 0);

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
