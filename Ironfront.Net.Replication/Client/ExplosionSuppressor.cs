using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Lets this client draw its own explosion the instant it happens and then swallow the
    /// server's confirmation of it, so the player does not watch their own grenade go off one
    /// round-trip late. phase-V10 task 10, decision D13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A prediction expires; it does not wait for a confirmation.</b> A grenade shot out of
    /// the air never produces an <c>S_EXPLOSION</c> at all, so an entry held until confirmed
    /// would sit there forever and swallow the <i>next</i> real blast from the same actor —
    /// turning a cosmetic latency win into a missing explosion, which is far worse than the
    /// thing it was buying. The window bounds the damage to D13's accepted cost: at worst one
    /// phantom flash that did no damage, never a swallowed real one.
    /// </para>
    /// <para>
    /// <b>A world-sourced blast is never suppressed, by construction.</b> Its
    /// <c>SourceActorId</c> is <see cref="DeathMessage.EnvironmentKiller"/> (0xFFFF), which is
    /// not a legal actor id and so can never equal a local one. No special case is needed and
    /// none should be added.
    /// </para>
    /// <para>
    /// Fixed ring, no allocation. Predictions are rare — one per grenade or rocket the local
    /// player sets off.
    /// </para>
    /// </remarks>
    public sealed class ExplosionSuppressor
    {
        /// <summary>
        /// How long a prediction stays eligible to swallow a confirmation. One second is well
        /// past any plausible round-trip and well short of the gap between two blasts from the
        /// same player.
        /// </summary>
        public const float DefaultSuppressionWindowSeconds = 1f;

        /// <summary>Predictions held at once. More than one grenade in flight is normal.</summary>
        public const int DefaultCapacity = 8;

        private readonly ushort[] _sourceActorIds;
        private readonly float[] _predictedAtSeconds;
        private readonly bool[] _live;
        private int _next;

        public ExplosionSuppressor(int capacity = DefaultCapacity)
        {
            if (capacity < 1) capacity = 1;

            _sourceActorIds     = new ushort[capacity];
            _predictedAtSeconds = new float[capacity];
            _live               = new bool[capacity];
        }

        /// <summary>Seconds a prediction stays eligible to suppress.</summary>
        public float SuppressionWindowSeconds { get; set; } = DefaultSuppressionWindowSeconds;

        /// <summary>Predictions recorded this connection.</summary>
        public long PredictedCount { get; private set; }

        /// <summary>Confirmations swallowed this connection.</summary>
        public long SuppressedCount { get; private set; }

        /// <summary>
        /// Records that this client has already drawn a blast from
        /// <paramref name="sourceActorId"/>. Ignores the environment sentinel: the local player
        /// cannot predict the world's explosions, and recording one would arm a suppression that
        /// could only ever swallow a real blast.
        /// </summary>
        public void PredictLocal(ushort sourceActorId, float nowSeconds)
        {
            if (sourceActorId == DeathMessage.EnvironmentKiller) return;
            if (sourceActorId == LocalActorIdentity.UnassignedActorId) return;

            _sourceActorIds[_next]     = sourceActorId;
            _predictedAtSeconds[_next] = nowSeconds;
            _live[_next]               = true;
            _next                      = (_next + 1) % _live.Length;

            PredictedCount++;
        }

        /// <summary>
        /// Whether this message confirms a blast already drawn locally, consuming the
        /// prediction if so. One prediction swallows exactly one confirmation.
        /// </summary>
        public bool ShouldSuppress(in ExplosionMessage message, float nowSeconds)
        {
            if (message.SourceActorId == DeathMessage.EnvironmentKiller) return false;

            for (int i = 0; i < _live.Length; i++)
            {
                if (!_live[i]) continue;

                // Expire in the same pass. There is no other clock here, and a stale entry
                // left behind is precisely the failure the window exists to prevent.
                if (nowSeconds - _predictedAtSeconds[i] >= SuppressionWindowSeconds)
                {
                    _live[i] = false;
                    continue;
                }

                if (_sourceActorIds[i] != message.SourceActorId) continue;

                _live[i] = false;
                SuppressedCount++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Drops predictions older than the window. Optional — <see cref="ShouldSuppress"/>
        /// expires them as it scans — but useful for a client that wants the count to mean
        /// something while no explosions are arriving.
        /// </summary>
        public void Prune(float nowSeconds)
        {
            for (int i = 0; i < _live.Length; i++)
            {
                if (!_live[i]) continue;
                if (nowSeconds - _predictedAtSeconds[i] >= SuppressionWindowSeconds) _live[i] = false;
            }
        }

        /// <summary>Predictions still eligible to suppress at <paramref name="nowSeconds"/>.</summary>
        public int LiveCount(float nowSeconds)
        {
            int count = 0;

            for (int i = 0; i < _live.Length; i++)
            {
                if (!_live[i]) continue;
                if (nowSeconds - _predictedAtSeconds[i] >= SuppressionWindowSeconds) continue;
                count++;
            }

            return count;
        }

        /// <summary>Forgets every prediction. Call when leaving a match.</summary>
        public void Reset()
        {
            for (int i = 0; i < _live.Length; i++) _live[i] = false;

            _next           = 0;
            PredictedCount  = 0;
            SuppressedCount = 0;
        }
    }
}
