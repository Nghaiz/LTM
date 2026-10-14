using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A guided launcher's finished lock is the same answer to every question asked in a frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server asks <c>Javelin.WithholdsTrigger</c> whether a pull is a shot, then hands the
    /// pull to <c>Javelin.Fire</c>, and both read the lock. The lock's timer is the engine's
    /// <c>Action</c>, whose <c>Done</c> answers false once when the timer runs out and true from
    /// the next call on. Asked through <c>Done</c>, the first question on the frame the lock
    /// completed was told "no lock" and the second "locked": the trigger was withheld from the
    /// combat authority and the engine launched anyway, latching <c>holdingFire</c> for the rest
    /// of the life. 78 "NOTHING WAS LAUNCHED" refusals on the live servers, 2026-10-12..14.
    /// </para>
    /// <para>
    /// <c>Javelin</c> and <c>Action</c> compile into Assembly-CSharp, which no asmdef can reference,
    /// so they are reached by name, as <c>JavelinReloadAnimationTests</c> does.
    /// </para>
    /// </remarks>
    public sealed class JavelinLockOnServerTests
    {
        private GameObject _body;
        private Component _launcher;

        [SetUp]
        public void SetUp()
        {
            _body = new GameObject(nameof(JavelinLockOnServerTests));
            _launcher = _body.AddComponent(GameType("Javelin"));

            // Locking on something: the lock is the timer, the target only has to exist.
            Set(_launcher, "target", _body.transform);
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_body);

        [Test]
        public void AFinishedLockLetsTheFirstPullThrough()
        {
            ExpireTheLockTimerJustNow();

            Assert.IsFalse(WithholdsTrigger(),
                "the lock's two seconds are over, so the pull is a shot for the combat authority");
        }

        [Test]
        public void AFinishedLockIsTheSameAnswerTwiceInAFrame()
        {
            ExpireTheLockTimerJustNow();

            bool first = WithholdsTrigger();
            bool second = WithholdsTrigger();

            Assert.AreEqual(first, second, "one frame, one lock: the withhold check and the pull must agree");
        }

        [Test]
        public void ALockStillRunningWithholdsThePull()
        {
            object timer = Get(_launcher, "lockOnAction");
            Set(timer, "end", Time.time + 1f);
            Set(timer, "lied", false);

            Assert.IsTrue(WithholdsTrigger(), "a lock with a second still to run is not a shot yet");
        }

        /// <summary>The state the frame after the lock's timer ran out, before anything asked.</summary>
        private void ExpireTheLockTimerJustNow()
        {
            object timer = Get(_launcher, "lockOnAction");
            Set(timer, "end", Time.time - 0.01f);
            Set(timer, "lied", false);
        }

        private bool WithholdsTrigger()
            => (bool)_launcher.GetType().GetMethod("WithholdsTrigger").Invoke(_launcher, null);

        private static Type GameType(string name)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(name, false))
                .First(t => t != null);

        private static FieldInfo Field(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
            }

            throw new MissingFieldException(target.GetType().Name, name);
        }

        private static object Get(object target, string name) => Field(target, name).GetValue(target);

        private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
    }
}
