using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    /// <summary>
    /// X-32 — the reliable channel abandons peers under <c>--sim typical</c>, and this is why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// protocol-spec.md § 2.2 argues that an ack cannot realistically be lost, because "every
    /// packet carries 33 pieces of ack information (1 + 32), so an ack is only lost if 33
    /// consecutive packets are lost — in practice, never". That argument is sound, and it
    /// silently assumes every transmission carries a <b>fresh</b> sequence, because the ack
    /// bitfield is addressed by <i>distance behind the receiver's newest sequence</i> and can
    /// express exactly 32 of them.
    /// </para>
    /// <para>
    /// A retransmission that reuses its original sequence breaks that assumption. It does not
    /// lose a race against 33 consecutive drops — it falls out of the addressable window and
    /// becomes <b>unacknowledgeable for the rest of the connection</b>, however many copies
    /// arrive. The sender then burns its whole <see cref="ReliabilityLayer.AbandonAfterMs"/>
    /// budget on a packet the peer already has, sets
    /// <see cref="ReliabilityLayer.HasAbandonedReliable"/>, and <c>Connection.Update</c> ends
    /// the connection with <c>TransportError</c> — the one call site all four (and later all
    /// eight) lost clients came through.
    /// </para>
    /// <para>
    /// <b>Why it only shows under loss.</b> On a clean wire nothing is ever retransmitted, so
    /// every reliable packet is acked at distance ~1 and the window is never approached. That
    /// is exactly the shape of the measurement: <c>run-02-clean</c> held 8 of 8 with the same
    /// client count, duration, behaviour and seed that lost four under <c>--sim typical</c>.
    /// </para>
    /// </remarks>
    public sealed class ReliableResendAckWindowTests
    {
        /// <summary>
        /// Outbound packets per second a loaded client actually produces: 30 Hz input plus a
        /// reliable baseline ack per applied snapshot at ~20 Hz. It is the reason the 32-wide
        /// window is only worth ~0.64 s of traffic.
        /// </summary>
        private const int PacketsPerSecond = 50;

        /// <summary>
        /// What <c>Connection.Resend</c> does to a retransmission before it goes out: stamp
        /// the fresh sequence the reliability layer assigned into the header.
        /// </summary>
        private static void PacketBuilder_TryRestamp(byte[] datagram, ushort sequence)
            => Endian.WriteU16LE(datagram, 4, sequence);

        private static byte[] Datagram(ushort sequence, ushort messageId = 0)
        {
            var datagram = new byte[GspHeader.Size + 8];
            Endian.WriteU16LE(datagram, 4, sequence);
            // The message rides in the payload, where a re-stamp cannot touch it. After the
            // fix the GSP sequence and the message are no longer the same number, so a test
            // that wants to impair ONE message has to name it here.
            Endian.WriteU16LE(datagram, GspHeader.Size, messageId);
            return datagram;
        }

        private static ushort MessageOf(byte[] datagram)
            => Endian.ReadU16LE(datagram, GspHeader.Size);

        // ------------------------------------------------------------------ the mechanism

        [Fact]
        public void AResendReusingItsOriginalSequenceWouldBeUnacknowledgeable()
        {
            // The window fact the fix exists because of, pinned so a protocol change cannot
            // quietly remove the reason. This is not a defect in ReliabilityLayer: given a
            // sequence that far behind and a 32-entry history addressed by distance, staying
            // silent is the only thing the receiver CAN do. The defect was asking it to.
            var sender = new ReliabilityLayer();
            var receiver = new ReliabilityLayer();

            ushort lost = sender.NextSequence();
            sender.OnPacketSent(lost, Datagram(lost), reliable: true, nowMs: 0);
            // ...and the receiver never sees it. No OnPacketReceived for `lost`.

            // 0.64 s of ordinary traffic at 50 packets/s is all it takes to walk past the window.
            for (int i = 1; i <= ProtocolConstants.ACK_BITFIELD_BITS + 1; i++)
            {
                ushort seq = sender.NextSequence();
                sender.OnPacketSent(seq, Datagram(seq), reliable: false, nowMs: i);
                receiver.OnPacketReceived(seq);
            }

            // What Connection.Resend used to do: ship the stored datagram verbatim, original
            // sequence and all. The copy ARRIVES here — it is not dropped.
            receiver.OnPacketReceived(lost);

            (ushort ack, uint bitfield) = receiver.BuildAck();
            sender.ProcessIncomingAck(ack, bitfield, nowMs: 700);

            Assert.Equal(1, sender.PendingReliableCount);
            Assert.False(
                GspHeader.IsAcked(lost, ack, bitfield),
                "if a sequence this far behind ever becomes expressible, re-stamping "
                + "retransmissions stops being necessary and this test should be deleted "
                + "rather than re-pinned");
        }

        [Fact]
        public void ARetransmissionGoesOutUnderAFreshSequenceAndIsAcknowledged()
        {
            // The fix, driven through the real retransmission path. Same setup as above — one
            // reliable packet lost, then more than a bitfield's worth of traffic — except the
            // resend comes out of ReliabilityLayer.Update, which assigns it a new sequence and
            // hands that to the caller to stamp into the header.
            var sender = new ReliabilityLayer();
            var receiver = new ReliabilityLayer();

            ushort lost = sender.NextSequence();
            sender.OnPacketSent(lost, Datagram(lost), reliable: true, nowMs: 0);

            for (int i = 1; i <= ProtocolConstants.ACK_BITFIELD_BITS + 1; i++)
            {
                ushort seq = sender.NextSequence();
                sender.OnPacketSent(seq, Datagram(seq), reliable: false, nowMs: i);
                receiver.OnPacketReceived(seq);
            }

            ushort resentAs = 0;
            sender.Update(nowMs: 700, (datagram, _, sequence) =>
            {
                PacketBuilder_TryRestamp(datagram, sequence);
                resentAs = sequence;
                receiver.OnPacketReceived(sequence);
            });

            Assert.NotEqual(lost, resentAs);

            (ushort ack, uint bitfield) = receiver.BuildAck();
            sender.ProcessIncomingAck(ack, bitfield, nowMs: 700);

            Assert.Equal(0, sender.PendingReliableCount);
        }

        [Fact]
        public void TheSameRetransmissionIsAcknowledgedWhenItArrivesInsideTheWindow()
        {
            // The control for the test above, and the reason it is a window problem rather
            // than a "duplicates are ignored" problem: identical setup, one fewer packet of
            // intervening traffic, and the ack lands.
            var sender = new ReliabilityLayer();
            var receiver = new ReliabilityLayer();

            ushort lost = sender.NextSequence();
            sender.OnPacketSent(lost, Datagram(lost), reliable: true, nowMs: 0);

            for (int i = 1; i <= ProtocolConstants.ACK_BITFIELD_BITS - 1; i++)
            {
                ushort seq = sender.NextSequence();
                sender.OnPacketSent(seq, Datagram(seq), reliable: false, nowMs: i);
                receiver.OnPacketReceived(seq);
            }

            receiver.OnPacketReceived(lost);

            (ushort ack, uint bitfield) = receiver.BuildAck();
            sender.ProcessIncomingAck(ack, bitfield, nowMs: 700);

            Assert.Equal(0, sender.PendingReliableCount);
        }

        // ------------------------------------------------------------------ the consequence

        [Fact]
        public void ThreeDroppedCopiesOutOfThirtyTwoAttemptsDoNotKillTheConnection()
        {
            // The step from "cannot be acked" to "the client is gone", with the loss written
            // down rather than rolled for. ONE reliable message's original and its first two
            // retransmissions are dropped; every copy after that is DELIVERED, so the peer has
            // held it from about a second in. Before the fix the sender still spent its whole
            // AbandonAfterMs budget and latched HasAbandonedReliable — the single guard on
            // Connection.cs's only Fail(DisconnectReason.TransportError) call site.
            //
            // Three drops is not a pathological wire. At 5 % each way a send-plus-ack round
            // trip fails about 9.75 % of the time, so three in a row is roughly 1 in 1,100 —
            // and a harness client sends ~2,400 reliable baseline acks in 120 s.
            //
            // Two details are load-bearing rather than decorative, and both were got wrong
            // first. (1) The 25 Hz reliable stream underneath: RTT is only sampled from
            // reliable packets acked on their first transmission, so a test whose only
            // reliable packet is the dropped one leaves SmoothedRtt at 0, pins the RTO to its
            // 30 ms floor, and retransmits fast enough to stay inside the ack window by
            // accident — green with the fix reverted, which is evidence about nothing. (2) The
            // drop budget is keyed on the MESSAGE, not on "the next resend to come past": a
            // lossless link still produces the occasional RTO race on other packets, and those
            // silently ate the budget until the impaired message was never impaired at all.
            var forward = new Link(seed: 1, lossPercent: 0.0, latencyMs: 50.0, jitterMs: 0.0);
            var back = new Link(seed: 2, lossPercent: 0.0, latencyMs: 50.0, jitterMs: 0.0);
            var sender = new ReliabilityLayer();
            var receiver = new ReliabilityLayer();

            const double stepMs = 1000.0 / PacketsPerSecond;
            const int reliableEvery = PacketsPerSecond / 25;
            const ushort impaired = 1;
            int copiesToDrop = 2;   // ...on top of the original, so three copies in all
            int step = 0;
            ushort nextMessage = impaired;

            for (double nowMs = 0;
                 nowMs <= ReliabilityLayer.AbandonAfterMs + 2000;
                 nowMs += stepMs, step++)
            {
                forward.Deliver(nowMs, seq => receiver.OnPacketReceived((ushort)seq));
                back.Deliver(nowMs, packed => sender.ProcessIncomingAck(
                    (ushort)(packed & 0xFFFF), (uint)(packed >> 16), nowMs));

                bool reliable = step % reliableEvery == 0;
                ushort sequence = sender.NextSequence();
                ushort message = reliable ? nextMessage++ : (ushort)0;
                sender.OnPacketSent(sequence, Datagram(sequence, message), reliable, nowMs);
                if (message != impaired) forward.Send(nowMs, sequence);

                sender.Update(nowMs, (datagram, _, resentAs) =>
                {
                    PacketBuilder_TryRestamp(datagram, resentAs);
                    if (MessageOf(datagram) == impaired && copiesToDrop-- > 0) return;
                    forward.Send(nowMs, resentAs);
                });

                (ushort ack, uint bitfield) = receiver.BuildAck();
                back.Send(nowMs, ack | ((long)bitfield << 16));

                if (sender.HasAbandonedReliable) break;
            }

            Assert.True(copiesToDrop <= 0, "the impaired message was never impaired");
            Assert.False(
                sender.HasAbandonedReliable,
                "the reliable channel gave up on a message the peer had held for most of the "
                + "run, after losing three copies out of an allowance of thirty-two — the "
                + "copies that arrived landed further behind the receiver's ack cursor than "
                + "the 32-entry bitfield can address, so no arrival could ever be reported. "
                + "Connection.Update turns this into a TransportError disconnect: X-32");
        }

        [Fact]
        public void APeerOnATypicalWireSurvivesTwoMinutesOfReliableTraffic()
        {
            // The 120 s / --sim typical run, at unit-test speed and with the wall clock
            // replaced by a counter: 50 packets/s outbound of which the ~20 Hz baseline ack is
            // reliable, 50 ms +/- 20 ms one-way latency and 5 % loss in each direction, seeded
            // so any failure replays exactly.
            //
            // The latency is not decoration. The RTO is 1.5 x smoothed RTT + 4 x jitter, so on
            // a 100 ms round trip a retransmission goes out ~200 ms after the original and the
            // one after it ~600 ms after that — which is how a 32-packet window worth ~0.64 s
            // of traffic gets walked past. A zero-latency model has an RTO pinned at the 30 ms
            // floor and does not reproduce the defect; that is a property of the model, not
            // evidence about the transport.
            var forward = new Link(seed: 12345, lossPercent: 5.0, latencyMs: 50.0, jitterMs: 20.0);
            var back = new Link(seed: 12345 + 7919, lossPercent: 5.0, latencyMs: 50.0, jitterMs: 20.0);
            var sender = new ReliabilityLayer();
            var receiver = new ReliabilityLayer();
            // The transport names the packet it gave up on, and that line is the whole
            // diagnosis when this goes red. Restored afterwards because NetLog's sink is
            // process-global and xUnit runs collections in parallel.
            var warnings = new System.Collections.Generic.List<string>();
            Action<string>? previousSink = NetLog.Warning;
            NetLog.Warning = warnings.Add;
            try
            {
                const double stepMs = 1000.0 / PacketsPerSecond;
                const double durationMs = 120_000.0;
                const int replyEvery = PacketsPerSecond / 20;

                int reliableSent = 0;
                int step = 0;

                for (double nowMs = 0; nowMs < durationMs; nowMs += stepMs, step++)
                {
                    forward.Deliver(nowMs, seq => receiver.OnPacketReceived((ushort)seq));
                    back.Deliver(nowMs, packed => sender.ProcessIncomingAck(
                        (ushort)(packed & 0xFFFF), (uint)(packed >> 16), nowMs));

                    // Two of every five outbound packets are the reliable baseline ack; the rest
                    // are input.
                    bool reliable = (step % 5) < 2;
                    // Through the layer's own counter, exactly as Connection.SendPacket does — a
                    // retransmission draws from the same space, so a model with a private counter
                    // would collide with its own resends and prove nothing.
                    ushort sequence = sender.NextSequence();
                    sender.OnPacketSent(sequence, Datagram(sequence), reliable, nowMs);
                    if (reliable) reliableSent++;
                    forward.Send(nowMs, sequence);

                    sender.Update(nowMs, (datagram, _, sequence) =>
                    {
                        PacketBuilder_TryRestamp(datagram, sequence);
                        forward.Send(nowMs, sequence);
                    });

                    if (step % replyEvery == 0)
                    {
                        (ushort ack, uint bitfield) = receiver.BuildAck();
                        back.Send(nowMs, ack | ((long)bitfield << 16));
                    }

                    Assert.False(
                        sender.HasAbandonedReliable,
                        $"the reliable channel gave up at {nowMs:F0} ms after {reliableSent} "
                        + "reliable packets; Connection.Update turns this into a TransportError "
                        + $"disconnect, which is X-32. warnings: {string.Join(" | ", warnings)}");
                }

                Assert.True(
                    reliableSent > 2000, "the model did not actually exercise the channel");
            }
            finally
            {
                NetLog.Warning = previousSink;
            }
        }

        /// <summary>
        /// One direction of a seeded, impaired wire: Bernoulli loss plus latency and uniform
        /// jitter, carrying a single <see cref="long"/> payload so the same class serves both
        /// packet sequences and packed (ack, bitfield) pairs.
        /// </summary>
        private sealed class Link
        {
            private readonly System.Collections.Generic.List<(double AtMs, long Payload)> _inFlight
                = new System.Collections.Generic.List<(double, long)>();
            private readonly Random _rng;
            private readonly double _lossPercent;
            private readonly double _latencyMs;
            private readonly double _jitterMs;

            public Link(int seed, double lossPercent, double latencyMs, double jitterMs)
            {
                _rng = new Random(seed);
                _lossPercent = lossPercent;
                _latencyMs = latencyMs;
                _jitterMs = jitterMs;
            }

            public void Send(double nowMs, long payload)
            {
                if (_rng.NextDouble() * 100.0 < _lossPercent) return;
                double delay = _latencyMs + (_rng.NextDouble() * 2.0 - 1.0) * _jitterMs;
                _inFlight.Add((nowMs + Math.Max(0.0, delay), payload));
            }

            public void Deliver(double nowMs, Action<long> deliver)
            {
                for (int i = _inFlight.Count - 1; i >= 0; i--)
                {
                    if (_inFlight[i].AtMs > nowMs) continue;
                    long payload = _inFlight[i].Payload;
                    _inFlight.RemoveAt(i);
                    deliver(payload);
                }
            }
        }
    }
}
