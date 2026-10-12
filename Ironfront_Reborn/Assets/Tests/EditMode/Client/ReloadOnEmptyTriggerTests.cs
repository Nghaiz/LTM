using System;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A trigger pulled, or still held, on an empty clip reloads, and a reload lowers the sights
    /// in the frame it starts (owner's run of 2026-10-12).
    /// </summary>
    /// <remarks>
    /// <c>FpsActorController</c>, <c>Actor</c> and <c>Weapon</c> compile into Assembly-CSharp, which
    /// no asmdef can reference, so they are reached by name, as
    /// <see cref="JavelinReloadAnimationTests"/> reaches the launcher. The weapon's spare ammo is
    /// infinite (<c>spareAmmo == -2</c>) so it can answer <c>HasSpareAmmo</c> without a body
    /// carrying a reserve.
    /// </remarks>
    public sealed class ReloadOnEmptyTriggerTests
    {
        private GameObject _body;
        private Component _controller;
        private Component _weapon;
        private FakeButtons _input;

        [SetUp]
        public void SetUp()
        {
            _body = new GameObject(nameof(ReloadOnEmptyTriggerTests));
            Component actor = _body.AddComponent(GameType("Actor"));
            _weapon = _body.AddComponent(GameType("Weapon"));
            _controller = _body.AddComponent(GameType("FpsActorController"));

            Set(_controller, "actor", actor);
            Set(actor, "activeWeapon", _weapon);

            // A component added outside the serializer gets no Configuration of its own.
            FieldInfo configurationField = Field(_weapon, "configuration");
            object configuration = Activator.CreateInstance(configurationField.FieldType);
            configurationField.SetValue(_weapon, configuration);
            Set(configuration, "ammo", 30);
            Set(configuration, "spareAmmo", -2);
            Set(_weapon, "ammo", 0);
            Set(_weapon, "unholstered", true);

            _input = new FakeButtons();
            _controller.GetType().GetMethod("SetInputSource").Invoke(_controller, new object[] { _input });
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_body);

        [Test]
        public void TheTriggerOnAnEmptyClipReloads()
        {
            _input.Buttons = (ushort)InputButtons.Fire;

            Assert.IsTrue(ControllerAsksForAReload());
        }

        [Test]
        public void AnEmptyClipWithTheTriggerUpWaitsForTheTrigger()
        {
            _input.Buttons = 0;

            Assert.IsFalse(ControllerAsksForAReload());
        }

        [Test]
        public void ALoadedClipFiresRatherThanReloads()
        {
            Set(_weapon, "ammo", 1);
            _input.Buttons = (ushort)InputButtons.Fire;

            Assert.IsFalse(ControllerAsksForAReload());
        }

        [Test]
        public void NoSpareRoundsMeansNoReload()
        {
            Set(Get(_weapon, "configuration"), "spareAmmo", 0);
            _input.Buttons = (ushort)InputButtons.Fire;

            Assert.IsFalse(ControllerAsksForAReload());
        }

        [Test]
        public void AReloadUnderwayIsNotAskedForAgain()
        {
            Set(_weapon, "reloading", true);
            _input.Buttons = (ushort)InputButtons.Fire;

            Assert.IsFalse(ControllerAsksForAReload());
        }

        [Test]
        public void ASprintWithTheTriggerDownReloadsNothing()
        {
            _input.Buttons = (ushort)(InputButtons.Fire | InputButtons.Sprint);

            Assert.IsFalse(ControllerAsksForAReload());
        }

        [Test]
        public void RStillReloadsWithTheTriggerUp()
        {
            Set(_weapon, "ammo", 12);
            _input.Buttons = (ushort)InputButtons.Reload;

            Assert.IsTrue(ControllerAsksForAReload());
        }

        [Test]
        public void AReloadLowersTheSightsInTheFrameItStarts()
        {
            Set(_weapon, "aiming", true);

            _weapon.GetType().GetMethod("Reload").Invoke(_weapon, new object[] { false });

            Assert.IsTrue((bool)Get(_weapon, "reloading"));
            Assert.IsFalse((bool)Get(_weapon, "aiming"), "the sights come down before the reload plays");
        }

        private bool ControllerAsksForAReload()
            => (bool)_controller.GetType().GetMethod("Reload", Type.EmptyTypes).Invoke(_controller, null);

        private static Type GameType(string name)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(name, false)).First(t => t != null);

        private static FieldInfo Field(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(
                    name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(target.GetType().Name, name);
        }

        private static object Get(object target, string name) => Field(target, name).GetValue(target);

        private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);

        private sealed class FakeButtons : IInputSource
        {
            public float MoveX => 0f;
            public float MoveZ => 0f;
            public float Yaw => 0f;
            public float Pitch => 0f;
            public float Lean => 0f;
            public float LookDeltaX => 0f;
            public float LookDeltaY => 0f;
            public ushort Buttons { get; set; }
            public float HeliYaw => 0f;
            public float HeliCollective => 0f;
            public float HeliRoll => 0f;
            public float HeliPitch => 0f;
            public bool RespawnPressed => false;
            public bool SeatTogglePressed => false;
        }
    }
}
