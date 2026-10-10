using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A seated body walks no steps (owner report 2026-10-10: "driving a jeep or a quad bike sounds
    /// like footsteps, not the engine").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driving holds the same move keys as walking and the body rides a moving hull, so the
    /// first-person controller's step cycle ran on in the seat; with the capsule switched back on by
    /// a menu, the chat or a get-up, the steps played over the engine. The step cycle now stops while
    /// <c>seated</c> is set, and <c>FpsActorController.EnableInput</c> leaves the capsule off in a seat.
    /// </para>
    /// <para>
    /// The controller lives in Assembly-CSharp-firstpass, which no asmdef can reference, so it is
    /// reached by name; its Start does not run in edit mode and the step cycle is driven directly.
    /// </para>
    /// </remarks>
    public sealed class SeatedFootstepsTests
    {
        private static readonly Type ControllerType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("UnityStandardAssets.Characters.FirstPerson.FirstPersonController", false))
            .First(type => type != null);

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void TheStepCycleStopsInASeatAndRunsOnFoot()
        {
            var holder = new GameObject("FirstPersonController test");
            try
            {
                Component controller = holder.AddComponent(ControllerType);
                Set(controller, "externalMovementAuthority", true);
                Set(controller, "externalVelocitySource", (Func<Vector3>)(() => new Vector3(12f, 0f, 0f)));
                Set(controller, "m_Input", new Vector2(0f, 1f));
                Set(controller, "m_StepCycle", 0f);
                Set(controller, "m_NextStep", float.MaxValue);   // no step is played: only the cycle is read

                MethodInfo progress = ControllerType.GetMethod("ProgressStepCycle", Instance);

                Set(controller, "seated", true);
                progress.Invoke(controller, new object[] { 4f });
                Assert.AreEqual(0f, (float)Get(controller, "m_StepCycle"), "a driver walks no steps");

                Set(controller, "seated", false);
                progress.Invoke(controller, new object[] { 4f });
                Assert.Greater((float)Get(controller, "m_StepCycle"), 0f, "the same input on foot still walks");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        private static void Set(object target, string field, object value)
            => ControllerType.GetField(field, Instance).SetValue(target, value);

        private static object Get(object target, string field)
            => ControllerType.GetField(field, Instance).GetValue(target);
    }
}
