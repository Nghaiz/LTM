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
            // The NetClientPresenterGuard.cs entry that used to sit here was DELETED by phase
            // C4a, on this gate's own instruction. It existed because IsLocalActor(Actor) was
            // defined as "does FpsActorController.instance's actor reference-equal this one" --
            // a local-only singleton read that could not guard itself without circularity. C4a
            // inverted that: the question is now asked through IGameplayActorPresence, which the
            // Actor answers on the far side of the seam, so the file touches no singleton at all
            // and the exemption had nothing left to exempt. PerActorGuardExemptions_HasNoStaleEntries
            // is what noticed, which is the whole reason that companion exists.

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

            // The same shape again, added with P12 D-1's local-team apply. Reached only from
            // Update(); the rig it reads and writes is THIS client's own body, and the team it
            // writes came from the snapshot entry for THIS client's own actor id -- so there is
            // no remote actor in scope for an IsLocalActor guard to be about. It is resolved per
            // frame for the same reason the two neighbours are: the body is spawned, killed and
            // respawned independently of this component. Same instruction as the others: if a
            // per-actor caller ever reaches this helper, delete this entry and guard the read.
            ("/NetClientLocalCombatDriver.cs", "ApplyLocalTeam",
                "reached only from Update(), a local-only per-frame path; the local player IS the "
                + "subject of both the read and the write"),

            // The same shape as the entry above, added with PredictFire's caller (ledger X-16).
            // Reached only from Update(); the trigger it reads is the local player's own, and
            // there is no actor id in scope to guard against. The same instruction applies: if a
            // per-actor caller ever appears, delete this and guard the read.
            ("/NetClientLocalCombatDriver.cs", "FirePressed",
                "reached only from Update(), a local-only per-frame path; the local player IS the "
                + "subject of the read"),

            // The fourth of the same shape, added when X-11 gave C_SPAWN_REQUEST a real body.
            // RequestRespawn used to send an empty body and so touched no singleton at all; it
            // now reads the loadout THIS client is about to render, which is what brought it
            // into G4's sight. Verified 2026-09-03: line 316, inside Update(), is its ONLY
            // caller -- the same Update() that already reaches FirePressed and
            // ScriptedRespawnPressed above. There is no actor id in scope for an IsLocalActor
            // guard to be about; the request being sent is this client's own. Same instruction
            // as the three above: if a per-actor caller ever reaches this helper, delete this
            // entry and guard the read rather than widening it.
            ("/NetClientLocalCombatDriver.cs", "RequestRespawn",
                "reached only from Update(), a local-only per-frame path; the loadout read is "
                + "this client's own spawn request"),

            // The fifth of the same shape, added when the FIRST deploy moved off the death screen
            // and onto the loadout screen's own Deploy button. The death panel is authored with a
            // "YOU WERE KILLED" title, so driving it from "a deploy is owed" showed it before any
            // death had happened -- every player's first sight of the game was a death banner
            // naming actor 0. Verified 2026-09-04: line 350, inside Update() at line 289, is this
            // helper's ONLY caller -- the same Update() the four entries above already name. The
            // edge it reads is a button on THIS client's own loadout screen; there is no actor id
            // in scope for an IsLocalActor guard to be about. Same instruction as the four above:
            // if a per-actor caller ever reaches this helper, delete this entry and guard the read
            // rather than widening it.
            ("/NetClientLocalCombatDriver.cs", "LoadoutDeployPressed",
                "reached only from Update(), a local-only per-frame path; the deploy edge is this "
                + "client's own loadout screen"),

            // The third of the same shape, added with the C_SEAT_REQUEST sender (ledger X-30).
            // Reached only from Update(); it reads THIS client's own input to decide whether the
            // player asked for a seat, and there is no actor id in scope to guard against. The
            // file IS otherwise per-actor -- OnSeatChange takes an ActorId and guards it with
            // IsLocalActor, which is why the file is in scope at all and why the exemption is
            // per-member rather than per-file. Same instruction as the two above: if a per-actor
            // caller ever reaches this helper, delete this entry and guard the read.
            ("/ClientSeatRequester.cs", "TryReadLocalSeatIntent",
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
        /// The enclosing member name of every local-only-singleton handle touch in
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
                if (!IsLocalOnlySingletonTouch(access, out _)) continue;

                string? member = EnclosingMemberName(access);
                if (member != null) members.Add(member);
            }

            return members;
        }

        /// <summary>
        /// The client-only singleton HANDLES G4 protects, as (type, member) pairs. Reaching one
        /// from a per-actor path is how a remote player's event ends up kicking your camera or
        /// rewriting your health bar.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Pairs, not type names with an implied <c>.instance</c> — phase C4a is why.</b> That
        /// phase sealed <c>Net/Client</c> behind interfaces, so every
        /// <c>FpsActorController.instance</c> read in the folder became
        /// <c>NetClientBindings.LocalPlayer</c>. The hazard did not move an inch: it is the same
        /// singleton, reached from the same per-actor paths, able to write the same camera. Only
        /// the spelling changed. A detector that knew one spelling would have gone quietly green
        /// across the whole folder — and the four exemptions below would have read as "no longer
        /// needed" rather than "no longer visible", which is the more dangerous of the two
        /// because it looks like progress.
        /// </para>
        /// <para>
        /// The companion is what caught it: <c>PerActorGuardExemptions_HasNoStaleEntries</c> went
        /// RED the moment the reads were re-spelled, and reading the failure in the direction it
        /// pointed — is this a fix, or is it the gate losing sight? — is the whole reason
        /// <c>pinned-baseline-test-companion.md</c> demands a leash on stored judgements.
        /// </para>
        /// <para>
        /// <b><c>NetClientBindings.ShowHit</c> is deliberately absent.</b> It is the static
        /// forwarder to the HUD, and its predecessor <c>IngameUi.Hit(...)</c> was never covered
        /// either — G4 has only ever matched singleton HANDLES, not static calls that use one
        /// internally. Adding it here would widen the rule under cover of a refactor that was
        /// supposed to change nothing; if that gap is worth closing it is worth closing on its
        /// own evidence, for both spellings at once.
        /// </para>
        /// </remarks>
        private static readonly (string Type, string Member)[] LocalOnlySingletons =
        {
            ("FpsActorController", "instance"),
            ("IngameUi", "instance"),

            // C4a. The seam Net/Client now reaches the local player and the HUD through. Same
            // singletons, same hazard, new names.
            ("NetClientBindings", "LocalPlayer"),
            ("NetClientBindings", "Hud"),
        };

        /// <summary>
        /// Whether <paramref name="access"/> is a touch of a client-only singleton handle, and
        /// which one.
        /// </summary>
        private static bool IsLocalOnlySingletonTouch(
            MemberAccessExpressionSyntax access, out string singleton)
        {
            singleton = string.Empty;

            if (!(access.Expression is IdentifierNameSyntax type)) return false;

            string typeName   = type.Identifier.ValueText;
            string memberName = access.Name.Identifier.ValueText;

            foreach ((string Type, string Member) candidate in LocalOnlySingletons)
            {
                if (candidate.Type != typeName || candidate.Member != memberName) continue;

                singleton = typeName + "." + memberName;
                return true;
            }

            return false;
        }

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
        /// The engine-side directory G15 governs: the legacy game, where the offline scoreboard
        /// lives and where every mutator call site is.
        /// </summary>
        private const string EngineDirectory = "/Assembly-CSharp/";

        /// <summary>
        /// Files G15 does not govern. <c>MatchScoreboard</c> DECLARES the mutators; a call from
        /// inside the type is the type doing its job, and gating it there would be wrong rather
        /// than merely noisy.
        /// </summary>
        private static readonly string[] DeltaScoreGuardExclusions = { "/MatchScoreboard.cs" };

        /// <summary>
        /// The predicate that makes an engine-side delta-score mutation legitimate. Matched on
        /// the member name so <c>Ironfront.Net.Unity.NetContext.IsOffline</c> and the
        /// <c>using</c>-shortened <c>NetContext.IsOffline</c> are both recognised.
        /// </summary>
        private const string OfflineGuardMember = "IsOffline";

        /// <summary>
        /// G15 - an engine-side <c>MatchScoreboard.AddScore</c> / <c>AddFlag</c> reached on a
        /// networked path, because nothing gates it on <c>NetContext.IsOffline</c> (P12 D-2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The sibling of G5, over the same two members, from the other side.</b> G5 stops a
        /// <c>Net/Client</c> presenter routing the server's TOTALS through delta mutators. This
        /// stops the reverse: the legacy engine keeping its own private tally while a networked
        /// client is running, which <c>ScoreUi.UpdateUi</c> then paints over the server's
        /// numbers. Both rules read <see cref="DeltaScoreMembers"/> — one list, so the two can
        /// never come to disagree about which members are delta mutators.
        /// </para>
        /// <para>
        /// <b>Why a gate and not a review note.</b> Both call sites P12 fixed —
        /// <c>Actor.Die</c> and <c>CapturePoint.SetOwner</c> — were reached from paths the
        /// SERVER drives, and the symptom was a wrong number on a HUD rather than an exception.
        /// Three sibling call sites already carried the guard (<c>CapturePoint</c> line 147,
        /// <c>MinimapUi</c>, <c>Projectile</c>), which is exactly the shape a reviewer reads as
        /// "this file already handles that" and moves on.
        /// </para>
        /// <para>
        /// <b>What it does NOT prove.</b> It matches the lexical guard, not reachability: a call
        /// wrapped in <c>if (NetContext.IsOffline)</c> passes even if that branch is dead, and a
        /// call guarded by an equivalent predicate spelled another way fails. The first is
        /// harmless, the second is the maintenance cost, and both are preferable to a rule that
        /// tries to evaluate role at compile time and quietly answers "maybe".
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnguardedEngineScoreMutation(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();
            string normalized = Normalize(path);

            if (IsExcludedFromScan(path)) return findings;
            if (!normalized.Contains(EngineDirectory, StringComparison.Ordinal)) return findings;
            if (IsInScope(path, DeltaScoreGuardExclusions)) return findings;

            foreach (InvocationExpressionSyntax invocation in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;

                string called = member.Name.Identifier.ValueText;
                if (Array.IndexOf(DeltaScoreMembers, called) < 0) continue;

                // The receiver must name the scoreboard. `AddScore` is a common enough verb that
                // an unqualified match would rope in anything that happens to share the name.
                if (!member.Expression.ToString().Contains("MatchScoreboard", StringComparison.Ordinal))
                    continue;

                if (IsInsideOfflineGuard(invocation)) continue;

                findings.Add(new GateFinding(
                    "G15", path, LineOf(invocation),
                    $"'MatchScoreboard.{called}' is called with no NetContext.IsOffline guard. "
                    + "It is a DELTA mutator on the OFFLINE scoreboard, so on a networked client "
                    + "this keeps a private tally that ScoreUi.UpdateUi then paints over the "
                    + "server's authoritative numbers (P12 D-2). Wrap it in "
                    + "`if (NetContext.IsOffline)`, as CapturePoint.cs:147, MinimapUi and "
                    + "Projectile already do."));
            }

            return findings;
        }

        /// <summary>
        /// Whether <paramref name="node"/> sits in the TRUE branch of an
        /// <c>if (… IsOffline …)</c>.
        /// </summary>
        /// <remarks>
        /// <b>The true branch specifically, not merely "inside an if that mentions IsOffline".</b>
        /// A call in the <c>else</c> of an offline test is the networked path — precisely the
        /// case this rule exists to catch — and a containment check that ignored which branch it
        /// was in would clear it. That is the difference between a gate and decoration.
        /// </remarks>
        private static bool IsInsideOfflineGuard(SyntaxNode node)
        {
            for (SyntaxNode? current = node; current != null; current = current.Parent)
            {
                if (current.Parent is not IfStatementSyntax ifStatement) continue;
                if (!ReferenceEquals(ifStatement.Statement, current)) continue;

                bool mentionsOffline = ifStatement.Condition
                    .DescendantNodesAndSelf()
                    .OfType<SimpleNameSyntax>()
                    .Any(name => name.Identifier.ValueText == OfflineGuardMember);

                if (mentionsOffline) return true;
            }

            return false;
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
                if (!IsLocalOnlySingletonTouch(access, out string singleton)) continue;

                if (IsMemberExempt(path, EnclosingMemberName(access))) continue;
                if (HasLocalActorGuard(access)) continue;

                findings.Add(new GateFinding(
                    "G4", path, LineOf(access),
                    $"'{singleton}' is reached from a per-actor path with no "
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
        /// <summary>
        /// Files G9 grades, as (path match, caller method, call it must make, whether that call
        /// must sit behind a server-role test) rows. See
        /// <see cref="FindUnpinnedLevelBoundsCall"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A table rather than one hardcoded pair, since X-75.</b> <c>Vehicle.cs</c> and
        /// <c>ServerPlayer.cs</c> both keep a body inside the wire's range, but they do it
        /// through different seams for a reason G9's own remarks already give: <c>Vehicle</c>
        /// calls <c>LevelBounds.ClampInside</c> because it can — it compiles into
        /// <c>Assembly-CSharp</c> alongside <c>LevelBounds</c>. <c>ServerPlayer</c> lives in the
        /// <c>Ironfront.Net.Unity.Server</c> asmdef, which cannot reference
        /// <c>Assembly-CSharp</c> at all, so it clamps against <c>PlayVolume</c> built from
        /// <c>Quantize.POS_MIN</c>/<c>POS_MAX</c> directly — the wire's own range, needing no
        /// seam. One row per shape, not one rule per file.
        /// </para>
        /// <para>
        /// <b>The server-role clause is per-row, not universal.</b> <c>Vehicle.KeepInsideLevelBounds</c>
        /// runs from <c>FixedUpdate</c> on every instance in the scene, client and server alike,
        /// so it needs an explicit <c>NetContext.IsServer</c> guard or a client fights its own
        /// snapshot corrections at the boundary. <c>ServerPlayer</c> has no client-side
        /// counterpart at all — it is constructed only for a connection this process is
        /// authoritative for — so requiring the same textual guard there would be grading code
        /// for a race that cannot happen on that path.
        /// </para>
        /// </remarks>
        private static readonly (string PathMatch, string CallerMethod, string CalleeMethod, bool RequiresServerRoleGuard)[]
            LevelBoundsCalls =
        {
            ("/Vehicle.cs",     "KeepInsideLevelBounds", "ClampInside", true),
            ("/ServerPlayer.cs", "EnforceWireVolume",     "TryClamp",   false),
        };

        /// <summary>Files G9 grades, ignoring which row governs them. See <see cref="LevelBoundsCalls"/>.</summary>
        private static readonly string[] LevelBoundsScope =
            Array.ConvertAll(LevelBoundsCalls, row => row.PathMatch);

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

            string normalized = Normalize(path);
            (string PathMatch, string CallerMethod, string CalleeMethod, bool RequiresServerRoleGuard)? row = null;
            foreach (var candidate in LevelBoundsCalls)
            {
                if (!normalized.Contains(candidate.PathMatch, StringComparison.Ordinal)) continue;
                row = candidate;
                break;
            }

            if (row == null) return findings;

            string callerName = row.Value.CallerMethod;
            string calleeName = row.Value.CalleeMethod;

            MethodDeclarationSyntax? caller = tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == callerName);

            if (caller == null)
            {
                findings.Add(new GateFinding(
                    "G9", path, 0,
                    $"No '{callerName}' method. LevelBounds.IsInside had zero callers for "
                    + "the whole of V5 and a body past the wire's ±2048 m desynced silently; if "
                    + "this method was renamed, move G9 with it in the same commit rather than "
                    + "letting the boundary go ungraded (ledger E-6 / X-75)."));
                return findings;
            }

            // Clause 1 - the call exists.
            InvocationExpressionSyntax? clamp = caller.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax member
                                     && member.Name.Identifier.ValueText == calleeName);

            if (clamp == null)
            {
                findings.Add(new GateFinding(
                    "G9", path, LineOf(caller),
                    $"{callerName} calls no .{calleeName}(...). Without it a body leaving the "
                    + "play area keeps being simulated at a position the snapshot encoder "
                    + "clamps, so every client sees it pinned to the boundary forever and "
                    + "nothing is logged (ledger E-6 / X-75)."));
                return findings;
            }

            // Clause 2 - and, where this row requires it, it is behind a server-role test.
            if (row.Value.RequiresServerRoleGuard && !HasGuardAbove(clamp, MentionsServerRoleTest))
                findings.Add(new GateFinding(
                    "G9", path, LineOf(clamp),
                    $"{calleeName} runs with no server-role guard above it. "
                    + "The clamp writes a position; on a client it fights the snapshot "
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

        private static readonly string[] DedicatedServerDialScope = { "/NetClientBootstrap.cs" };

        private const string DedicatedServerDialCaller = "Awake";

        private const string DedicatedServerFlag = "IsDedicatedServer";

        /// <summary>The call the guard must precede. See the rule's remark for why order matters.</summary>
        private const string DedicatedServerDialConfigure = "ResolveConfiguration";

        /// <summary>Exposed so the companion test can assert the rule is in scope at all.</summary>
        public static bool IsDedicatedServerDialScoped(string path) =>
            !IsExcludedFromScan(path) && IsInScope(path, DedicatedServerDialScope);

        /// <summary>
        /// G11 - <c>NetClientBootstrap.Awake</c> dialling a client on a dedicated server.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a gate and not a test.</b> The behaviour is one early return inside a Unity
        /// lifecycle method, and Unity does not run <c>Awake</c> on <c>AddComponent</c> outside
        /// play mode — <c>NetServerActorSeamTests</c> already records paying for that. An EditMode
        /// fixture built around it passes whether or not the guard exists, which is the shape
        /// <c>green-that-proves-nothing.md</c> is about; it was written first here and its own
        /// control test caught it going vacuously green.
        /// </para>
        /// <para>
        /// <b>What it protects.</b> Every map scene carries an active <c>NetServer</c> AND an
        /// active <c>NetClient</c>, so any process loading one is a listen server. The lane-B
        /// harness strips the half it is not; the shipped dedicated server strips nothing, so
        /// before this guard it dialled itself over loopback and joined its own match — a real
        /// body at a real spawn point, one of sixteen player slots and one connection spent on a
        /// phantom, and the congestion controller reacting to its own traffic. Measured on the
        /// first deployment anybody read the log of: <c>[net] conn 1 joined as actor 41
        /// (127.0.0.1:59244)</c>. <c>architecture.md</c> AD-1 says there is no host/listen-server
        /// mode; nothing enforced it.
        /// </para>
        /// <para>
        /// <b>Position is part of the rule, not pedantry.</b> The guard must sit ahead of
        /// <c>ResolveConfiguration</c>, because the role claim below that line
        /// (<c>if (!NetContext.IsServer) SetRole(Client)</c>) races
        /// <c>NetServerBootstrap.Awake</c>'s mirror of it and can settle a dedicated process as a
        /// Client. A guard placed lower still stops the dial and still leaves that race — it was
        /// this rule's author's own first draft, which is why the check is here rather than left
        /// to a reviewer.
        /// </para>
        /// <para>
        /// <b>Not <c>NetContext.IsServer</c>.</b> That property IS the race. Matching on
        /// <c>IsDedicatedServer</c> by name is deliberate: it has exactly one setter, and a
        /// future edit that "simplifies" the condition to <c>IsServer</c> turns this rule red
        /// rather than quietly reintroducing an Awake-order dependency.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnguardedDedicatedServerClientDial(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, DedicatedServerDialScope)) return findings;

            MethodDeclarationSyntax? awake = tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == DedicatedServerDialCaller);

            if (awake == null)
            {
                findings.Add(new GateFinding(
                    "G11", path, 0,
                    $"'{DedicatedServerDialCaller}' is gone from NetClientBootstrap, so the "
                    + "dedicated-server guard cannot be where it has to be. If the dial moved to "
                    + "another member, move this rule with it rather than deleting it."));
                return findings;
            }

            IfStatementSyntax? guard = awake.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .FirstOrDefault(i =>
                    i.Condition.ToString().Contains(DedicatedServerFlag, StringComparison.Ordinal)
                    && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());

            if (guard == null)
            {
                findings.Add(new GateFinding(
                    "G11", path, LineOf(awake),
                    $"Awake has no 'if (NetContext.{DedicatedServerFlag}) ... return;' guard, so a "
                    + "dedicated server dials a client and joins its own match: a body at a spawn "
                    + "point, one of sixteen player slots and one connection gone, and the "
                    + "congestion controller reacting to its own loopback traffic. AD-1 says "
                    + "there is no host/listen-server mode."));
                return findings;
            }

            InvocationExpressionSyntax? configure = awake.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i =>
                    NameOfInvoked(i) == DedicatedServerDialConfigure);

            if (configure != null && configure.SpanStart < guard.SpanStart)
            {
                findings.Add(new GateFinding(
                    "G11", path, LineOf(guard),
                    $"the dedicated-server guard sits AFTER '{DedicatedServerDialConfigure}', so "
                    + "the role claim between them still runs on a dedicated server and still "
                    + "races NetServerBootstrap.Awake for this process's identity. Move the guard "
                    + "above it."));
            }

            return findings;
        }

        private static readonly string[] DeclaredClientHostScope = { "/NetServerBootstrap.cs" };

        private const string DeclaredClientHostCaller = "Awake";

        private const string DeclaredClientFlag = "IsDeclaredClient";

        /// <summary>The call the guard must precede. See the rule's remark for why order matters.</summary>
        private const string DeclaredClientHostConfigure = "ResolveConfiguration";

        /// <summary>Exposed so the companion test can assert the rule is in scope at all.</summary>
        public static bool IsDeclaredClientHostScoped(string path) =>
            !IsExcludedFromScan(path) && IsInScope(path, DeclaredClientHostScope);

        /// <summary>
        /// G14 - <c>NetServerBootstrap.Awake</c> hosting a server on a declared client.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The mirror of G11, and the other half of AD-1.</b> G11 stops a dedicated server
        /// dialling itself a client; this stops a client hosting a server. Both exist because
        /// every map scene carries an active <c>NetServer</c> AND an active <c>NetClient</c>, so
        /// a process that declares nothing is a listen server — and <c>architecture.md</c> AD-1
        /// says there is no listen-server mode.
        /// </para>
        /// <para>
        /// <b>What it protects, measured on two logs rather than argued.</b>
        /// <c>tools/play-lan.ps1</c> launched two human clients against the sandbox server with
        /// <c>IRONFRONT_ROLE=client</c>. Both logged <c>[net] role = Client</c> — the X-10
        /// mechanism working — and then started a full authority anyway, because the role
        /// deferral in <c>Awake</c> only declined to CLAIM the role and never declined to START.
        /// Client 1 took UDP 27015 and reported <c>16 player slots will not fit: 51 actors are
        /// already registered</c> and <c>0 claimable player bodies against 16 admitted
        /// connections</c>; client 2 threw an unhandled <c>SocketException</c> out of
        /// <c>Awake</c> because client 1 held the port. Lane B had already met this and worked
        /// around it — <c>LaneBHarness</c>'s own remark says a client "must be CONFIGURED not to
        /// open a socket rather than stripped after it has", which is why
        /// <c>run-lane-b.ps1</c> sets <c>IRONFRONT_GAMESERVER_TRANSPORT=loopback</c> — and that
        /// remark ends by saying the real fix belongs in its own commit. This is it (X-52).
        /// </para>
        /// <para>
        /// <b>Position is part of the rule, not pedantry</b>, exactly as in G11. Everything below
        /// the guard is server startup: <c>ResolveConfiguration</c> parses a port, a slot count
        /// and a shared secret this process will never bind, and the lines after it log a physics
        /// rate as though this were the authority for it. A guard placed lower still stops the
        /// bind and still leaves a client reading and announcing a server's configuration.
        /// </para>
        /// <para>
        /// <b>Not <c>NetContext.IsClient</c>.</b> That property IS the Awake race X-9 closed —
        /// it is settled by whichever of the two <c>-1000</c> bootstraps wakes first, so gating
        /// on it would make an Editor Play session stop hosting depending on component order.
        /// Matching on <c>IsDeclaredClient</c> by name is deliberate: it has exactly one setter,
        /// and a future edit that "simplifies" the condition turns this rule red rather than
        /// quietly reintroducing that dependency.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnguardedDeclaredClientHost(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, DeclaredClientHostScope)) return findings;

            MethodDeclarationSyntax? awake = tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == DeclaredClientHostCaller);

            if (awake == null)
            {
                findings.Add(new GateFinding(
                    "G14", path, 0,
                    $"'{DeclaredClientHostCaller}' is gone from NetServerBootstrap, so the "
                    + "declared-client guard cannot be where it has to be. If the startup moved "
                    + "to another member, move this rule with it rather than deleting it."));
                return findings;
            }

            IfStatementSyntax? guard = awake.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .FirstOrDefault(i =>
                    i.Condition.ToString().Contains(DeclaredClientFlag, StringComparison.Ordinal)
                    && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());

            if (guard == null)
            {
                findings.Add(new GateFinding(
                    "G14", path, LineOf(awake),
                    $"Awake has no 'if (NetContext.{DeclaredClientFlag}) ... return;' guard, so a "
                    + "process launched to JOIN a match hosts one of its own: it binds the UDP "
                    + "port, fills sixteen player bodies and runs a 30 Hz authority beside the "
                    + "server it connected to, and a second client on the same machine dies on a "
                    + "SocketException. AD-1 says there is no host/listen-server mode."));
                return findings;
            }

            InvocationExpressionSyntax? configure = awake.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => NameOfInvoked(i) == DeclaredClientHostConfigure);

            if (configure != null && configure.SpanStart < guard.SpanStart)
            {
                findings.Add(new GateFinding(
                    "G14", path, LineOf(guard),
                    $"the declared-client guard sits AFTER '{DeclaredClientHostConfigure}', so a "
                    + "client still parses and announces a server's port, slot count and shared "
                    + "secret before declining to use any of them. Move the guard above it."));
            }

            return findings;
        }

        private static readonly string[] DeployedViewScope =
            { "/NetClientLocalCombatDriver.cs" };

        /// <summary>The handler for the server's "your body is deployed" message.</summary>
        private const string DeployedViewCaller = "OnSpawnActor";

        /// <summary>The handler for the server's "you are alive again" transition.</summary>
        private const string DeployedViewRespawnCaller = "OnRespawned";

        private const string DeployedViewCall = "EnterDeployedView";

        /// <summary>Exposed so the companion test can assert the rule is in scope at all.</summary>
        public static bool IsDeployedViewScoped(string path) =>
            !IsExcludedFromScan(path) && IsInScope(path, DeployedViewScope);

        /// <summary>
        /// <b>G12</b> — the client leaves the pre-deploy menu view when the server says it is
        /// deployed. Ledger <b>X-48</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this is protecting, and why a rule rather than a test.</b> The defect it pins
        /// was invisible for a week across five runs and 90 captured frames: a networked client
        /// rendered the deploy menu for entire matches while its simulation joined, spawned,
        /// aimed and killed. Nothing failed. Every counter was healthy, every check that reads a
        /// counter passed, and the two checks that read the FRAMES were quietly ungradeable —
        /// which reads like an artifact problem, not a game one. A test cannot pin the behaviour
        /// here: the switch happens in a router callback on a live client, and Unity runs no
        /// <c>Awake</c> outside play mode, which is the trap
        /// <c>DedicatedServerDeclinesLocalClientTests</c> already paid for once.
        /// </para>
        /// <para>
        /// <b>Both callers, not one.</b> The first spawn and a respawn are different code paths —
        /// <c>S_SPAWN_ACTOR</c> and <c>ClientCombatState.OnRespawned</c> — and losing either one
        /// leaves a player looking at the menu for the rest of a life, which is exactly as bad as
        /// losing both and half as likely to be noticed.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindMissingDeployedViewSwitch(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;
            if (!IsInScope(path, DeployedViewScope)) return findings;

            foreach (string caller in new[] { DeployedViewCaller, DeployedViewRespawnCaller })
            {
                MethodDeclarationSyntax? handler = tree.GetRoot()
                    .DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == caller);

                if (handler == null)
                {
                    findings.Add(new GateFinding(
                        "G12", path, 0,
                        $"'{caller}' is gone from NetClientLocalCombatDriver, so the deploy-view "
                        + "switch cannot be where it has to be. If the handler was renamed, move "
                        + "this rule with it rather than deleting it."));
                    continue;
                }

                bool calls = handler.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(i => NameOfInvoked(i) == DeployedViewCall);

                if (calls) continue;

                findings.Add(new GateFinding(
                    "G12", path, LineOf(handler),
                    $"'{caller}' does not call '{DeployedViewCall}', so a networked client stays "
                    + "in the pre-deploy menu view after the server has deployed it: the scenery "
                    + "camera keeps repainting the whole screen at depth 100, the loadout screen "
                    + "is never dismissed and input is never restored. The simulation carries on "
                    + "normally throughout, so no counter and no test goes red — only the frames "
                    + "do, and only to a human who looks at one."));
            }

            return findings;
        }

        /// <summary>
        /// Each registry that hands objects to per-frame consumers, and the removal its owning
        /// type must call from <c>OnDestroy</c>. Ledger <b>X-49</b>.
        /// </summary>
        private static readonly (string File, string Call)[] RegistryDropContracts =
        {
            ("/Actor.cs", "ActorManager.Drop"),
            ("/Vehicle.cs", "ActorManager.DropVehicle"),
        };

        /// <summary>Exposed so the companion test can assert the rule is in scope at all.</summary>
        public static bool IsRegistryDropScoped(string path) =>
            !IsExcludedFromScan(path)
            && RegistryDropContracts.Any(c => IsInScope(path, new[] { c.File }));

        /// <summary>
        /// <b>G13</b> — a type that registers itself with <c>ActorManager</c> deregisters in
        /// <c>OnDestroy</c>. Ledger <b>X-49</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this pins was PRESENT and UNWIRED, which is the shape that survives review.</b>
        /// <c>ActorManager.Drop</c> existed with <b>zero callers</b> while its vehicle twin
        /// <c>DropVehicle</c> was wired, so nothing looked missing — and a destroyed actor stayed
        /// in <c>actors</c> and <c>aliveActors</c> for the rest of the match. Unity's overloaded
        /// <c>==</c> then let each stale entry pass every <c>x != y</c> test in the consumers
        /// before being dereferenced, at a measured 1,012 NullReferenceExceptions across three
        /// clients in one lane-B combat run, from three separate call sites.
        /// </para>
        /// <para>
        /// <b>Checks the method exists AND that it makes the call</b>, because an empty
        /// <c>OnDestroy</c> is exactly as broken as no <c>OnDestroy</c> and looks deliberate.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<GateFinding> FindUnregisteredRegistryDrop(
            SyntaxTree tree, string path)
        {
            var findings = new List<GateFinding>();

            if (IsExcludedFromScan(path)) return findings;

            foreach ((string file, string call) in RegistryDropContracts)
            {
                if (!IsInScope(path, new[] { file })) continue;

                MethodDeclarationSyntax? onDestroy = tree.GetRoot()
                    .DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == "OnDestroy");

                bool drops = onDestroy != null
                    && onDestroy.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(i => i.Expression.ToString().EndsWith(call, StringComparison.Ordinal));

                if (drops) continue;

                findings.Add(new GateFinding(
                    "G13", path, onDestroy != null ? LineOf(onDestroy) : 0,
                    onDestroy == null
                        ? $"no OnDestroy, so a destroyed instance is never removed from "
                          + $"ActorManager and '{call}' is left with no caller. Every per-frame "
                          + "consumer that walks that register then dereferences a destroyed "
                          + "object, and Unity's overloaded == will not stop it."
                        : $"OnDestroy does not call '{call}', so the deregistration it exists for "
                          + "does not happen. An empty OnDestroy reads as deliberate and is as "
                          + "broken as none at all."));
            }

            return findings;
        }
    }
}
