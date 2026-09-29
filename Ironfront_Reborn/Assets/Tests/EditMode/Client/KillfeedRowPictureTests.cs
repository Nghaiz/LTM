#nullable enable

using System;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A killfeed row draws the weapon's own picture in place of its name, and keeps the chip only
    /// for what the picture cannot say. Owner report 2026-09-29: the text feed was hard to read.
    /// </summary>
    /// <remarks>
    /// Graded on the authored prefab, so a builder that stopped authoring the picture, or a row that
    /// stopped choosing it, goes red here rather than on somebody's screen.
    /// </remarks>
    public sealed class KillfeedRowPictureTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private GameObject? _contents;
        private Func<byte, Sprite>? _previousIcons;
        private Sprite? _rifle;

        [SetUp]
        public void SetUp()
        {
            _previousIcons = NetClientBindings.WeaponIcon;
            _rifle = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 4f), Vector2.one * 0.5f);
            NetClientBindings.WeaponIcon = id => id == WeaponIds.RK44 ? _rifle : null;
            _contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        }

        [TearDown]
        public void TearDown()
        {
            NetClientBindings.WeaponIcon = _previousIcons;
            if (_contents != null) PrefabUtility.UnloadPrefabContents(_contents);
            if (_rifle != null) UnityEngine.Object.DestroyImmediate(_rifle);
        }

        private KillfeedRowView FirstRow()
        {
            KillfeedRowView row = _contents!.GetComponentInChildren<KillfeedRowView>(true);
            Assert.NotNull(row, "the readout carries no killfeed row; run the builder.");

            row.gameObject.SetActive(true);
            typeof(KillfeedRowView)
                .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(row, null);
            return row;
        }

        private static T Part<T>(KillfeedRowView row, string field) where T : class
            => (T)typeof(KillfeedRowView)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(row);

        private static KillfeedLine Line(byte weapon, string label, string rest)
            => new KillfeedLine(
                1, "Minh", TeamId.Team0, "Reyes", TeamId.Team1, label, string.Empty,
                false, false, false, weapon, rest);

        [Test]
        public void AWeaponWithAPicture_IsDrawnAsThePicture_AndTheChipGoes()
        {
            KillfeedRowView row = FirstRow();
            row.Show(Line(WeaponIds.RK44, "RK-44", string.Empty), 0, Color.blue, Color.red);

            Image weapon = Part<Image>(row, "_weapon");
            Assert.IsTrue(weapon.gameObject.activeSelf, "the weapon's picture was not drawn.");
            Assert.AreSame(_rifle, weapon.sprite);
            Assert.IsFalse(Part<GameObject>(row, "_how").activeSelf,
                "the weapon's name was drawn beside its own picture.");
        }

        [Test]
        public void WhatThePictureCannotSay_StaysAChip()
        {
            KillfeedRowView row = FirstRow();
            row.Show(Line(WeaponIds.RK44, "RK-44  ·  MELEE", "MELEE"), 0, Color.blue, Color.red);

            Assert.IsTrue(Part<Image>(row, "_weapon").gameObject.activeSelf);
            Assert.IsTrue(Part<GameObject>(row, "_how").activeSelf);
            Assert.AreEqual("MELEE", Part<Text>(row, "_howText").text);
        }

        [Test]
        public void AWeaponWithNoPicture_IsNamed()
        {
            KillfeedRowView row = FirstRow();
            row.Show(Line(WeaponIds.FRAG, "FRAG", string.Empty), 0, Color.blue, Color.red);

            Assert.IsFalse(Part<Image>(row, "_weapon").gameObject.activeSelf);
            Assert.AreEqual("FRAG", Part<Text>(row, "_howText").text);
        }

        // ------------------------------------------------------------------ owner's report 2026-09-30

        /// <summary>A vehicle's kill is drawn as the vehicle, and the chip keeps only "ROADKILL".</summary>
        [Test]
        public void AVehicleKill_IsDrawnAsTheVehicle_NotAsAWeaponSilhouette()
        {
            KillfeedRowView row = FirstRow();
            var line = new KillfeedLine(
                2, "Minh", TeamId.Team0, "Reyes", TeamId.Team1, "QUAD BIKE  ·  ROADKILL", string.Empty,
                false, false, false, glyph: (int)KillfeedGlyph.QuadBike, restAfterGlyph: "ROADKILL");
            row.Show(line, 0, Color.blue, Color.red);

            Image glyph = Part<Image>(row, "_glyph");
            Assert.IsTrue(glyph.gameObject.activeSelf, "the vehicle's picture is not shown");
            Assert.AreSame(HudSprites.Glyph(KillfeedGlyph.QuadBike), glyph.sprite);
            Assert.IsFalse(Part<Image>(row, "_weapon").gameObject.activeSelf);
            Assert.AreEqual("ROADKILL", Part<Text>(row, "_howText").text);

            // A drawn picture is white with clear corners; the silhouette shader would fill them.
            Assert.IsTrue(glyph.material == null || glyph.material.shader.name != "Ironfront/UI/Silhouette");
        }

        [Test]
        public void AKillThatEarnedABadge_ShowsIt_AndOneThatDidNot_DoesNot()
        {
            KillfeedRowView row = FirstRow();
            var badged = new KillfeedLine(
                3, "Minh", TeamId.Team0, "Reyes", TeamId.Team1, "RK-44", string.Empty,
                true, false, false, WeaponIds.RK44, string.Empty,
                badge: "TRIPLE KILL", badgeTone: (int)KillfeedTone.MultiKill);
            row.Show(badged, 0, Color.blue, Color.red);

            Assert.IsTrue(Part<GameObject>(row, "_badge").activeSelf);
            Assert.AreEqual("TRIPLE KILL", Part<Text>(row, "_badgeText").text);
            Assert.IsTrue(Part<Image>(row, "_headshot").gameObject.activeSelf);

            row.Show(Line(WeaponIds.RK44, "RK-44", string.Empty), 0, Color.blue, Color.red);
            Assert.IsFalse(Part<GameObject>(row, "_badge").activeSelf);
        }

        [Test]
        public void ALongShot_SaysHowFarItFlew()
        {
            KillfeedRowView row = FirstRow();
            var line = new KillfeedLine(
                4, "Minh", TeamId.Team0, "Reyes", TeamId.Team1, "RECON LRR", string.Empty,
                false, false, false, WeaponIds.RK44, string.Empty, distance: "312 m");
            row.Show(line, 0, Color.blue, Color.red);

            Assert.IsTrue(Part<Image>(row, "_longShot").gameObject.activeSelf);
            Assert.AreEqual("312 m", Part<Text>(row, "_distance").text);
        }

        [Test]
        public void ADeathNobodyScored_LeadsWithItsCause_AndNamesNoKiller()
        {
            KillfeedRowView row = FirstRow();
            var line = new KillfeedLine(
                5, string.Empty, TeamId.None, "Reyes", TeamId.Team1, string.Empty, "drowned",
                false, false, false, glyph: (int)KillfeedGlyph.Drowned);
            row.Show(line, 0, Color.grey, Color.red);

            Assert.IsTrue(Part<Image>(row, "_lead").gameObject.activeSelf);
            Assert.AreSame(HudSprites.Glyph(KillfeedGlyph.Drowned), Part<Image>(row, "_lead").sprite);
            Assert.IsFalse(Part<Text>(row, "_killer").gameObject.activeSelf);
            Assert.AreEqual("drowned", Part<Text>(row, "_sentence").text);
            Assert.IsFalse(Part<GameObject>(row, "_how").activeSelf);
        }

        [Test]
        public void AMatchEvent_ReadsSubjectVerbObject()
        {
            KillfeedRowView row = FirstRow();
            var line = new KillfeedLine(
                6, "BLUE TEAM", TeamId.Team0, "FORTRESS", TeamId.Team0, string.Empty, string.Empty,
                false, false, false, glyph: (int)KillfeedGlyph.Flag, verb: "captured", isEvent: true);
            row.Show(line, 0, Color.blue, Color.blue);

            Assert.AreEqual("BLUE TEAM", Part<Text>(row, "_killer").text);
            Assert.IsTrue(Part<Text>(row, "_verb").gameObject.activeSelf);
            Assert.AreEqual("captured", Part<Text>(row, "_verb").text);
            Assert.AreEqual("FORTRESS", Part<Text>(row, "_victim").text);
            Assert.IsTrue(Part<Image>(row, "_lead").gameObject.activeSelf);
            Assert.IsFalse(Part<GameObject>(row, "_how").activeSelf);
            Assert.IsFalse(Part<GameObject>(row, "_badge").activeSelf);
        }

        /// <summary>
        /// The picture is drawn through the silhouette material: the loadout's weapon art is white on
        /// black with no alpha, and drawn plainly every gun sat in a black box.
        /// </summary>
        [Test]
        public void ThePictureIsDrawnAsASilhouette()
        {
            Image weapon = Part<Image>(FirstRow(), "_weapon");

            Assert.NotNull(weapon.material);
            Assert.AreEqual("Ironfront/UI/Silhouette", weapon.material.shader.name);
        }
    }
}
