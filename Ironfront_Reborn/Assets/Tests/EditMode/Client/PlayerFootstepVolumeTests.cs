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
    /// Owner request 2026-10-07: "the player's footsteps are far too quiet". The first-person
    /// controller plays its steps with <c>PlayOneShot</c> through the AudioSource on the player
    /// root, which the prefab had at 0.5. That source also carries the jump and land sounds, so
    /// all three body sounds rise together and keep their balance with each other.
    /// </remarks>
    public sealed class PlayerFootstepVolumeTests
    {
        [Test]
        public void TheFootstepsPlayAtFullVolume()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/Player Fps Actor.prefab");
            Component controller = prefab.GetComponents<Component>().Single(c => c.GetType().Name == "FirstPersonController");
            var steps = (AudioClip[])controller.GetType()
                .GetField("m_FootstepSounds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(controller);
            AudioSource source = controller.GetComponent<AudioSource>();

            Assert.IsTrue(steps.Length > 0 && steps.All(clip => clip != null), "the footstep clips");
            Assert.AreEqual(1f, source.volume, 1e-4f, "the source the steps are played through");
        }
    }
}
