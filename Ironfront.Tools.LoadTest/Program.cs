using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>
    /// Entry point stub. OWNER: Dev D — replace with the bot-client driver.
    /// </summary>
    /// <remarks>
    /// Target for M4: 16 simulated clients against one game server, reporting tick time,
    /// bandwidth per client and packet loss.
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("Ironfront Load Test — not yet implemented (Dev D).");
            Console.WriteLine($"Target scale: {ProtocolConstants.MAX_PLAYERS} players + " +
                              $"{ProtocolConstants.MAX_BOTS} bots.");

            if (args.Length > 0)
                Console.WriteLine($"Ignored {args.Length} argument(s) — no CLI surface yet.");

            return 0;
        }
    }
}
