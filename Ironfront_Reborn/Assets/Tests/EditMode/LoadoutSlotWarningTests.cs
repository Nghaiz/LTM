using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// What <c>ServerCombatBridge.ResolveActiveLoadoutSlot</c> says, and when it keeps quiet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The 2026-09-27 playtest logged "the session and the body disagree about the loadout"
    /// forty times in one Island match, every one for weapon 0.</b> Weapon 0 is
    /// <see cref="WeaponIds.NONE"/>: the body was dead, driving, riding a seat that holsters the
    /// carried weapon, or on a turret. None of that is a disagreement, and the line read to the
    /// playtester as a reload bug. These tests pin both halves -- quiet for NONE, loud for a
    /// weapon the loadout never had -- so neither the false alarm nor the real one can come back
    /// unnoticed.
    /// </para>
    /// <para>
    /// <b><c>LogAssert.NoUnexpectedReceived</c> is what makes the quiet cases assertions.</b>
    /// Without it a test that expects no warning passes whether one is logged or not.
    /// </para>
    /// </remarks>
    public sealed class LoadoutSlotWarningTests
    {
        private const ushort Actor = 7;

        private static readonly Regex Disagreement = new Regex("which is in none of its five loadout slots");

        private static ClientSession DeployedSession()
        {
            var session = new ClientSession(connectionId: 3, actorId: Actor);
            session.SetLoadout(WeaponIds.RK44, WeaponIds.BIL_SCALPEL, WeaponIds.FRAG,
                WeaponIds.AMMO_BAG, WeaponIds.NONE);
            return session;
        }

        [Test]
        public void HoldingNothingForgetsTheSlotWithoutCallingItADisagreement()
        {
            ClientSession session = DeployedSession();
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.RK44);
            Assert.IsTrue(session.HasActiveLoadoutSlot);

            // The death, the driver's seat, the turret: the body holds nothing.
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.NONE);

            Assert.IsFalse(session.HasActiveLoadoutSlot,
                "nothing held means no reserve to draw from, so the slot must be forgotten");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AWeaponTheLoadoutNeverHadIsReported()
        {
            ClientSession session = DeployedSession();
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.RK44);

            LogAssert.Expect(LogType.Warning, Disagreement);
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.SL_DEFENDER);

            Assert.IsFalse(session.HasActiveLoadoutSlot);
        }

        [Test]
        public void AForeignWeaponRightAfterADeathIsStillReported()
        {
            // The case the old known-to-unknown edge hid: the corpse held nothing, so the slot
            // was already unknown when the body came back armed with something foreign.
            ClientSession session = DeployedSession();
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.NONE);

            LogAssert.Expect(LogType.Warning, Disagreement);
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.SL_DEFENDER);
        }

        [Test]
        public void HonkingTheHornIsNotADisagreement()
        {
            // The horn is a seat weapon with a wire id and no loadout row (V6-D8): the body holds
            // it while the driver honks, exactly as it holds a turret's gun. v4.1.0 warned on
            // every honk.
            ClientSession session = DeployedSession();
            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.RK44);

            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.CAR_HORN);

            Assert.IsFalse(session.HasActiveLoadoutSlot,
                "a horn has no reserve, so the carried weapon's slot must be forgotten");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ALoadoutWeaponResolvesToItsSlotQuietly()
        {
            ClientSession session = DeployedSession();

            ServerCombatBridge.ResolveActiveLoadoutSlot(session, WeaponIds.BIL_SCALPEL);

            Assert.IsTrue(session.HasActiveLoadoutSlot);
            Assert.AreEqual(1, session.ActiveLoadoutSlot);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
