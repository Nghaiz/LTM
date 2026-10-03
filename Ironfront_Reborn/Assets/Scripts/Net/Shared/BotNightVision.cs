namespace Ironfront.Net.Unity
{
    /// <summary>
    /// When a bot wears its goggles at night (phase P32): on for a fight or the last stretch to its
    /// objective, off once it has been quiet for <see cref="QuietSecondsBeforeOff"/>, and never past
    /// what the battery allows -- the same <see cref="NightVisionBattery"/> a player carries.
    /// </summary>
    /// <remarks>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it; the bot's controller ticks
    /// it and reads <see cref="IsOn"/> when it looks for targets.
    /// </remarks>
    public sealed class BotNightVision
    {
        /// <summary>How long without a fight or an objective in reach before a bot takes the goggles off.</summary>
        public const float QuietSecondsBeforeOff = 6f;

        /// <summary>
        /// A bot keeps this share of the battery back while it is only closing on its objective, so
        /// the fight it is walking into finds the goggles with charge in them.
        /// </summary>
        public const float ApproachReserve = 0.4f;

        /// <summary>
        /// How far above <see cref="ApproachReserve"/> the battery must refill before the approach
        /// puts the goggles back on, so they do not flick on and off at the reserve.
        /// </summary>
        public const float ApproachHysteresis = 0.15f;

        private float _quietFor;

        public BotNightVision(float batterySeconds)
        {
            Battery = new NightVisionBattery(batterySeconds);
        }

        public NightVisionBattery Battery { get; }

        public bool IsOn => Battery.IsOn;

        /// <summary>
        /// Runs the goggles for <paramref name="deltaSeconds"/>: <paramref name="inFight"/> when the
        /// bot has a target or is under fire, <paramref name="nearObjective"/> when it is closing on
        /// the flag it was sent to.
        /// </summary>
        public void Tick(float deltaSeconds, bool inFight, bool nearObjective)
        {
            _quietFor = inFight || nearObjective ? 0f : _quietFor + deltaSeconds;

            bool approach = nearObjective && Battery.Fraction > ApproachReserve + ApproachHysteresis;
            if ((inFight || approach) && !Battery.IsOn) Battery.TrySwitchOn();

            // Out of a fight the goggles keep the reserve back, and come off once all is quiet.
            if (Battery.IsOn && !inFight && Battery.Fraction <= ApproachReserve) Battery.SwitchOff();
            if (Battery.IsOn && _quietFor >= QuietSecondsBeforeOff) Battery.SwitchOff();

            Battery.Tick(deltaSeconds);
        }

        /// <summary>A new life: a full battery, goggles off.</summary>
        public void Refill()
        {
            Battery.Refill();
            _quietFor = 0f;
        }
    }
}
