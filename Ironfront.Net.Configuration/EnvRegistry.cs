using System;
using System.Collections.Generic;
using System.Text;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// Every <c>IRONFRONT_*</c> variable the repository understands, declared once.
    /// <c>.env.example</c> is generated from this list and a test fails when the two drift.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the single source of truth, and the file on disk is the artefact.</b> The
    /// previous arrangement was the other way round — the file was hand-written and the code
    /// was expected to keep up — and it did not: <c>IRONFRONT_GAMESERVER_UDP_PORT</c> was
    /// documented and read by nothing, while six variables that <c>tools/backup.sh</c>,
    /// <c>tools/alert.sh</c> and <c>tools/deploy.sh</c> genuinely read were in no template at
    /// all. Both failures are unrepresentable now: a variable that is not here does not reach
    /// the template, and a variable that is here reaches it automatically.
    /// </para>
    /// <para>
    /// <b>What does NOT belong here.</b> Protocol constants — tick rate, movement speeds,
    /// interpolation delay, the reliable window, anything in <c>MovementCore</c> — are not
    /// configuration and must never become variables. The client predicts using those exact
    /// numbers; a server configured with a different one produces a permanent misprediction
    /// that surfaces as rubber-banding at the client, nowhere near the machine whose
    /// configuration is wrong. Match rules (tickets, warm-up, bleed rate) are game design and
    /// belong in a data file a designer can edit, not in an operator's <c>.env</c>.
    /// </para>
    /// </remarks>
    public static class EnvRegistry
    {
        // ---- Identity and secrets ------------------------------------------------------

        /// <summary>The HMAC key that signs join tickets. No default, on purpose.</summary>
        public static readonly EnvVar SharedSecret = new EnvVar(
            "IRONFRONT_SHARED_SECRET", "Identity and secrets", "master server, game server",
            "The HMAC key that signs joinTickets. The master server (Dev D) issues tickets with\n" +
            "it and the game server (Dev C) verifies them with it — both processes must be\n" +
            "configured with the SAME value, or every CONNECT_REQUEST is rejected with\n" +
            "CONNECT_DENIED reason 3.\n" +
            "\n" +
            "There is no default and there will not be one: a development default follows you to\n" +
            "production, and a shared key that reached a git history is not a key any more. The\n" +
            "master server refuses to start without it; the game server stays standalone.\n" +
            "\n" +
            "Generate one with:\n" +
            "  [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))",
            secret: true);

        // ---- Master server -------------------------------------------------------------

        /// <summary>The master's TCP port, dialled by clients and by game servers alike.</summary>
        public static readonly EnvVar MasterPort = new EnvVar(
            "IRONFRONT_MASTER_PORT", "Master server", "master server, game server",
            "The master's single TCP port. Clients and game servers both dial it — GS_REGISTER\n" +
            "travels the same MSP connection as a player login, so there is no second port to\n" +
            "configure and a game server pointed at one will simply never reach the master.",
            "27000");

        /// <summary>Host name or address of the master, from a game server's point of view.</summary>
        public static readonly EnvVar MasterHost = new EnvVar(
            "IRONFRONT_MASTER_HOST", "Master server", "game server",
            "Where a game server finds the master. EMPTY MEANS STANDALONE: no connection, no\n" +
            "advertisement, matches still play to completion. That is the phase-03 contingency\n" +
            "for the master not being up, and it is deliberately what you get by doing nothing.");

        /// <summary>SQLite file backing accounts, sessions and rooms.</summary>
        public static readonly EnvVar DatabasePath = new EnvVar(
            "IRONFRONT_DB_PATH", "Master server", "master server, tools/backup.sh",
            "The SQLite file holding accounts, sessions and the room registry.",
            "./ironfront.db");

        // ---- Game server ---------------------------------------------------------------

        /// <summary>UDP port the game server binds and advertises.</summary>
        public static readonly EnvVar GameServerUdpPort = new EnvVar(
            "IRONFRONT_GAMESERVER_UDP_PORT", "Game server", "game server",
            "The UDP port the game server binds AND the port it advertises to the master, which\n" +
            "are necessarily the same number — clients dial what the matchmaker hands them.\n" +
            "Overrides the Unity inspector field, so a second instance on one host needs no\n" +
            "second scene.",
            "27015");

        /// <summary>Transport selection: the real socket, or the in-process wire.</summary>
        public static readonly EnvVar GameServerTransport = new EnvVar(
            "IRONFRONT_GAMESERVER_TRANSPORT", "Game server", "game server",
            "udp | loopback. The loopback wire is an in-process pipe with no socket, for driving\n" +
            "both ends from one Editor; anything reachable over a network needs udp. A headless\n" +
            "build left on loopback starts cleanly, logs nothing unusual and accepts nobody,\n" +
            "which is why this is settable without opening the scene.",
            "udp");

        /// <summary>Connection slots on the game server's transport.</summary>
        public static readonly EnvVar GameServerMaxConnections = new EnvVar(
            "IRONFRONT_GAMESERVER_MAX_CONNECTIONS", "Game server", "game server",
            "Transport-level connection slots. Keep it at or above the advertised player count —\n" +
            "the difference is spectators and reconnect churn, not headroom for a flood.",
            "16");

        /// <summary>Player count the matchmaker fills.</summary>
        public static readonly EnvVar GameServerMaxPlayers = new EnvVar(
            "IRONFRONT_GAMESERVER_MAX_PLAYERS", "Game server", "game server",
            "The player count advertised to the master, which is what the matchmaker fills.\n" +
            "0..255: the wire carries it as a byte.",
            "16");

        /// <summary>Address handed to clients, when it differs from the one the master sees.</summary>
        public static readonly EnvVar GameServerPublicIp = new EnvVar(
            "IRONFRONT_GAMESERVER_PUBLIC_IP", "Game server", "game server",
            "The address clients dial. Empty means the master infers it from the connection,\n" +
            "which is right on a plain VPS and wrong behind NAT or a port-forward — there the\n" +
            "master sees the gateway and the clients need the mapped address.");

        /// <summary>Maps this server can host.</summary>
        public static readonly EnvVar GameServerMapIds = new EnvVar(
            "IRONFRONT_GAMESERVER_MAP_IDS", "Game server", "game server",
            "Comma-separated map ids this server can host, driving the matchmaker's\n" +
            "preferred-map filter. Empty means no preference.");

        /// <summary>Development shortcut: admit tickets nobody signed.</summary>
        public static readonly EnvVar GameServerAcceptUnsignedTickets = new EnvVar(
            "IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS", "Game server", "game server",
            "Accept any join ticket when no shared secret is configured. DEVELOPMENT ONLY: it\n" +
            "bypasses the protocol-spec section 12 HMAC check, so a public server running with\n" +
            "it on is a server anyone can join as anyone. Set it to 0 on anything reachable\n" +
            "from the Internet; the server then refuses every connection until the secret is\n" +
            "present, which is the failure you want.",
            "1");

        // ---- Game client ---------------------------------------------------------------

        /// <summary>Game server the client dials.</summary>
        public static readonly EnvVar ClientHost = new EnvVar(
            "IRONFRONT_CLIENT_HOST", "Game client", "game client",
            "The game server a client build connects to, for a headless or scripted run. In the\n" +
            "Editor the inspector field is the convenient one and wins whenever this is unset.",
            "127.0.0.1");

        /// <summary>Game server port the client dials.</summary>
        public static readonly EnvVar ClientPort = new EnvVar(
            "IRONFRONT_CLIENT_PORT", "Game client", "game client",
            "Matches the game server's IRONFRONT_GAMESERVER_UDP_PORT.",
            "27015");

        /// <summary>Client-side connection logging.</summary>
        public static readonly EnvVar ClientVerbose = new EnvVar(
            "IRONFRONT_CLIENT_VERBOSE", "Game client", "game client",
            "Log the first snapshot and every connection state change.",
            "1");

        // ---- Logging -------------------------------------------------------------------

        /// <summary>Master server verbosity.</summary>
        public static readonly EnvVar LogLevel = new EnvVar(
            "IRONFRONT_LOG_LEVEL", "Logging", "master server",
            "Error | Warn | Debug. Debug logs per-connection and per-frame detail and will fill\n" +
            "a VPS disk in days — keep it at Warn outside an investigation.",
            "Warn");

        /// <summary>JSON event stream on stdout.</summary>
        public static readonly EnvVar StructuredLog = new EnvVar(
            "IRONFRONT_STRUCTURED_LOG", "Logging", "master server",
            "1 emits one JSON object per line on stdout beside the human log, for jq and log\n" +
            "aggregation. Values registered as secrets are redacted before writing.",
            "0");

        // ---- TLS -----------------------------------------------------------------------

        /// <summary>PKCS#12 bundle the master presents.</summary>
        public static readonly EnvVar TlsCertificatePath = new EnvVar(
            "IRONFRONT_TLS_CERT_PATH", "TLS", "master server",
            "Empty means PLAINTEXT. That is fine on a LAN and NOT fine on the Internet: the wire\n" +
            "carries a password hash and a session token, and to the server the hash IS the\n" +
            "password — anyone who captures it can log in as that account.\n" +
            "\n" +
            "Generate a certificate and get the fingerprint to pin in the client:\n" +
            "  ./tools/new-dev-cert.ps1 -Subject <hostname> -AlsoValidFor <ip>");

        /// <summary>Password for the PKCS#12 bundle.</summary>
        public static readonly EnvVar TlsCertificatePassword = new EnvVar(
            "IRONFRONT_TLS_CERT_PASSWORD", "TLS", "master server",
            "The certificate bundle's password. Registered for redaction at startup and never\n" +
            "printed.",
            secret: true);

        // ---- Metrics and durability ----------------------------------------------------

        /// <summary>Metrics endpoint port. 0 disables.</summary>
        public static readonly EnvVar MetricsPort = new EnvVar(
            "IRONFRONT_METRICS_PORT", "Metrics and durability", "master server, tools/alert.sh",
            "`nc 127.0.0.1 27001` prints a JSON snapshot. 0 disables the endpoint. It must differ\n" +
            "from IRONFRONT_MASTER_PORT or the second listener fails to bind.",
            "27001");

        /// <summary>Metrics endpoint bind address.</summary>
        public static readonly EnvVar MetricsBind = new EnvVar(
            "IRONFRONT_METRICS_BIND", "Metrics and durability", "master server",
            "The bind address stays on loopback deliberately: the payload is unauthenticated and\n" +
            "tells anyone who can reach it how many players are online and whether a game server\n" +
            "is down. Reach it over the SSH session you already have.",
            "127.0.0.1");

        /// <summary>Host the alert script polls.</summary>
        public static readonly EnvVar MetricsHost = new EnvVar(
            "IRONFRONT_METRICS_HOST", "Metrics and durability", "tools/alert.sh",
            "Where tools/alert.sh looks for the metrics endpoint. Loopback, for the same reason\n" +
            "the endpoint binds there — the script runs from cron on the same box.",
            "127.0.0.1");

        /// <summary>Durability CSV path.</summary>
        public static readonly EnvVar MetricsCsvPath = new EnvVar(
            "IRONFRONT_METRICS_CSV", "Metrics and durability", "master server",
            "One CSV row per interval, for the durability chart (tools/chart-durability.ps1).\n" +
            "Empty disables sampling.");

        /// <summary>Seconds between durability CSV rows.</summary>
        public static readonly EnvVar MetricsCsvIntervalSeconds = new EnvVar(
            "IRONFRONT_METRICS_CSV_INTERVAL_SEC", "Metrics and durability", "master server",
            "Seconds between durability CSV rows.",
            "60");

        // ---- Limits --------------------------------------------------------------------

        /// <summary>Per-IP connection cap on the master.</summary>
        public static readonly EnvVar MaxConnectionsPerIp = new EnvVar(
            "IRONFRONT_MAX_CONNECTIONS_PER_IP", "Limits", "master server",
            "The defaults below are sized for real players and are correct for production.\n" +
            "\n" +
            "They are also what makes a single-machine load test impossible, because every bot\n" +
            "shares one source address: with the defaults, a 16-client run gets 5 connections and\n" +
            "5 logins and 11 errors. Raise them ONLY on a test rig:\n" +
            "\n" +
            "  IRONFRONT_MAX_CONNECTIONS_PER_IP=200\n" +
            "  IRONFRONT_LOGIN_RATE_PER_MINUTE=500",
            "5");

        /// <summary>Global connection cap on the master. 0 disables.</summary>
        public static readonly EnvVar MaxTotalConnections = new EnvVar(
            "IRONFRONT_MAX_TOTAL_CONNECTIONS", "Limits", "master server",
            "Global connection cap. 0 removes it.",
            "256");

        /// <summary>Per-IP login attempts per minute.</summary>
        public static readonly EnvVar LoginRatePerMinute = new EnvVar(
            "IRONFRONT_LOGIN_RATE_PER_MINUTE", "Limits", "master server",
            "Per-IP login attempts per minute.",
            "5");

        // ---- Diagnostics ---------------------------------------------------------------

        /// <summary>Packet capture path.</summary>
        public static readonly EnvVar PacketCapturePath = new EnvVar(
            "IRONFRONT_PCAP", "Diagnostics", "game server, game client, tools/PacketReplay",
            "Write every datagram to this file, for Ironfront.Tools.PacketReplay. Empty disables\n" +
            "capture. It records payloads verbatim, so treat a capture from a live server as\n" +
            "sensitive.");

        /// <summary>Network-condition simulator preset.</summary>
        public static readonly EnvVar Simulator = new EnvVar(
            "IRONFRONT_SIM", "Diagnostics", "game server, game client",
            "Impair the local network on purpose: a preset name from SimulatorConfig (for example\n" +
            "wifi, mobile, terrible). Empty or unrecognised means no simulation — a typo must not\n" +
            "silently degrade a real server.");

        /// <summary>Simulator RNG seed.</summary>
        public static readonly EnvVar SimulatorSeed = new EnvVar(
            "IRONFRONT_SIM_SEED", "Diagnostics", "game server, game client",
            "Seeds the simulator's RNG so a bad run reproduces. Empty means a fresh seed each\n" +
            "start.");

        // ---- Deployment scripts --------------------------------------------------------

        /// <summary>Install root on the server.</summary>
        public static readonly EnvVar InstallRoot = new EnvVar(
            "IRONFRONT_ROOT", "Deployment scripts", "tools/backup.sh",
            "Install root on the VPS: where the database and the backups live.",
            "/opt/ironfront");

        /// <summary>Install root as seen by the deploy script.</summary>
        public static readonly EnvVar RemoteRoot = new EnvVar(
            "IRONFRONT_REMOTE_ROOT", "Deployment scripts", "tools/deploy.sh",
            "The same directory as IRONFRONT_ROOT, named from the deploying machine's side.",
            "/opt/ironfront");

        /// <summary>Backup destination.</summary>
        public static readonly EnvVar BackupDir = new EnvVar(
            "IRONFRONT_BACKUP_DIR", "Deployment scripts", "tools/backup.sh",
            "Where dumps are written. Defaults to a backups/ directory under IRONFRONT_ROOT,\n" +
            "which is on the same disk as the database — point it elsewhere if the backup is\n" +
            "meant to survive losing that disk.");

        /// <summary>Backup retention.</summary>
        public static readonly EnvVar BackupRetentionDays = new EnvVar(
            "IRONFRONT_BACKUP_RETENTION_DAYS", "Deployment scripts", "tools/backup.sh",
            "Days of dumps to keep. Older ones are deleted on the next run.",
            "7");

        // ---- Alerting ------------------------------------------------------------------

        /// <summary>Webhook the alert script posts to.</summary>
        public static readonly EnvVar AlertWebhook = new EnvVar(
            "IRONFRONT_ALERT_WEBHOOK", "Alerting", "tools/alert.sh",
            "Where tools/alert.sh posts, run from cron. Empty means alerts are computed and not\n" +
            "delivered, which is the same as no alerting.",
            secret: true);

        /// <summary>Error-rate alert threshold.</summary>
        public static readonly EnvVar AlertErrorsPerMinute = new EnvVar(
            "IRONFRONT_ALERT_ERRORS_PER_MIN", "Alerting", "tools/alert.sh",
            "Errors per minute above which an alert fires.",
            "10");

        /// <summary>Memory-growth alert threshold.</summary>
        public static readonly EnvVar AlertRssGrowthPercent = new EnvVar(
            "IRONFRONT_ALERT_RSS_GROWTH_PERCENT", "Alerting", "tools/alert.sh",
            "Resident-memory growth, as a percentage of the first sample, that fires a leak\n" +
            "alert.",
            "50");

        /// <summary>Where the alert script remembers what it already sent.</summary>
        public static readonly EnvVar AlertStatePath = new EnvVar(
            "IRONFRONT_ALERT_STATE", "Alerting", "tools/alert.sh",
            "Where the alert script remembers the previous sample, so it reports a change rather\n" +
            "than a level and does not repeat itself every minute.",
            "/tmp/ironfront-alert-state");

        /// <summary>Every declared variable, in <c>.env.example</c> order.</summary>
        public static readonly IReadOnlyList<EnvVar> All = new[]
        {
            SharedSecret,
            MasterPort, MasterHost, DatabasePath,
            GameServerUdpPort, GameServerTransport, GameServerMaxConnections, GameServerMaxPlayers,
            GameServerPublicIp, GameServerMapIds, GameServerAcceptUnsignedTickets,
            ClientHost, ClientPort, ClientVerbose,
            LogLevel, StructuredLog,
            TlsCertificatePath, TlsCertificatePassword,
            MetricsPort, MetricsBind, MetricsHost, MetricsCsvPath, MetricsCsvIntervalSeconds,
            MaxConnectionsPerIp, MaxTotalConnections, LoginRatePerMinute,
            PacketCapturePath, Simulator, SimulatorSeed,
            InstallRoot, RemoteRoot, BackupDir, BackupRetentionDays,
            AlertWebhook, AlertErrorsPerMinute, AlertRssGrowthPercent, AlertStatePath,
        };

        /// <summary>Looks a variable up by name, or null when it is not declared.</summary>
        public static EnvVar? Find(string name)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (string.Equals(All[i].Name, name, StringComparison.Ordinal)) return All[i];
            }

            return null;
        }

        /// <summary>
        /// Renders <c>.env.example</c>. The file in the repository root is this method's
        /// output, and <c>EnvExampleTests</c> fails when it is not.
        /// </summary>
        /// <remarks>
        /// Line endings are LF unconditionally. <c>.gitattributes</c> normalises the file
        /// anyway, and comparing rendered text against a file read from disk on a Windows
        /// checkout is otherwise a test that fails for reasons nobody cares about.
        /// </remarks>
        public static string RenderEnvExample()
        {
            var text = new StringBuilder();

            AppendComment(text,
                "Ironfront Reborn — environment template.\n" +
                "COMMIT this file. NEVER commit a real .env (see conventions.md section 1.4).\n" +
                "\n" +
                "GENERATED from Ironfront.Net.Configuration/EnvRegistry.cs — do not hand-edit.\n" +
                "Add the variable there and regenerate:\n" +
                "\n" +
                "  IRONFRONT_WRITE_ENV_EXAMPLE=1 dotnet test Ironfront.Net.Configuration.Tests\n" +
                "\n" +
                "A variable that is not in the registry does not reach this file, and one that is\n" +
                "reaches it automatically — which is what stops a documented setting from being\n" +
                "read by nothing, and a setting that is read from being documented nowhere.\n" +
                "\n" +
                "Every value below is the DEFAULT the code already uses. Uncommented lines are\n" +
                "here to be found and changed, not because they must be set.");

            string? section = null;

            foreach (EnvVar variable in All)
            {
                if (!string.Equals(section, variable.Section, StringComparison.Ordinal))
                {
                    section = variable.Section;
                    text.Append('\n');
                    text.Append("# --- ").Append(section).Append(' ');
                    text.Append('-', Math.Max(3, 76 - section.Length - 7));
                    text.Append('\n');
                }
                else
                {
                    text.Append('\n');
                }

                AppendComment(text, "Read by: " + variable.ReadBy);
                if (variable.Comment.Length > 0) AppendComment(text, variable.Comment);

                text.Append(variable.Name).Append('=').Append(variable.DefaultValue).Append('\n');
            }

            return text.ToString();
        }

        private static void AppendComment(StringBuilder text, string comment)
        {
            foreach (string line in comment.Split('\n'))
            {
                if (line.Length == 0) text.Append("#\n");
                else text.Append("# ").Append(line).Append('\n');
            }
        }
    }
}
