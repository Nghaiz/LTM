using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The rules of the player's key bindings (owner's list of 2026-10-09, item 3): the defaults are
    /// the keys the game shipped with, a key lives in one slot, and what is saved comes back.
    /// </summary>
    public sealed class KeyBindingSetTests
    {
        [Test]
        public void TheCatalogueIsInEnumOrderWithUniqueIds()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < GameActionCatalog.All.Count; i++)
            {
                GameActionInfo info = GameActionCatalog.All[i];
                Assert.AreEqual(i, (int)info.Action, $"{info.Id} is out of enum order; Get(action) would answer another action.");
                Assert.IsTrue(ids.Add(info.Id), $"Two actions are saved under '{info.Id}'.");
            }

            Assert.AreEqual(System.Enum.GetValues(typeof(GameAction)).Length, GameActionCatalog.All.Count,
                "Every GameAction needs a catalogue entry, or it has no default key and no name on screen.");
        }

        [Test]
        public void TheDefaultsAreTheKeysTheGameShippedWith()
        {
            var keys = new KeyBindingSet();

            Assert.AreEqual(KeyCode.W, keys.Get(GameAction.MoveForward, 0));
            Assert.AreEqual(KeyCode.UpArrow, keys.Get(GameAction.MoveForward, 1));
            Assert.AreEqual(KeyCode.LeftControl, keys.Get(GameAction.Crouch, 0));
            Assert.AreEqual(KeyCode.C, keys.Get(GameAction.Crouch, 1));
            Assert.AreEqual(KeyCode.Mouse0, keys.Get(GameAction.Fire, 0));
            Assert.AreEqual(KeyCode.Mouse1, keys.Get(GameAction.Aim, 0));
            Assert.AreEqual(KeyCode.F, keys.Get(GameAction.Use, 0));
            Assert.AreEqual(KeyCode.Tab, keys.Get(GameAction.Scoreboard, 0));
            Assert.AreEqual(KeyCode.Return, keys.Get(GameAction.Chat, 0));
            Assert.IsTrue(keys.IsDefault);
        }

        [Test]
        public void NoTwoDefaultSlotsShareAKey()
        {
            var seen = new System.Collections.Generic.Dictionary<KeyCode, string>();
            foreach (GameActionInfo info in GameActionCatalog.All)
            {
                foreach (KeyCode key in new[] { info.DefaultPrimary, info.DefaultSecondary })
                {
                    if (key == KeyCode.None) continue;
                    Assert.IsFalse(seen.TryGetValue(key, out string first), $"{key} is a default of both {first} and {info.Id}.");
                    seen[key] = info.Id;
                }
            }
        }

        [Test]
        public void BindingATakenKeyTakesItAndSaysFromWhom()
        {
            var keys = new KeyBindingSet();

            KeyBindingSet.BindResult result = keys.Bind(GameAction.Reload, 0, KeyCode.F);

            Assert.IsTrue(result.Accepted);
            Assert.IsTrue(result.Displaced, "The screen must be told the player just unbound enter/exit.");
            Assert.AreEqual(GameAction.Use, result.DisplacedAction);
            Assert.AreEqual(KeyCode.F, keys.Get(GameAction.Reload, 0));
            Assert.AreEqual(KeyCode.None, keys.Get(GameAction.Use, 0), "F must not fire two actions at once.");
            Assert.IsTrue(keys.IsUnbound(GameAction.Use));
        }

        [Test]
        public void MovingAKeyBetweenAnActionsOwnSlotsDisplacesNobody()
        {
            var keys = new KeyBindingSet();

            KeyBindingSet.BindResult result = keys.Bind(GameAction.Crouch, 1, KeyCode.LeftControl);

            Assert.IsFalse(result.Displaced);
            Assert.AreEqual(KeyCode.LeftControl, keys.Get(GameAction.Crouch, 1));
            Assert.AreEqual(KeyCode.None, keys.Get(GameAction.Crouch, 0));
        }

        [Test]
        public void EscapeCanNeverBeBound()
        {
            var keys = new KeyBindingSet();

            Assert.IsFalse(keys.Bind(GameAction.Jump, 0, KeyCode.Escape).Accepted,
                "Escape opens the pause menu and cancels a rebind; no action may take it.");
            Assert.AreEqual(KeyCode.Space, keys.Get(GameAction.Jump, 0));
        }

        [Test]
        public void WhatIsSavedComesBack()
        {
            var keys = new KeyBindingSet();
            keys.Bind(GameAction.Jump, 0, KeyCode.Mouse4);
            keys.Bind(GameAction.Reload, 1, KeyCode.T);
            keys.Clear(GameAction.LeanLeft, 0);

            KeyBindingSet loaded = KeyBindingSet.Parse(keys.Serialize());

            foreach (GameActionInfo info in GameActionCatalog.All)
                for (int slot = 0; slot < KeyBindingSet.Slots; slot++)
                    Assert.AreEqual(keys.Get(info.Action, slot), loaded.Get(info.Action, slot), $"{info.Id} slot {slot}");
        }

        [Test]
        public void AnActionMissingFromSavedTextKeepsItsDefault()
        {
            // An older save knows nothing of an action added since: it must get its default key,
            // not end up unbound.
            KeyBindingSet loaded = KeyBindingSet.Parse("jump=Mouse4,None;nonsense=Q,Q;reload=NotAKey,R");

            Assert.AreEqual(KeyCode.Mouse4, loaded.Get(GameAction.Jump, 0));
            Assert.AreEqual(KeyCode.F, loaded.Get(GameAction.Use, 0));
            Assert.AreEqual(KeyCode.R, loaded.Get(GameAction.Reload, 0), "An unreadable entry keeps the default.");
        }

        [Test]
        public void LeanEasesLikeTheOldInputManagerAxis()
        {
            float lean = 0f;
            lean = GameKeys.StepLean(lean, 1f, 0.05f);
            Assert.AreEqual(0.5f, lean, 1e-5f, "Sensitivity 10: half way in 50 ms.");
            lean = GameKeys.StepLean(lean, 1f, 0.2f);
            Assert.AreEqual(1f, lean, 1e-5f);
            lean = GameKeys.StepLean(lean, 0f, 0.05f);
            Assert.AreEqual(0.5f, lean, 1e-5f, "Gravity 10 on release.");
        }

        [Test]
        public void KeyCapsReadAsPrinted()
        {
            Assert.AreEqual("LMB", GameKeys.KeyName(KeyCode.Mouse0));
            Assert.AreEqual("3", GameKeys.KeyName(KeyCode.Alpha3));
            Assert.AreEqual("L-SHIFT", GameKeys.KeyName(KeyCode.LeftShift));
            Assert.AreEqual("ENTER", GameKeys.KeyName(KeyCode.Return));
            Assert.AreEqual("F", GameKeys.KeyName(KeyCode.F));
        }
    }
}
