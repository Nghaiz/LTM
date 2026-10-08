using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The local player's footsteps play at full volume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owner request 2026-10-07: "the player's footsteps are far too quiet". The first-person
    /// controller plays its steps with <c>PlayOneShot</c> through the AudioSource on the player
    /// root, which the prefab had at 0.5. That source also carries the jump and land sounds, so
    /// all three body sounds rise together and keep their balance with each other.
    /// </para>
    /// <para>
    /// Owner, again on 2026-10-08 after the v4.5.0 playtest: "even at 100 % system volume I hear
    /// them only faintly". The clips themselves were the cause: recorded 5.6 to 13.8 dB under full
    /// scale (RMS -25 to -31 dBFS, against -10 for the jump), and played from a 3D source at the
    /// feet, a metre and a half under the listener. They are now peak-normalised to -1 dBFS
    /// (<c>tools/normalize_wav.py</c>, +4.6 to +12.8 dB) and the source is 2D: these are the
    /// listener's own steps.
    /// </para>
    /// </remarks>
    public sealed class PlayerFootstepVolumeTests
    {
        /// <summary>Quietest peak a footstep clip may have, dBFS. Normalised to -1; 0.5 dB of slack.</summary>
        private const float QuietestPeakDb = -1.5f;

        [Test]
        public void TheFootstepsPlayAtFullVolume()
        {
            AudioClip[] steps = Steps(out AudioSource source);

            Assert.IsTrue(steps.Length > 0 && steps.All(clip => clip != null), "the footstep clips");
            Assert.AreEqual(1f, source.volume, 1e-4f, "the source the steps are played through");
            Assert.AreEqual(0f, source.spatialBlend, 1e-4f,
                "the player's own steps are played 2D, not from the feet under the listener");
        }

        [Test]
        public void EveryFootstepClipIsNormalised()
        {
            foreach (AudioClip clip in Steps(out _))
            {
                var samples = new float[clip.samples * clip.channels];
                Assert.IsTrue(clip.GetData(samples, 0), clip.name + " could not be read");
                float peak = samples.Max(Mathf.Abs);
                float peakDb = 20f * Mathf.Log10(Mathf.Max(peak, 1e-6f));
                Assert.GreaterOrEqual(peakDb, QuietestPeakDb,
                    $"{clip.name} peaks at {peakDb:F1} dBFS: re-run tools/normalize_wav.py --peak-db -1 on it");
            }
        }

        private static AudioClip[] Steps(out AudioSource source)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/Player Fps Actor.prefab");
            Component controller = prefab.GetComponents<Component>().Single(c => c.GetType().Name == "FirstPersonController");
            source = controller.GetComponent<AudioSource>();
            return (AudioClip[])controller.GetType()
                .GetField("m_FootstepSounds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(controller);
        }
    }
}
