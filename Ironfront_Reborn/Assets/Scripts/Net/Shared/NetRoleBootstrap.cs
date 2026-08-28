using System;
using Ironfront.Net.Configuration;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Declares this process's role before any scene loads. Ledger <b>X-10</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was missing.</b> <c>Dustbowl</c> carries an active <c>NetServer</c> AND an active
    /// <c>NetClient</c>, so every process that loads it runs both bootstraps, and each claims the
    /// role only if the other has not — <c>NetClientBootstrap</c>'s
    /// <c>if (!NetContext.IsServer)</c> against <c>NetServerBootstrap</c>'s
    /// <c>if (!NetContext.IsClient)</c>, both at execution order -1000. With nothing declared,
    /// which one wins is Unity's tie to break. It is not cosmetic:
    /// <c>NetClientPresenterGuard.IsPresentable</c> is <c>NetContext.IsClient</c>, and every
    /// presenter behind it latches <c>enabled = false</c> during that same <c>Awake</c> pass and
    /// never re-checks, so a process that loses the flip has a dead killfeed, a dead name table
    /// and a dead local combat driver for the rest of its life.
    /// </para>
    /// <para>
    /// <b>Lane B was never affected, and that is what kept this hidden.</b>
    /// <c>LaneBHarness.DeclareRole</c> declares from <c>IRONFRONT_LANEB_ROLE</c> at
    /// <c>BeforeSceneLoad</c>, ahead of every scene <c>Awake</c> — so every lane-B run is correct
    /// and the shipped client, which had no equivalent, is not. A green lane-B run makes this
    /// LESS likely to be found rather than more (<c>green-that-proves-nothing.md</c>).
    /// </para>
    /// <para>
    /// <b>This does NOT change the no-declaration default, deliberately.</b> A rendered process
    /// with nothing set resolves to <c>Undeclared</c> and the bootstraps decide exactly as they
    /// always have — which is what keeps offline single-player and the Editor sandbox working,
    /// per <c>NetServerBootstrap.Awake</c>'s own remark. Whether a rendered process should
    /// default to <c>Client</c> is the client-only-mode product decision, and it is still open.
    /// What changes here is that a shipped client now HAS a way to say so, and that the
    /// undeclared case is announced instead of being a silent coin flip.
    /// </para>
    /// <para>
    /// <b>Ordering against lane B does not matter, and it is worth saying why.</b> Both run at
    /// <c>BeforeSceneLoad</c> and Unity orders them arbitrarily. If lane B goes first this
    /// returns immediately (the role is no longer <c>Offline</c>); if this goes first, lane B's
    /// unconditional <c>SetRole</c> overwrites it. Either way lane B wins, and nothing has read
    /// the role in between — the first <c>Awake</c> has not run yet.
    /// </para>
    /// </remarks>
    public static class NetRoleBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Declare()
        {
            // Something already declared — lane B, or a test. Deferring rather than re-deciding
            // is what makes the ordering above irrelevant.
            if (NetContext.Role != NetRole.Offline) return;

            string named = Environment.GetEnvironmentVariable(NetRoleDeclaration.RoleVariable);
            if (string.IsNullOrWhiteSpace(named))
                named = NetRoleDeclaration.FromCommandLine(Environment.GetCommandLineArgs());

            bool dedicatedServerBuild =
#if UNITY_SERVER
                true;
#else
                false;
#endif

            DeclaredNetRole resolved = NetRoleDeclaration.Resolve(
                named, Application.isBatchMode, dedicatedServerBuild);

            switch (resolved)
            {
                case DeclaredNetRole.Server:
                    NetContext.SetRole(NetRole.Server);
                    return;

                case DeclaredNetRole.Client:
                    // Both, and they are not the same statement. SetRole says what this process
                    // IS RUNNING and is free to be overwritten by a bootstrap; DeclareClientProcess
                    // says what this process WAS LAUNCHED AS, which NetServerBootstrap reads to
                    // decline. Setting only the role left a declared client still binding UDP
                    // 27015 and running a sixteen-slot authority beside the server it had joined
                    // (X-52).
                    NetContext.SetRole(NetRole.Client);
                    NetContext.DeclareClientProcess();
                    return;
            }

            if (!NetRoleDeclaration.IsUndeclaredRenderedProcess(resolved, Application.isBatchMode))
                return;

            // Once, at startup, before anything can have latched. The role itself is unchanged;
            // what this buys is that "my killfeed is dead" stops being indistinguishable from
            // "there is nothing to show" (ledger X-10).
            Debug.LogWarning(
                "[net] this rendered process declared no role, so whichever of NetClientBootstrap "
                + "and NetServerBootstrap Unity Awakes first decides it. Dustbowl runs both. If "
                + "the server wins, NetClientPresenterGuard.IsPresentable is false for the whole "
                + "session and the killfeed, name table and local combat driver stay disabled. "
                + $"Set {NetRoleDeclaration.RoleVariable}=client (or pass "
                + $"{NetRoleDeclaration.RoleArgument}=client) on a client build. Offline "
                + "single-player is unaffected and needs nothing (ledger X-10).");
        }
    }
}
