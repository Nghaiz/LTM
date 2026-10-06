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
        public void AReloadFromTheHipGoesDownOutOfViewToo()
        {
            foreach (string from in new[] { "Hip", "Unholster" })
            {
                AnimatorStateTransition down = State(from).transitions.Single(t => t.destinationState == State("Reload hidden"));
                Assert.IsTrue(down.conditions.Any(c => c.parameter == "reloading" && c.mode == AnimatorConditionMode.If), from);
            }
        }

        /// <summary>
        /// Owner report 2026-10-07: "when it is fired empty (0/0) the launcher is gone from my hands".
        /// The original redrew it after the shot, and drew it at all, only while ammunition was left.
        /// </summary>
        [Test]
        public void AnEmptyLauncherStaysInView()
        {
            foreach (string from in new[] { "Fire", "Out of frame" })
            {
                AnimatorStateTransition draw = State(from).transitions.Single(t => t.destinationState == State("Unholster"));
                Assert.IsFalse(draw.conditions.Any(c => c.parameter == "no ammo"), from + " waits for ammunition before the launcher comes up");
            }
        }

        /// <summary>
        /// Owner report 2026-10-07: "after the shot it reloads by itself -- it should take R". The
        /// original called <c>Reload()</c> after every shot and <c>ReloadDone()</c> when an empty
        /// launcher was drawn; online both loaded only the client's copy.
        /// </summary>
        [Test]
        public void TheLauncherReloadsOnlyWhenThePlayerAsks()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "Scripts/Assembly-CSharp/Javelin.cs"));

            StringAssert.DoesNotContain("Reload();", Body(source, "public override void Fire("));
            StringAssert.DoesNotContain("ReloadDone", Body(source, "public override void Unholster()"));
        }

        /// <summary>The text of the method whose signature starts with <paramref name="signature"/>, to its closing brace.</summary>
        private static string Body(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, signature);
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            Assert.Fail("unbalanced braces after " + signature);
            return string.Empty;
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
