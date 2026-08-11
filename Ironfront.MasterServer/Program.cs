using System;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer
{
    /// <summary>
    /// Entry point stub. OWNER: Dev D — replace with the real listener host.
    /// </summary>
    /// <remarks>
    /// Two traps worth carrying into the real implementation
    /// (dev-d-master-server/phases/phase-00-foundation.md):
    /// set <c>socket.NoDelay = true</c> so Nagle does not add up to 200 ms to every small
    /// lobby reply, and decrement the per-IP connection counter on EVERY exit path or the
    /// count leaks until nobody can connect.
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("Ironfront Master Server — not yet implemented (Dev D).");
            Console.WriteLine($"Protocol version: {ProtocolConstants.PROTOCOL_VERSION}");
            Console.WriteLine($"Default TCP port: read IRONFRONT_MASTER_PORT (see .env.example)");

            if (args.Length > 0)
                Console.WriteLine($"Ignored {args.Length} argument(s) — no CLI surface yet.");

            return 0;
        }
    }
}
