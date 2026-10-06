using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The guided launcher's reload can be seen, and fits inside the original's reload time:
    /// down out of view after the shot, up again just as the launcher is loaded.
    /// </summary>
    /// <remarks>
    /// Owner request 2026-10-06: "the guided launcher has no reload animation". The original had
    /// none either -- its controller has no reload state -- so the launcher sat in view through
    /// the whole reload. <c>Javelin.Reload</c> drives the controller's "reloading" bool; this pins
    /// the two halves together, since a renamed parameter or a slower raise would fail silently.
    /// </remarks>
    public sealed class JavelinReloadAnimationTests
    {
        private const string ControllerPath = "Assets/AnimatorController/Old Javelin.controller";

        private static AnimatorController Controller => AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

        private static AnimatorState State(string name)
            => Controller.layers[0].stateMachine.states.Single(s => s.state.name == name).state;

        private static float RaiseSecondsInCode()
        {
            Type javelin = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Javelin", false)).First(t => t != null);
            return (float)javelin.GetField("ReloadRaiseSeconds", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        }

        private static float ReloadTime()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/javelin.prefab");
            Component weapon = prefab.GetComponents<Component>().First(c => c.GetType().Name == "Javelin");
            object configuration = weapon.GetType().GetField("configuration").GetValue(weapon);
            return (float)configuration.GetType().GetField("reloadTime").GetValue(configuration);
        }

        [Test]
        public void TheShotLowersTheLauncherWhileItReloads()
        {
            Assert.IsTrue(Controller.parameters.Any(p => p.name == "reloading" && p.type == AnimatorControllerParameterType.Bool));

            AnimatorStateTransition down = State("Fire").transitions.Single(t => t.destinationState == State("Reload hidden"));
            Assert.IsTrue(down.conditions.Any(c => c.parameter == "reloading" && c.mode == AnimatorConditionMode.If));

            AnimatorStateTransition up = State("Reload hidden").transitions.Single(t => t.destinationState == State("Reload raise"));
            Assert.IsTrue(up.conditions.Any(c => c.parameter == "reloading" && c.mode == AnimatorConditionMode.IfNot));

            Assert.IsTrue(State("Reload raise").transitions.Any(t => t.destinationState == State("Hip")));
        }

        [Test]
        public void TheRaiseTheCodeWaitsForIsTheRaiseTheControllerPlays()
        {
            AnimatorState raise = State("Reload raise");
            float played = raise.motion.averageDuration / raise.speed;

            Assert.AreEqual(played, RaiseSecondsInCode(), 0.02f);
        }

        [Test]
        public void TheWholeReloadFitsInTheOriginalsReloadTime()
        {
            AnimatorState fire = State("Fire");
            AnimatorStateTransition down = fire.transitions.Single(t => t.destinationState == State("Reload hidden"));
            float lowered = down.exitTime * fire.motion.averageDuration + down.duration;
            float raiseStarts = ReloadTime() - RaiseSecondsInCode();

            Assert.AreEqual(2f, ReloadTime(), 0.001f, "the original's reload time");
            Assert.Less(lowered, raiseStarts, "the launcher would come up before it went down");
        }
    }
}
