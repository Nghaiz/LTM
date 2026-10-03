namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The night-vision battery of Night Mode (phase P32): drains while the goggles are on,
    /// recharges while they are off, and switches them off when it runs out, so they are a choice
    /// about when to see rather than a permanent light switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full battery holds the room's <c>nightVisionSeconds</c>, which the host chose in the lobby
    /// (<c>RoomRules</c>: 10-180 s, default 45). It refills at a third of the rate it drains, and
    /// once empty it must refill to <see cref="MinimumToSwitchOn"/> before the goggles go back on,
    /// so flicking them on and off cannot stretch an empty battery.
    /// </para>
    /// <para>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it; <c>NightVisionGoggles</c>
    /// drives it and draws it.
    /// </para>
    /// </remarks>
    public sealed class NightVisionBattery
    {
        /// <summary>Seconds of recharge per second off, against one of drain per second on.</summary>
        public const float RechargeRatio = 1f / 3f;

        /// <summary>The share of a full battery the goggles need before they will switch on.</summary>
        public const float MinimumToSwitchOn = 0.15f;

        /// <summary>Below this share the battery reads as low: amber, then red and blinking.</summary>
        public const float LowFraction = 0.25f;

        public NightVisionBattery(float capacitySeconds)
        {
            Capacity = capacitySeconds > 0f ? capacitySeconds : 0f;
            Charge = Capacity;
        }

        /// <summary>Seconds a full battery holds.</summary>
        public float Capacity { get; }

        /// <summary>Seconds of night vision left.</summary>
        public float Charge { get; private set; }

        /// <summary>Whether the goggles are on.</summary>
        public bool IsOn { get; private set; }

        /// <summary>From 1 (full) to 0 (empty).</summary>
        public float Fraction => Capacity > 0f ? Charge / Capacity : 0f;

        /// <summary>Whether there is enough charge to switch on.</summary>
        public bool CanSwitchOn => Capacity > 0f && Fraction >= MinimumToSwitchOn;

        public bool IsLow => Fraction < LowFraction;

        /// <summary>Switches the goggles on if the battery allows it; returns whether they are on.</summary>
        public bool TrySwitchOn()
        {
            if (!IsOn && CanSwitchOn) IsOn = true;
            return IsOn;
        }

        public void SwitchOff() => IsOn = false;

        /// <summary>
        /// Runs the battery for <paramref name="deltaSeconds"/>. Returns true on the tick it runs
        /// out, when it has switched the goggles off itself.
        /// </summary>
        public bool Tick(float deltaSeconds)
        {
            if (deltaSeconds <= 0f || Capacity <= 0f) return false;

            if (!IsOn)
            {
                Charge = Charge + (deltaSeconds * RechargeRatio);
                if (Charge > Capacity) Charge = Capacity;
                return false;
            }

            Charge -= deltaSeconds;
            if (Charge > 0f) return false;

            Charge = 0f;
            IsOn = false;
            return true;
        }

        /// <summary>Back to full and off: a new life, or a new round.</summary>
        public void Refill()
        {
            Charge = Capacity;
            IsOn = false;
        }
    }
}
