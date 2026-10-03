using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Night Mode's night-vision battery (phase P32): a decision about when to see, not a light
    /// switch left on.
    /// </summary>
    public sealed class NightVisionBatteryTests
    {
        [Fact]
        public void AFullBatteryLastsTheRoomsSecondsThenSwitchesItselfOff()
        {
            var battery = new NightVisionBattery(45f);
            Assert.True(battery.TrySwitchOn());

            Assert.False(battery.Tick(44.9f));
            Assert.True(battery.IsOn);
            Assert.True(battery.Tick(0.2f), "the tick that empties it reports it");
            Assert.False(battery.IsOn);
            Assert.Equal(0f, battery.Charge);
        }

        [Fact]
        public void ItRechargesAtAThirdOfTheDrainWhileOff()
        {
            var battery = new NightVisionBattery(30f);
            battery.TrySwitchOn();
            battery.Tick(30f);

            battery.Tick(30f);
            Assert.Equal(10f, battery.Charge, 3);

            battery.Tick(1000f);
            Assert.Equal(30f, battery.Charge, 3);
        }

        [Fact]
        public void AnEmptyBatteryMustRechargeBeforeTheGogglesGoBackOn()
        {
            var battery = new NightVisionBattery(60f);
            battery.TrySwitchOn();
            battery.Tick(60f);

            battery.Tick(3f * 60f * NightVisionBattery.MinimumToSwitchOn - 0.5f);
            Assert.False(battery.TrySwitchOn(), "flicking it on and off cannot stretch an empty battery");

            battery.Tick(1f);
            Assert.True(battery.TrySwitchOn());
        }

        [Fact]
        public void ItReadsLowUnderAQuarter()
        {
            var battery = new NightVisionBattery(40f);
            battery.TrySwitchOn();
            battery.Tick(29f);
            Assert.False(battery.IsLow);
            battery.Tick(2f);
            Assert.True(battery.IsLow);
        }

        [Fact]
        public void ARefillIsFullAndOff()
        {
            var battery = new NightVisionBattery(20f);
            battery.TrySwitchOn();
            battery.Tick(15f);
            battery.Refill();
            Assert.False(battery.IsOn);
            Assert.Equal(1f, battery.Fraction);
        }

        [Fact]
        public void NoBatteryNeverSwitchesOn()
        {
            var battery = new NightVisionBattery(0f);
            Assert.False(battery.TrySwitchOn());
            Assert.False(battery.Tick(1f));
        }
    }
}
