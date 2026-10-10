using Ironfront.Net.Replication.Client;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The hitmarker tells a body hit, a headshot, a kill and a headshot kill apart (owner's run of
    /// 2026-10-10, phase P38), and a burst reads as a burst.
    /// </summary>
    public sealed class HitFeedbackRulesTests
    {
        private static readonly int[] Severities =
        {
            HitFeedbackRules.Body, HitFeedbackRules.Headshot, HitFeedbackRules.Kill, HitFeedbackRules.HeadshotKill,
        };

        [Test]
        public void TheSeveritiesAreTheHitmarkerModelsOwnNumbers()
        {
            // The HUD is handed the model's severity as an int; the two orders are one contract.
            Assert.AreEqual((int)HitmarkerSeverity.Normal, HitFeedbackRules.Body);
            Assert.AreEqual((int)HitmarkerSeverity.Headshot, HitFeedbackRules.Headshot);
            Assert.AreEqual((int)HitmarkerSeverity.Kill, HitFeedbackRules.Kill);
            Assert.AreEqual((int)HitmarkerSeverity.HeadshotKill, HitFeedbackRules.HeadshotKill);
        }

        [Test]
        public void EachKindOfHitHasItsOwnColour()
        {
            for (int i = 0; i < Severities.Length; i++)
                for (int j = i + 1; j < Severities.Length; j++)
                    Assert.AreNotEqual(HitFeedbackRules.Colour(Severities[i]), HitFeedbackRules.Colour(Severities[j]),
                        $"severities {Severities[i]} and {Severities[j]} draw the same cross");

            Assert.AreEqual(Color.white, HitFeedbackRules.Colour(HitFeedbackRules.Body));
        }

        [Test]
        public void ALouderHitShowsLongerAndPopsHarder()
        {
            for (int i = 1; i < Severities.Length; i++)
            {
                Assert.Greater(HitFeedbackRules.Seconds(Severities[i]), HitFeedbackRules.Seconds(Severities[i - 1]));
                Assert.Greater(HitFeedbackRules.Pop(Severities[i]), HitFeedbackRules.Pop(Severities[i - 1]));
            }
        }

        [Test]
        public void TheCrossPopsAndSettlesAndFades()
        {
            Assert.AreEqual(1f + HitFeedbackRules.Pop(HitFeedbackRules.Kill), HitFeedbackRules.Scale(HitFeedbackRules.Kill, 0f), 1e-5f);
            Assert.AreEqual(1f, HitFeedbackRules.Scale(HitFeedbackRules.Kill, 0.5f), 1e-5f);
            Assert.AreEqual(1f, HitFeedbackRules.Alpha(0.5f));
            Assert.AreEqual(0f, HitFeedbackRules.Alpha(1f));
        }

        [Test]
        public void AQuieterHitDoesNotCutAKillShort()
        {
            Assert.IsFalse(HitFeedbackRules.Replaces(HitFeedbackRules.Kill, 0.2f, HitFeedbackRules.Body));
            Assert.IsTrue(HitFeedbackRules.Replaces(HitFeedbackRules.Kill, 0.9f, HitFeedbackRules.Body));
            Assert.IsTrue(HitFeedbackRules.Replaces(HitFeedbackRules.Body, 0.1f, HitFeedbackRules.Body),
                "every round that lands restarts the cross");
            Assert.IsTrue(HitFeedbackRules.Replaces(HitFeedbackRules.Headshot, 0.1f, HitFeedbackRules.HeadshotKill));
        }

        [Test]
        public void EveryLouderHitHasASoundThatShips()
        {
            Assert.IsNull(HitFeedbackRules.SoundPath(HitFeedbackRules.Body), "a body hit keeps the HUD's own tick");
            for (int i = 1; i < Severities.Length; i++)
            {
                string path = HitFeedbackRules.SoundPath(Severities[i]);
                Assert.IsNotNull(Resources.Load<AudioClip>(path), $"{path} is not in Resources");
            }
        }
    }
}
