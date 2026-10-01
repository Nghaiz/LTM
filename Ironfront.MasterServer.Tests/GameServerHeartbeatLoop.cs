using System;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Heartbeats a test game server once a second, as a real one does, so the master keeps it
    /// allocatable for as long as the test needs it.
    /// </summary>
    /// <remarks>
    /// The master stops allocating a game server whose last heartbeat is more than 15 s old
    /// (<c>GameServerRecord.IsHealthy</c>). A test that registered one, then signed two accounts
    /// in, created a room and joined it, ran past that on a slow Windows runner (18 s, CI of
    /// 2026-10-01): the join answered <c>NoGameServerAvailable</c> (3000), and the test failed
    /// for a reason that was never its subject. Sends are serialised by the link's own write
    /// lock, so a background loop is safe beside the test's own calls.
    /// </remarks>
    internal sealed class GameServerHeartbeatLoop : IAsyncDisposable
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _loop;

        public GameServerHeartbeatLoop(GameServerLink link, ushort serverId)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));

            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    link.Heartbeat(new GameServerHeartbeat { ServerId = serverId });
                    try
                    {
                        await Task.Delay(Interval, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
