using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests.Net
{
    /// <summary>
    /// The byte-stream behaviours the existing suite left uncovered, and the Slowloris
    /// deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pre-existing host test writes one whole 6-byte heartbeat per <c>Write</c> call,
    /// so the stream always happened to be perfectly aligned. Deleting the <c>while</c> loop
    /// that drains glued frames, or the entire over-long-frame close path, broke none of them.
    /// The codec itself is well covered by <c>MspFramingTests</c>; what was untested was this
    /// project's WIRING of it — which is where a TCP server actually goes wrong, because TCP
    /// has no message boundaries and a local loopback write almost always arrives whole.
    /// </para>
    /// <para>
    /// These push deliberately misaligned bytes through a real socket: one frame across many
    /// writes, several frames in one write, and a header torn in half.
    /// </para>
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public sealed class TcpStreamFramingTests
    {
        private const MspMessageType Heartbeat = MspMessageType.Heartbeat;

        [Fact]
        public async Task ThreeFramesGluedIntoOneWriteAreAllDelivered()
        {
            // Phase-00 trap 1. Draining exactly one frame per receive leaves the other two
            // sitting in the buffer, and if the client is waiting on a reply to the third
            // nothing further arrives to shake them loose — a deadlock that only appears once
            // a client is fast enough to glue its sends, i.e. in production.
            await using var harness = new MasterHostHarness();
            TcpClient client = await harness.ConnectAsync();

            byte[] glued = Concat(
                Frame(Heartbeat, "{\"n\":1}"),
                Frame(Heartbeat, "{\"n\":2}"),
                Frame(Heartbeat, "{\"n\":3}"));

            await client.GetStream().WriteAsync(glued);

            bool got = await MasterHostHarness.WaitUntilAsync(
                () => harness.Host.TotalFramesReceived >= 3);

            Assert.True(got, $"only {harness.Host.TotalFramesReceived} of 3 glued frames arrived");
        }

        [Fact]
        public async Task AFrameSplitAcrossManyWritesIsReassembled()
        {
            await using var harness = new MasterHostHarness();
            TcpClient client = await harness.ConnectAsync();

            byte[] frame = Frame(Heartbeat, "{\"split\":true}");
            NetworkStream stream = client.GetStream();

            // One byte at a time, which is the shape a Slowloris client and a badly congested
            // link both produce.
            for (int i = 0; i < frame.Length; i++)
            {
                await stream.WriteAsync(frame.AsMemory(i, 1));
                await stream.FlushAsync();
            }

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalFramesReceived >= 1),
                "a frame delivered one byte per write was never assembled");
        }

        [Fact]
        public async Task AFrameWhoseLengthPrefixIsTornInHalfIsReassembled()
        {
            // The worst split: the reader has two bytes of a four-byte length prefix and must
            // not act on them.
            await using var harness = new MasterHostHarness();
            TcpClient client = await harness.ConnectAsync();

            byte[] frame = Frame(Heartbeat, "{\"x\":1}");
            NetworkStream stream = client.GetStream();

            await stream.WriteAsync(frame.AsMemory(0, 2));
            await stream.FlushAsync();
            await Task.Delay(120);
            await stream.WriteAsync(frame.AsMemory(2));
            await stream.FlushAsync();

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalFramesReceived >= 1),
                "a frame split inside its length prefix was never assembled");
        }

        [Fact]
        public async Task AFrameGluedOntoThePartialTailOfThePreviousOneIsDelivered()
        {
            // The combination the two tests above miss individually: a write that finishes one
            // frame AND starts the next. An implementation that resets its buffer per receive
            // instead of carrying the remainder loses the second one.
            await using var harness = new MasterHostHarness();
            TcpClient client = await harness.ConnectAsync();

            byte[] first = Frame(Heartbeat, "{\"a\":1}");
            byte[] second = Frame(Heartbeat, "{\"b\":2}");
            NetworkStream stream = client.GetStream();

            await stream.WriteAsync(first.AsMemory(0, 3));
            await stream.FlushAsync();
            await Task.Delay(80);

            await stream.WriteAsync(Concat(first.AsSpan(3).ToArray(), second));
            await stream.FlushAsync();

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalFramesReceived >= 2),
                $"expected 2 frames, got {harness.Host.TotalFramesReceived}");
        }

        [Fact]
        public async Task AnOverLongDeclaredLengthClosesTheConnection()
        {
            // protocol-spec.md section 10. The declared length is attacker-controlled, so the
            // only safe response to one above the cap is to stop reading the stream — and the
            // client has to actually observe the close, not just have it logged.
            await using var harness = new MasterHostHarness();
            TcpClient client = await harness.ConnectAsync();

            var oversize = new byte[MspFrame.MinFrameSize];
            Endian.WriteU32BE(oversize, 0, ProtocolConstants.MSP_MAX_FRAME_LENGTH + 1);
            Endian.WriteU16LE(oversize, MspFrame.LengthPrefixSize, (ushort)Heartbeat);

            await client.GetStream().WriteAsync(oversize);

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0),
                "the host kept a connection that declared a frame above the 64 KB cap");

            // Read returning 0 is the peer's FIN — proof the socket was closed rather than
            // merely forgotten by the host while the client sat connected to nothing.
            int read = await client.GetStream().ReadAsync(new byte[16]);
            Assert.Equal(0, read);
        }

        [Fact]
        public async Task AnOverLongDeclaredLengthAllocatesNothingFirst()
        {
            // The defense is worthless if the cap is checked after the buffer is sized from the
            // declared length. 4 GB declared, six bytes sent.
            await using var harness = new MasterHostHarness();
            TcpClient client = await harness.ConnectAsync();

            var absurd = new byte[MspFrame.MinFrameSize];
            Endian.WriteU32BE(absurd, 0, uint.MaxValue);
            Endian.WriteU16LE(absurd, MspFrame.LengthPrefixSize, (ushort)Heartbeat);

            await client.GetStream().WriteAsync(absurd);

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0),
                "a 4 GB declared length did not close the connection");
        }

        [Fact]
        public async Task ADribblingClientIsClosedOnTheDeadlineNotTheIdleGap()
        {
            // The Slowloris defense, and the bug it did not have before this test existed.
            //
            // The unauthenticated timeout used to be measured from the last byte received,
            // which is not a defense: the attack is a client that stays just busy enough to
            // look alive while never completing a frame or authenticating, so a clock any byte
            // resets is a clock the attacker owns. Measured against the real server before the
            // fix, one byte every 20 s held a slot for 89 s against a 30 s limit, and would
            // have held it indefinitely.
            //
            // The deadline runs from accept, so dribbling cannot extend it.
            //
            // Held clock, because on a real one this test could go green for the wrong reason.
            // It asserts that a connection IS reaped, and a stalled CI runner reaps it whether
            // the deadline works or not — so the pass would prove nothing about the defense.
            // Stepping the clock ourselves makes the reap attributable to the 30 s deadline and
            // to nothing else.
            var clock = new HeldClock();
            await using var harness = new MasterHostHarness(o =>
            {
                o.Clock                  = clock;
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);
            });

            TcpClient client = await harness.ConnectAsync();
            NetworkStream stream = client.GetStream();

            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            // Open a frame that is valid and will never finish: a length prefix announcing a
            // 64-byte body, followed by that body one byte at a time. This is the attack as
            // described — always mid-message, never complete — and it is why the dribble cannot
            // just be raw 0x00 bytes. Four zero bytes ARE a complete length prefix, declaring a
            // zero-length frame, and the reader closes the connection on it as malformed. The
            // test would then be measuring the parser, not the deadline.
            await stream.WriteAsync(LengthPrefix(MspFrame.MsgTypeSize + 64));
            await stream.FlushAsync();

            // Five dribbles at 5 s apart — 25 s, deliberately INSIDE the 30 s deadline, so the
            // connection is alive for every one of them and each byte provably lands.
            //
            // Waiting for the server to record each byte is what makes this conclusive, and
            // leaving it out is a trap worth naming: if the bytes are all ingested early and the
            // clock is then stepped in one jump, the idle gap grows just as much as the deadline
            // does, and the old buggy code reaps the connection too. The test goes green having
            // proven nothing. Confirming each byte pins the idle gap at 5 s, so only the
            // deadline can produce a reap.
            for (int i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(5));

                await stream.WriteAsync(new byte[] { 0x00 });
                await stream.FlushAsync();

                long stampedAt = clock.NowMs;
                Assert.True(
                    await MasterHostHarness.WaitUntilAsync(
                        async () => await LastActivityMsAsync(harness) >= stampedAt),
                    $"dribbled byte {i + 1} of 5 never reached the server");
            }

            Assert.Equal(1, harness.Host.ConnectionCount);
            Assert.Equal(0, harness.Host.TotalFramesReceived);   // still mid-frame, as intended

            // 25 s of dribbling has reset the idle clock five times. Now cross the deadline. The
            // idle gap is 5 s and the deadline is 30 s, so exactly one of the two can reap this.
            clock.Advance(TimeSpan.FromSeconds(6));

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0),
                "a client that dribbled a byte every 5 s survived its 30 s deadline — the "
                + "unauthenticated timeout is an idle gap, not a deadline");
        }

        [Fact]
        public async Task AnAuthenticatedConnectionStillUsesTheIdleClock()
        {
            // The other half of the same rule, and the reason the deadline is not applied to
            // everybody: once a client has logged in, HEARTBEAT is exactly how it says it is
            // still there, so its clock MUST reset on traffic. A deadline here would drop every
            // healthy session on a fixed timer.
            // The 400 ms deadline this used to set was live between ConnectAsync and
            // MarkAuthenticated below — a runner that stalled in that gap reaped the connection
            // before the test had authenticated it, and the failure looked like the idle clock
            // was broken. A held clock does not tick during that gap at all.
            var clock = new HeldClock();
            await using var harness = new MasterHostHarness(o =>
            {
                o.Clock                  = clock;
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);
                o.HeartbeatTimeout       = TimeSpan.FromSeconds(45);
            });

            TcpClient client = await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            await harness.Host.InvokeOnLogicThreadAsync(() =>
            {
                foreach (ClientConnection connection in harness.Host.ConnectionsUnsafe)
                    connection.MarkAuthenticated();
                return true;
            });

            // Six beats 30 s apart — 180 s in total, four times the 45 s window and six times
            // the 30 s deadline that would apply if this connection were still unauthenticated.
            // Each beat is counted before the clock moves, so the gap the server measures is
            // exactly the 30 s stepped here.
            NetworkStream stream = client.GetStream();
            for (int i = 0; i < 6; i++)
            {
                await stream.WriteAsync(Frame(Heartbeat, "{}"));
                await stream.FlushAsync();

                int expected = i + 1;
                Assert.True(
                    await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalHeartbeats >= expected),
                    $"heartbeat {expected} of 6 was never parsed");

                clock.Advance(TimeSpan.FromSeconds(30));
            }

            Assert.Equal(1, harness.Host.ConnectionCount);
            Assert.Equal(0, harness.Host.TotalTimedOut);
        }

        [Fact]
        public async Task HeartbeatsDoNotExtendAnUnauthenticatedDeadline()
        {
            // The realistic form of the attack, and the one raw dribbled bytes do not cover: a
            // client speaking the protocol perfectly, sending well-formed HEARTBEAT frames
            // forever, and never authenticating. Every frame is valid, so nothing looks wrong;
            // if the deadline reset on traffic this connection would hold its slot for as long
            // as the attacker cared to keep beating.
            // Held clock, for the same reason as the dribble test: this asserts that a
            // connection IS reaped, so on a real clock a stalled runner would reap it and the
            // test would go green without the deadline having done anything. Stepping the clock
            // ourselves makes the reap attributable.
            var clock = new HeldClock();
            await using var harness = new MasterHostHarness(o =>
            {
                o.Clock                  = clock;
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);
            });

            TcpClient client = await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            NetworkStream stream = client.GetStream();
            byte[] heartbeat = Frame(Heartbeat, "{}");

            // Five well-formed beats 5 s apart — 25 s, inside the 30 s deadline, each one
            // confirmed PARSED before the clock moves. These are the exact frames that keep an
            // authenticated connection alive; the whole question is whether they do anything for
            // a connection that never logged in.
            for (int i = 0; i < 5; i++)
            {
                await stream.WriteAsync(heartbeat);
                await stream.FlushAsync();

                clock.Advance(TimeSpan.FromSeconds(5));

                int expected = i + 1;
                Assert.True(
                    await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalHeartbeats >= expected),
                    $"heartbeat {expected} of 5 was never parsed");
            }

            Assert.Equal(1, harness.Host.ConnectionCount);

            // Cross the deadline. The idle gap is at most 5 s; the deadline is 30 s. A reap here
            // is the deadline and can be nothing else.
            clock.Advance(TimeSpan.FromSeconds(6));

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0),
                "a client that never authenticated held its slot by heartbeating — the "
                + "unauthenticated timeout is an idle gap, not a deadline");
        }

        /// <summary>
        /// A bare 4-byte MSP length prefix, big-endian, with no body behind it — the opening of
        /// a frame that the caller intends never to finish.
        /// </summary>
        private static byte[] LengthPrefix(int declaredLength)
        {
            var prefix = new byte[MspFrame.LengthPrefixSize];
            Endian.WriteU32BE(prefix, 0, (uint)declaredLength);
            return prefix;
        }

        /// <summary>
        /// The single live connection's <c>LastActivityMs</c>, read on the logic thread — the
        /// only place a connection may be touched.
        /// </summary>
        /// <remarks>
        /// Lets a test wait for "the server has actually ingested that byte" rather than assume
        /// it. Raw bytes complete no frame, so no counter on the host moves for them and this is
        /// the only observable that does.
        /// </remarks>
        private static Task<long> LastActivityMsAsync(MasterHostHarness harness)
            => harness.Host.InvokeOnLogicThreadAsync(() =>
            {
                long last = long.MinValue;
                foreach (ClientConnection connection in harness.Host.ConnectionsUnsafe)
                    last = connection.LastActivityMs;
                return last;
            });

        private static byte[] Frame(MspMessageType msgType, string json)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            var buffer = new byte[MspFrame.FrameSizeFor(body.Length)];
            int written = MspFrame.Write(buffer, msgType, body);
            Assert.True(written > 0);
            return buffer;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (byte[] part in parts) total += part.Length;

            var result = new byte[total];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                part.CopyTo(result, offset);
                offset += part.Length;
            }

            return result;
        }
    }
}
