using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Groups every test class that stands up a real listener, so they run one class at a time
    /// instead of all at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// xUnit runs test collections in parallel, and by default each class is its own collection.
    /// Four classes here open real sockets — and one of them,
    /// <c>Accepts32SimultaneousConnections</c>, opens thirty-two at a go, each with its own
    /// receive loop. Every one of those loops, both host loops, and every <c>Task.Delay</c> in
    /// the tests themselves is a thread-pool continuation, on a runner with four cores that is
    /// also running the rest of the suite.
    /// </para>
    /// <para>
    /// The pool injects new worker threads at roughly one or two a second once it is saturated,
    /// so a continuation can sit for whole seconds. That is what made the timeout sweep miss its
    /// window on #90: the deadline was 200 ms and the budget 3000 ms, and the logic loop still
    /// did not get scheduled in time. A held clock (see <see cref="HeldClock"/>) fixes the
    /// half where the server's clock ran away from the test; this fixes the half where the
    /// server's loop does not get to run at all.
    /// </para>
    /// <para>
    /// The cost is that these classes no longer overlap each other. They still overlap every
    /// pure-logic class in the assembly, which is most of it, and the socket tests are mostly
    /// short waits rather than CPU — so this buys determinism for very little wall clock.
    /// </para>
    /// </remarks>
    [CollectionDefinition(Name)]
    public sealed class SocketTestCollection
    {
        public const string Name = "real-sockets";
    }
}
