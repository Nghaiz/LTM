using System;
using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Night Mode's map reveal (phase P32): in the dark the map shows only the enemies the eye
    /// could make out, or those standing in a pumpkin's or lamp's light; with night vision, the
    /// usual radius. Owner report 2026-10-04: the radar was a map hack in the dark.
    /// </summary>
    /// <remarks>Every test starts from <see cref="EnemyMapReveal.Clear"/>: the class is static.</remarks>
    public sealed class EnemyMapRevealTests : IDisposable
    {
        private const float Seeing = 60f;

        public EnemyMapRevealTests() => EnemyMapReveal.Clear();

        public void Dispose() => EnemyMapReveal.Clear();

        [Fact]
        public void ByDayEveryEnemyShowsAtTheUsualRadius()
        {
            EnemyMapReveal.AddLight(0f, 0f);
            Assert.False(EnemyMapReveal.IsDark);
            Assert.Equal(Seeing, EnemyMapReveal.RevealRadius(500f, 500f, Seeing));
        }

        [Fact]
        public void InTheDarkAnEnemyShowsOnlyAsFarAsTheEyeReaches()
        {
            EnemyMapReveal.SetDark(25f, 10f);
            Assert.Equal(25f, EnemyMapReveal.RevealRadius(500f, 500f, Seeing));
        }

        [Fact]
        public void TheDarkRadiusNeverWidensTheUsualOne()
        {
            EnemyMapReveal.SetDark(100f, 10f);
            Assert.Equal(Seeing, EnemyMapReveal.RevealRadius(0f, 0f, Seeing));
        }

        [Fact]
        public void AnEnemyInAPumpkinsLightShowsAtTheUsualRadiusInTheDark()
        {
            EnemyMapReveal.AddLight(100f, 100f);
            EnemyMapReveal.SetDark(25f, 10f);

            Assert.True(EnemyMapReveal.IsLit(108f, 104f));
            Assert.Equal(Seeing, EnemyMapReveal.RevealRadius(108f, 104f, Seeing));

            Assert.False(EnemyMapReveal.IsLit(111f, 100f), "11 m from the pumpkin is outside its 10 m light.");
            Assert.Equal(25f, EnemyMapReveal.RevealRadius(111f, 100f, Seeing));
        }

        [Fact]
        public void ALightAcrossACellBorderStillLights()
        {
            // 16 m cells: the light at x=-1 and the enemy at x=5 sit in different cells.
            EnemyMapReveal.AddLight(-1f, -1f);
            EnemyMapReveal.SetDark(25f, 10f);
            Assert.True(EnemyMapReveal.IsLit(5f, 5f));
        }

        [Fact]
        public void NightVisionBringsTheUsualRadiusBack()
        {
            EnemyMapReveal.SetDark(25f, 10f);
            EnemyMapReveal.SetSeeing();
            Assert.False(EnemyMapReveal.IsDark);
            Assert.Equal(Seeing, EnemyMapReveal.RevealRadius(500f, 500f, Seeing));
        }

        [Fact]
        public void ClearingForgetsTheLightsAndTheDark()
        {
            EnemyMapReveal.AddLight(0f, 0f);
            EnemyMapReveal.SetDark(25f, 10f);
            EnemyMapReveal.Clear();

            Assert.Equal(0, EnemyMapReveal.LightCount);
            Assert.False(EnemyMapReveal.IsDark);
            EnemyMapReveal.SetDark(25f, 10f);
            Assert.False(EnemyMapReveal.IsLit(0f, 0f), "a light from the last map still lit this one.");
        }

        [Fact]
        public void NegativeRadiiAreRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => EnemyMapReveal.SetDark(-1f, 10f));
            Assert.Throws<ArgumentOutOfRangeException>(() => EnemyMapReveal.SetDark(25f, -1f));
        }
    }
}
