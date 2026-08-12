using System;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Two-mode congestion signal with hysteresis. It recommends a send rate; it does not
    /// decide what game data may be discarded.
    /// </summary>
    public sealed class CongestionControl
    {
        public enum Mode
        {
            Good,
            Bad,
        }

        private const float RttToBadMs = 250f;
        private const float RttToGoodMs = 200f;
        private const float MinBadDurationSeconds = 10f;
        private const float GoodStreakForNormalPenalty = 10f;

        private float _badTimer;
        private float _goodStreak;

        public Mode CurrentMode { get; private set; } = Mode.Good;

        public int RecommendedSendRateHz => CurrentMode == Mode.Good ? 20 : 10;

        public bool ShouldReduceDetail => CurrentMode == Mode.Bad;

        public float BadTimeRemainingSeconds => _badTimer;

        public void Update(float deltaSeconds, float smoothedRttMs)
        {
            if (deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

            if (CurrentMode == Mode.Good)
            {
                if (smoothedRttMs > RttToBadMs)
                {
                    CurrentMode = Mode.Bad;
                    _badTimer = _goodStreak < GoodStreakForNormalPenalty
                        ? MinBadDurationSeconds * 2f
                        : MinBadDurationSeconds;
                    _goodStreak = 0f;
                    NetLog.Warn($"congestion -> BAD (rtt {smoothedRttMs:F0}ms)");
                }
                else
                {
                    _goodStreak += deltaSeconds;
                }
            }
            else
            {
                _badTimer -= deltaSeconds;
                if (_badTimer <= 0f && smoothedRttMs < RttToGoodMs)
                {
                    CurrentMode = Mode.Good;
                    _badTimer = 0f;
                    NetLog.Warn("congestion -> GOOD");
                }
            }
        }
    }
}
