using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// Watches a tumbling body's velocity and reports each hard impact once it is over: how much
    /// speed the body lost hitting the ground or a wall, the number <see cref="FallDamage"/> is
    /// paid on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner ruling 2026-10-06</b>: "a free fall from 10 m is not the same as being thrown by
    /// a big force at high speed; that must hurt more". So a ragdoll's fall damage is measured on
    /// the speed it loses in the impact, whatever brought it to that speed -- a fall, a blast that
    /// threw it, a vehicle that hit it -- rather than on the height it fell.
    /// </para>
    /// <para>
    /// <b>Fed the centre of mass's velocity</b>, which only something outside the body changes:
    /// the joints pulling a ragdoll's parts on each other cancel out there, so a blast landing on
    /// the hips and the legs catching up a step later do not read as an impact.
    /// </para>
    /// <para>
    /// <b>Only a loss of speed is an impact.</b> The loss is the largest change of velocity, over
    /// the last <see cref="WindowSeconds"/>, from any earlier sample that was faster than now: a
    /// landing stops a body within a few physics steps, while a slide that slows down over a
    /// second never loses that much inside the window, and a blast that speeds a body up loses
    /// nothing at all. A bounce counts in full: a body that hits at 15 m/s and comes off at 5 m/s
    /// the other way lost 20. An impact spread over several steps is reported once, when it has
    /// stopped growing for <see cref="SettleSeconds"/>.
    /// </para>
    /// </remarks>
    public sealed class ImpactDetector
    {
        /// <summary>Seconds over which a loss of speed counts as one impact.</summary>
        public const float WindowSeconds = 0.25f;

        /// <summary>Seconds an impact must stop growing before it is reported.</summary>
        public const float SettleSeconds = 0.1f;

        private const int Capacity = 64;

        private readonly Vec3[] _velocity = new Vec3[Capacity];
        private readonly float[] _time = new float[Capacity];
        private int _count;
        private int _head;

        private bool _impacting;
        private float _impactLoss;
        private float _impactGrewAt;

        /// <summary>Forgets everything seen: a new body, or a new fall.</summary>
        public void Reset()
        {
            _count = 0;
            _head = 0;
            _impacting = false;
            _impactLoss = 0f;
        }

        /// <summary>
        /// One sample of the body's velocity at <paramref name="time"/> seconds. Returns the speed
        /// lost in an impact that has just ended, if that loss reached <paramref name="threshold"/>
        /// m/s; otherwise zero.
        /// </summary>
        public float Observe(in Vec3 velocity, float time, float threshold)
        {
            float speed = velocity.Magnitude;
            float loss = 0f;
            for (int i = 0; i < _count; i++)
            {
                int slot = (_head - 1 - i + Capacity) % Capacity;
                if (time - _time[slot] > WindowSeconds) break;
                Vec3 earlier = _velocity[slot];
                if (!(earlier.Magnitude > speed)) continue;
                float lost = (earlier - velocity).Magnitude;
                if (lost > loss) loss = lost;
            }

            _velocity[_head] = velocity;
            _time[_head] = time;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;

            if (loss >= threshold && loss > _impactLoss)
            {
                _impacting = true;
                _impactLoss = loss;
                _impactGrewAt = time;
            }

            if (!_impacting || time - _impactGrewAt < SettleSeconds) return 0f;

            float reported = _impactLoss;
            _impacting = false;
            _impactLoss = 0f;
            // The samples before the impact are spent: keep only where the body is now, so the
            // same impact is never reported twice.
            _velocity[0] = velocity;
            _time[0] = time;
            _head = 1;
            _count = 1;
            return reported;
        }
    }
}
