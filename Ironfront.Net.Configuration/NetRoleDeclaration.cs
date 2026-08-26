namespace Ironfront.Net.Configuration
{
    /// <summary>What a process has declared itself to be, before any scene loads.</summary>
    public enum DeclaredNetRole
    {
        /// <summary>Nothing declared one. The bootstraps decide, as they always have.</summary>
        Undeclared = 0,

        /// <summary>This process drives the simulation.</summary>
        Server = 1,

        /// <summary>This process renders and predicts; the server is authoritative.</summary>
        Client = 2,
    }

    /// <summary>
    /// Resolves the role a process declares for itself. Ledger <b>X-10</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes.</b> <c>Dustbowl</c> carries an active <c>NetServer</c> AND an
    /// active <c>NetClient</c>, so every process that loads it runs both bootstraps. Each claims
    /// the role only if the other has not (<c>if (!IsClient) SetRole(Server)</c> and its mirror),
    /// both at execution order -1000 — so with nothing declared, which one wins is Unity's tie to
    /// break. It is not cosmetic: <c>NetClientPresenterGuard.IsPresentable</c> is
    /// <c>NetContext.IsClient</c>, and every presenter guarded by it latches
    /// <c>enabled = false</c> during that same <c>Awake</c> pass and never re-checks. A process
    /// that loses the flip has a dead killfeed, a dead name table and a dead local combat driver
    /// for the rest of its life.
    /// </para>
    /// <para>
    /// <b>Lane B is not affected, and that is exactly the problem.</b> The harness declares a
    /// role at <c>BeforeSceneLoad</c> from <c>IRONFRONT_LANEB_ROLE</c>, ahead of every scene
    /// <c>Awake</c>, so every lane-B run is correct — and the shipped client, which has no such
    /// declaration, is not. A green lane-B run therefore makes this LESS likely to be found, not
    /// more (<c>green-that-proves-nothing.md</c>).
    /// </para>
    /// <para>
    /// <b>What this deliberately does NOT do: change the no-signal default.</b> With nothing
    /// declared it returns <see cref="DeclaredNetRole.Undeclared"/> and the bootstraps behave
    /// exactly as they do today — the server claims it, which is what keeps the Editor sandbox
    /// and offline single-player working (<c>NetServerBootstrap.Awake</c>'s own remark). Whether
    /// a RENDERED process should default to <see cref="DeclaredNetRole.Client"/> is a product
    /// decision about client-only mode, and it is still open; this supplies the mechanism a
    /// shipped client needs in order to say so, and makes the undeclared case audible instead of
    /// silent.
    /// </para>
    /// </remarks>
    public static class NetRoleDeclaration
    {
        /// <summary>The environment variable a shipped build reads. Ledger X-10.</summary>
        /// <remarks>
        /// Distinct from lane B's <c>IRONFRONT_LANEB_ROLE</c> on purpose: that one also installs
        /// the harness, strips a bootstrap and writes checkpoint artifacts. This one only names
        /// a role, so setting it on a player build cannot drag verification scaffolding in.
        /// </remarks>
        public const string RoleVariable = "IRONFRONT_ROLE";

        /// <summary>The command-line form, for a launcher that cannot set an environment.</summary>
        public const string RoleArgument = "-ironfront-role";

        /// <summary>
        /// Resolves the declared role from the signals available before any scene loads.
        /// </summary>
        /// <param name="roleVariable">
        /// The value of <see cref="RoleVariable"/>, or the <see cref="RoleArgument"/> value.
        /// Null or blank when neither was supplied.
        /// </param>
        /// <param name="isBatchMode">Whether this process has no display.</param>
        /// <param name="isDedicatedServerBuild">
        /// Whether this binary was built for the Dedicated Server platform (<c>UNITY_SERVER</c>).
        /// </param>
        /// <remarks>
        /// <para>
        /// Order is explicit-beats-inferred. A human or a launcher that named a role meant it,
        /// and inferring over the top of that is how a staging build silently becomes something
        /// else.
        /// </para>
        /// <para>
        /// <b>An unrecognised value is <see cref="DeclaredNetRole.Undeclared"/>, not a guess.</b>
        /// <c>IRONFRONT_ROLE=sever</c> resolving to Server would be a typo silently honoured;
        /// resolving to Client would be a typo silently inverted. Undeclared falls through to the
        /// same behaviour as setting nothing, which is the one outcome a reader can predict — and
        /// the caller reports it.
        /// </para>
        /// <para>
        /// <b>Batch mode alone is enough</b>, without <c>UNITY_SERVER</c>: the dedicated build
        /// this project ships today is a headless run of the ordinary player, so a check that
        /// required the Dedicated Server platform would infer nothing on the binary that actually
        /// runs. A rendered process is never inferred to be a server, because there is no
        /// headless client and a display is the one signal that cannot be faked by a flag.
        /// </para>
        /// </remarks>
        public static DeclaredNetRole Resolve(
            string? roleVariable, bool isBatchMode, bool isDedicatedServerBuild)
        {
            DeclaredNetRole explicitly = Parse(roleVariable);
            if (explicitly != DeclaredNetRole.Undeclared) return explicitly;

            if (isDedicatedServerBuild || isBatchMode) return DeclaredNetRole.Server;

            return DeclaredNetRole.Undeclared;
        }

        /// <summary>
        /// Whether the resolved state leaves a rendered process racing for its own role.
        /// </summary>
        /// <remarks>
        /// The condition worth warning about is narrow on purpose. A headless process resolves to
        /// Server, so it is never here. An Editor sandbox session IS here — and that is correct,
        /// because it is running the same undeclared listen-server topology a shipped client
        /// runs; the warning is the only thing that makes the difference between "offline
        /// single-player" and "a networked client whose presenters are dead" visible at all.
        /// </remarks>
        public static bool IsUndeclaredRenderedProcess(DeclaredNetRole resolved, bool isBatchMode)
            => resolved == DeclaredNetRole.Undeclared && !isBatchMode;

        /// <summary>Reads a role name. Case- and whitespace-insensitive; anything else is Undeclared.</summary>
        public static DeclaredNetRole Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DeclaredNetRole.Undeclared;

            switch (value.Trim().ToLowerInvariant())
            {
                case "server": return DeclaredNetRole.Server;
                case "client": return DeclaredNetRole.Client;
                default:       return DeclaredNetRole.Undeclared;
            }
        }

        /// <summary>
        /// Pulls the <see cref="RoleArgument"/> value out of a command line, or null.
        /// </summary>
        /// <remarks>
        /// Both spellings, because launchers differ and a form that silently does nothing is
        /// worse than no form: <c>-ironfront-role client</c> (separate token) and
        /// <c>-ironfront-role=client</c>.
        /// </remarks>
        public static string? FromCommandLine(string[]? args)
        {
            if (args == null) return null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null) continue;

                if (arg.StartsWith(RoleArgument + "=", System.StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(RoleArgument.Length + 1);

                if (string.Equals(arg, RoleArgument, System.StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length)
                    return args[i + 1];
            }

            return null;
        }
    }
}
