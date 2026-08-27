using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class ReliabilityLayerTests
    {
        [Fact]
        public void FirstReceiveCreatesAnAckWithAnEmptyHistory()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(7);

            (ushort ack, uint bits) = reliability.BuildAck();
            Assert.Equal((ushort)7, ack);
            Assert.Equal(0u, bits);
        }

        [Fact]
        public void InOrderReceivesSetThePreviousSequenceBits()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(1);
            reliability.OnPacketReceived(2);
            reliability.OnPacketReceived(3);

            Assert.Equal(((ushort)3, 0b11u), reliability.BuildAck());
        }

        [Fact]
        public void AJumpBeyondTheBitfieldResetsHistory()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(1);
            reliability.OnPacketReceived(2);
            reliability.OnPacketReceived(3);
            reliability.OnPacketReceived(200);

            Assert.Equal(((ushort)200, 0u), reliability.BuildAck());
        }

        [Fact]
        public void AnExactThirtyTwoPacketJumpKeepsTheOldestHistoryBit()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(100);
            reliability.OnPacketReceived(132);

            (ushort ack, uint bitfield) = reliability.BuildAck();

            Assert.Equal((ushort)132, ack);
            Assert.Equal(1u << 31, bitfield);
        }

        [Fact]
        public void AckBitfieldCanBeDisabledForTheComparisonExperiment()
        {
            var reliability = new ReliabilityLayer { AckBitfieldEnabled = false };
            reliability.OnPacketReceived(10);
            reliability.OnPacketReceived(11);

            Assert.Equal(((ushort)11, 0u), reliability.BuildAck());
        }

        [Fact]
        public void AReorderedPacketSetsItsSpecificHistoryBit()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(10);
            reliability.OnPacketReceived(12);
            reliability.OnPacketReceived(11);

            Assert.Equal(((ushort)12, 0b11u), reliability.BuildAck());
        }

        [Fact]
        public void DuplicateReceiveDoesNotShiftTheHistory()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(10);
            reliability.OnPacketReceived(11);
            reliability.OnPacketReceived(11);

            Assert.Equal(((ushort)11, 0b1u), reliability.BuildAck());
        }

        [Fact]
        public void ReceiveSequenceWrapsCorrectly()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketReceived(ushort.MaxValue);
            reliability.OnPacketReceived(0);
            reliability.OnPacketReceived(1);

            Assert.Equal(((ushort)1, 0b11u), reliability.BuildAck());
        }

        [Fact]
        public void AckBitfieldAcknowledgesSeveralReliablePackets()
        {
            var reliability = new ReliabilityLayer();
            for (ushort sequence = 0; sequence < 3; sequence++)
                reliability.OnPacketSent(sequence, new byte[] { 1, 2 }, true, 0);

            reliability.ProcessIncomingAck(2, 0b11u, 20);

            Assert.Equal(0, reliability.PendingReliableCount);
        }

        [Fact]
        public void AnOverwrittenHistorySlotCannotBeAcknowledgedAsTheNewPacket()
        {
            var reliability = new ReliabilityLayer(new BufferPool(1025, ProtocolConstants.MTU_SAFE));
            for (int i = 0; i <= 1024; i++)
                reliability.OnPacketSent((ushort)i, new byte[] { (byte)i }, true, i);

            reliability.ProcessIncomingAck(0, 0, 2000);

            Assert.Equal(1024, reliability.PendingReliableCount);
            Assert.True(reliability.HasAbandonedReliable);
        }

        [Fact]
        public void ReliablePacketIsResentAfterItsRto()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 0xAB }, true, 0);
            var resend = new List<byte>();

            reliability.Update(31, (data, length, _) => resend.Add(data[0]));

            Assert.Equal(new byte[] { 0xAB }, resend);
        }

        [Fact]
        public void PacketLossCountsOneLossPerReliableTimeout()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 0xAB }, true, 0);

            reliability.Update(31, (_, _, _) => { });
            reliability.Update(62, (_, _, _) => { });

            Assert.Equal(1, reliability.PacketsLost);
        }

        [Fact]
        public void RetryMetricsCountDistinctPacketsAndEveryResendSeparately()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 0xAB }, true, 0);

            // 31 ms clears the first interval (RTO floor 30); the second is 60 ms after that,
            // not another 31 — see RetransmissionIntervalsBackOffExponentially...
            reliability.Update(31, (_, _, _) => { });
            reliability.Update(92, (_, _, _) => { });

            Assert.Equal(1, reliability.ReliablePacketsSent);
            Assert.Equal(1, reliability.ReliablePacketsRetried);
            Assert.Equal(2, reliability.ReliablePacketsResent);
        }

        [Fact]
        public void AckStopsFurtherRetransmission()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 0);
            reliability.ProcessIncomingAck(0, 0, 10);
            int resends = 0;

            reliability.Update(1000, (_, _, _) => resends++);

            Assert.Equal(0, resends);
            Assert.Equal(0, reliability.PendingReliableCount);
        }

        [Fact]
        public void ARetransmittedPacketIsGivenUpOnTimeNotOnAttemptCountAndReleasesItsBuffer()
        {
            // Replaces TenRetransmissionsThenGiveUpAndReleaseTheBuffer, which asserted 10
            // resends at a fixed 31 ms interval. That test was green for the whole life of the
            // lane-B blocker BECAUSE it encoded the blocker as the specification: ten attempts
            // at the RTO floor is a 300 ms budget, and it fired on clients that were merely
            // busy. The behaviour worth pinning is the BUDGET, and it is a duration.
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 0);

            // Nothing is abandoned inside the old budget, or anywhere near it.
            for (double nowMs = 1; nowMs <= 2_000; nowMs += 5)
                reliability.Update(nowMs, (_, _, _) => { });

            Assert.False(
                reliability.HasAbandonedReliable,
                "a peer that has been quiet for 2 s is busy, not gone — the connection's own "
                + "liveness rule is TIMEOUT_MS and it has not fired");
            Assert.Equal(1, reliability.PendingReliableCount);

            // And it IS abandoned once the budget genuinely expires, so this is a deadline
            // that moved, not one that was deleted.
            for (double nowMs = 2_000; nowMs <= ReliabilityLayer.AbandonAfterMs + 2_000; nowMs += 5)
                reliability.Update(nowMs, (_, _, _) => { });

            Assert.True(
                reliability.HasAbandonedReliable,
                "the layer must still give up on a peer that never answers, or a dead "
                + "connection is held open forever");
            Assert.Equal(0, reliability.PendingReliableCount);
        }

        [Fact]
        public void RetransmissionIntervalsBackOffExponentiallyInsteadOfFloodingAtTheRtoFloor()
        {
            // The other half of the same fix. Ten copies of one packet inside 300 ms is not
            // only the give-up bug — it floods a peer at exactly the moment it has least
            // capacity to answer. Asserted on the schedule rather than a count, because the
            // count is what was wrong.
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 0);

            var sendTimes = new List<double>();
            for (double nowMs = 1; nowMs <= 1_000; nowMs += 1)
                reliability.Update(nowMs, (_, _, _) => sendTimes.Add(nowMs));

            // 30, then 60, 120, 240, 480 later: five resends in the first second, where the
            // fixed-interval schedule issued thirty-three.
            Assert.Equal(5, sendTimes.Count);
            for (int i = 1; i < sendTimes.Count; i++)
            {
                double gap = sendTimes[i] - sendTimes[i - 1];
                double previousGap = i == 1 ? 30.0 : sendTimes[i - 1] - sendTimes[i - 2];
                Assert.True(
                    gap >= previousGap * 1.9,
                    $"gap {i} was {gap:F0} ms after {previousGap:F0} ms — backoff must double");
            }
        }

        [Fact]
        public void KarnsAlgorithmDoesNotSampleAResentPacket()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 0);
            reliability.Update(31, (_, _, _) => { });
            reliability.ProcessIncomingAck(0, 0, 32);

            Assert.Equal(0f, reliability.SmoothedRttMs);
        }

        [Fact]
        public void AcknowledgedFirstTransmissionProducesAnRttSample()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 10);
            reliability.ProcessIncomingAck(0, 0, 55);

            Assert.Equal(45f, reliability.SmoothedRttMs);
            Assert.True(reliability.RetransmissionTimeoutMs >= 30f);
        }

        [Fact]
        public void ReliableWindowStopsAtSixtyFourUnackedPackets()
        {
            var reliability = new ReliabilityLayer(new BufferPool(64, ProtocolConstants.MTU_SAFE));
            for (ushort sequence = 0; sequence < 64; sequence++)
                reliability.OnPacketSent(sequence, new byte[] { 1 }, true, 0);

            Assert.False(reliability.CanSendReliable);
        }

        [Fact]
        public void OneThousandReliablePacketsArriveThroughThirtyPercentLoss()
        {
            const int packetCount = 1_000;
            var dataPool = new BufferPool(4_096, ProtocolConstants.MTU_SAFE);
            var ackPool = new BufferPool(4_096, ProtocolConstants.MTU_SAFE);
            var config = new SimulatorConfig
            {
                Enabled = true,
                LatencyMs = 10f,
                PacketLossPercent = 30f,
                RandomSeed = 12345,
            };
            var dataWire = new NetworkSimulator<int>(config, dataPool);
            var ackWire = new NetworkSimulator<int>(config.Clone(), ackPool);
            var sender = new ReliabilityLayer(dataPool);
            var receiver = new ReliabilityLayer(ackPool);
            var delivered = new bool[packetCount];
            int deliveredCount = 0;
            double lastPeriodicAckMs = 0;

            // Fed through the SAME in-flight gate production uses: CanSendReliable stops the
            // sender at 64 unacked, which is what Connection does.
            //
            // The wire payload carries the MESSAGE id and the GSP SEQUENCE separately, because
            // after X-32 they are no longer the same number: a retransmission is re-stamped
            // with a fresh sequence (Connection.Resend) while the message it carries is
            // unchanged. Conflating them is what let the old model hide the defect — a resend
            // "arriving" was scored against the message id, and the receiver's inability to
            // acknowledge the SEQUENCE never showed up.
            ushort nextMessage = 0;
            void FeedSendWindow(double nowMs)
            {
                while (nextMessage < packetCount && sender.CanSendReliable)
                {
                    ushort sequence = sender.NextSequence();
                    byte[] packet = new byte[4];
                    Endian.WriteU16LE(packet, 0, nextMessage);
                    Endian.WriteU16LE(packet, 2, sequence);
                    sender.OnPacketSent(sequence, packet, reliable: true, nowMs);
                    dataWire.ShouldSend(packet, 0, nowMs);
                    nextMessage++;
                }
            }

            FeedSendWindow(0);

            for (double nowMs = 0;
                 nowMs <= 60_000
                     && (deliveredCount < packetCount
                         || nextMessage < packetCount
                         || sender.PendingReliableCount > 0);
                 nowMs += 5)
            {
                dataWire.Flush(nowMs, (data, length, _) =>
                {
                    ushort message = Endian.ReadU16LE(data.AsSpan(0, length), 0);
                    ushort sequence = Endian.ReadU16LE(data.AsSpan(0, length), 2);
                    if (!delivered[message])
                    {
                        delivered[message] = true;
                        deliveredCount++;
                    }
                    receiver.OnPacketReceived(sequence);
                    (ushort ack, uint bits) = receiver.BuildAck();
                    byte[] ackPacket = new byte[6];
                    Endian.WriteU16LE(ackPacket, 0, ack);
                    Endian.WriteU32LE(ackPacket, 2, bits);
                    ackWire.ShouldSend(ackPacket, 0, nowMs);
                });

                ackWire.Flush(nowMs, (ackPacket, _, _) =>
                {
                    ushort ack = Endian.ReadU16LE(ackPacket, 0);
                    uint bits = Endian.ReadU32LE(ackPacket, 2);
                    sender.ProcessIncomingAck(ack, bits, nowMs);
                });

                // The receiver's periodic ack, at ProtocolConstants.KEEPALIVE_MS. Production has
                // this and the model above did not: Connection emits a keep-alive on the idle
                // timer carrying the freshly built ack window, so the sender learns of delivery
                // even when no further data arrives to piggyback on. Without it this test made
                // the sender's knowledge depend entirely on how hard it was retransmitting,
                // which is backwards — retransmission is what the acks are supposed to STOP.
                if (nowMs - lastPeriodicAckMs >= ProtocolConstants.KEEPALIVE_MS)
                {
                    lastPeriodicAckMs = nowMs;
                    (ushort ack, uint bits) = receiver.BuildAck();
                    byte[] periodic = new byte[6];
                    Endian.WriteU16LE(periodic, 0, ack);
                    Endian.WriteU32LE(periodic, 2, bits);
                    ackWire.ShouldSend(periodic, 0, nowMs);
                }

                sender.Update(nowMs, (data, length, sequence) =>
                {
                    // Connection.Resend's re-stamp, modelled: the retransmission goes out
                    // under the fresh sequence, carrying the same message.
                    Endian.WriteU16LE(data, 2, sequence);
                    dataWire.ShouldSend(data.AsSpan(0, length), 0, nowMs);
                });

                FeedSendWindow(nowMs);
            }

            Assert.Equal(packetCount, deliveredCount);
            Assert.Equal(0, sender.PendingReliableCount);

            dataWire.Clear();
            ackWire.Clear();
            sender.Clear();

            Assert.Equal(0, dataPool.RentedCount);
            Assert.Equal(0, ackPool.RentedCount);
        }
    }
}
