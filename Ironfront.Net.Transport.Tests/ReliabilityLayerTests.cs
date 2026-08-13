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

            reliability.Update(31, (data, length) => resend.Add(data[0]));

            Assert.Equal(new byte[] { 0xAB }, resend);
        }

        [Fact]
        public void PacketLossCountsOneLossPerReliableTimeout()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 0xAB }, true, 0);

            reliability.Update(31, (_, _) => { });
            reliability.Update(62, (_, _) => { });

            Assert.Equal(1, reliability.PacketsLost);
        }

        [Fact]
        public void RetryMetricsCountDistinctPacketsAndEveryResendSeparately()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 0xAB }, true, 0);

            reliability.Update(31, (_, _) => { });
            reliability.Update(62, (_, _) => { });

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

            reliability.Update(1000, (_, _) => resends++);

            Assert.Equal(0, resends);
            Assert.Equal(0, reliability.PendingReliableCount);
        }

        [Fact]
        public void TenRetransmissionsThenGiveUpAndReleaseTheBuffer()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 0);
            int resends = 0;
            for (int i = 1; i <= 11; i++)
                reliability.Update(i * 31, (_, _) => resends++);

            Assert.Equal(10, resends);
            Assert.Equal(0, reliability.PendingReliableCount);
        }

        [Fact]
        public void KarnsAlgorithmDoesNotSampleAResentPacket()
        {
            var reliability = new ReliabilityLayer();
            reliability.OnPacketSent(0, new byte[] { 1 }, true, 0);
            reliability.Update(31, (_, _) => { });
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

            for (ushort sequence = 0; sequence < packetCount; sequence++)
            {
                byte[] packet = new byte[2];
                Endian.WriteU16LE(packet, 0, sequence);
                sender.OnPacketSent(sequence, packet, reliable: true, nowMs: 0);
                dataWire.ShouldSend(packet, 0, 0);
            }

            for (double nowMs = 0;
                 nowMs <= 10_000 && (deliveredCount < packetCount || sender.PendingReliableCount > 0);
                 nowMs += 5)
            {
                dataWire.Flush(nowMs, (data, length, _) =>
                {
                    ushort sequence = Endian.ReadU16LE(data.AsSpan(0, length), 0);
                    if (!delivered[sequence])
                    {
                        delivered[sequence] = true;
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

                sender.Update(nowMs, (data, length) =>
                    dataWire.ShouldSend(data.AsSpan(0, length), 0, nowMs));
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
