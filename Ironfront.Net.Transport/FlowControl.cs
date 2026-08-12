using System;

namespace Ironfront.Net.Transport
{
    /// <summary>Receiver headroom advertised in a keep-alive/control payload.</summary>
    public readonly struct FlowControlInfo
    {
        public FlowControlInfo(ushort pendingReliableCount, byte bufferPressurePercent)
        {
            PendingReliableCount = pendingReliableCount;
            BufferPressurePercent = bufferPressurePercent > 100 ? (byte)100 : bufferPressurePercent;
        }

        public ushort PendingReliableCount { get; }

        public byte BufferPressurePercent { get; }
    }

    /// <summary>Small sliding-window guard for new reliable packets.</summary>
    public sealed class FlowControl
    {
        public const int MaxUnackedReliable = 64;

        private bool _pauseNewReliable;

        public bool CanSendReliable(int localUnacked)
            => !_pauseNewReliable && localUnacked < MaxUnackedReliable;

        public void ApplyRemote(FlowControlInfo info)
        {
            _pauseNewReliable = info.BufferPressurePercent > 80
                || info.PendingReliableCount >= MaxUnackedReliable;
        }

        public void Reset() => _pauseNewReliable = false;
    }
}
