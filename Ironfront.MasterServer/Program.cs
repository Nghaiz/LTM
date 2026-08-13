using System;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Configuration;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.MasterServer.Dispatch;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer
{
    /// <summary>
    /// The master server entry point: load configuration, fail fast if it is wrong, then run
    /// the TCP listener until Ctrl+C.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase-00 acceptance criterion 11 lives here.</b> A missing or too-short
    /// <c>IRONFRONT_SHARED_SECRET</c> makes <see cref="MasterServerConfig.FromEnvironment()"/>
    /// throw, and this method turns that into a printed, actionable message and a non-zero
    /// exit code — the server refuses to start rather than booting with a forgeable key. The
    /// secret is validated but not yet consumed: joinTicket signing arrives in phase 02, and
    /// validating a value phase 00 does not use is the whole point of failing at the boundary
    /// instead of at first use.
    /// </para>
    /// <para>
    /// <see cref="DotEnv.LoadDefault"/> runs first so a local <c>.env</c> populates the
    /// environment for development, but a real environment variable always wins over the file
    /// (see <see cref="DotEnv"/>), which is the direction a VPS unit file needs in phase 03.
    /// </para>
    /// </remarks>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            DotEnv.LoadDefault();

            MasterServerConfig config;
            try
            {
                config = MasterServerConfig.FromEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                // The message from FromEnvironment is written to be the user interface for a
                // misconfigured process — print it and refuse to start.
                MasterLog.Error(ex.Message);
                return 1;
            }

            MasterLog.Level = config.LogLevel;

            MasterLog.Warn($"Ironfront Master Server — protocol v{ProtocolConstants.PROTOCOL_VERSION}, " +
                           $"db {config.DatabasePath}");

            if (args.Length > 0)
                MasterLog.Warn($"ignored {args.Length} argument(s) — no CLI surface yet");

            using var cts = new CancellationTokenSource();

            // Ctrl+C should drain and shut down cleanly, not kill the process mid-write.
            // Cancel the token and let RunAsync's finally close the sockets in order.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                // ReSharper disable once AccessToDisposedClosure — the handler is unhooked
                // when the process exits, and RunAsync owns the lifetime until then.
                if (!cts.IsCancellationRequested) cts.Cancel();
            };

            using var database = new SqliteDatabase(config.DatabasePath);
            var auth = new AuthService(database);
            var lobby = new LobbyService();
            var gameServers = new GameServerRegistry(config.SharedSecret);
            var dispatcher = new MspMessageDispatcher(auth, lobby, gameServers, database, config.SharedSecret);
            var options = new TcpListenerHostOptions { Port = config.Port };
            using var host = new TcpListenerHost(options, dispatcher);

            try
            {
                await host.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C — RunAsync already shut down in its finally block.
            }

            return 0;
        }
    }
}
