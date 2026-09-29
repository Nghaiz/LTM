#nullable enable

using System;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The holo-frame name plate the owner chose on 2026-09-29: the name as the player wrote it,
    /// and every icon saying the one thing it is there for.
    /// </summary>
    /// <remarks>
    /// Graded on the authored prefab, as <see cref="KillfeedRowPictureTests"/> is, so a builder
    /// that stopped authoring a part or a view that stopped choosing it goes red here rather than
    /// over somebody's head.
    /// </remarks>
    public sealed class NameplateHoloTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private GameObject? _contents;
        private Func<byte, Sprite>? _previousIcons;
        private Sprite? _rifle;
        private NameplateView? _view;

        [SetUp]
        public void SetUp()
        {
            _previousIcons = NetClientBindings.WeaponIcon;
            _rifle = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 1f), Vector2.one * 0.5f);
            NetClientBindings.WeaponIcon = id => id == WeaponIds.RK44 ? _rifle : null;

            _contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            _view = _contents.GetComponentInChildren<NameplateView>(true);
            Assert.NotNull(_view, "the readout carries no name plate; run the builder.");

            _view!.Initialize();
            Assert.IsTrue(_view.IsComplete, "the authored plate is missing a part; run the builder.");
        }

        [TearDown]
        public void TearDown()
        {
            NetClientBindings.WeaponIcon = _previousIcons;
            if (_contents != null) PrefabUtility.UnloadPrefabContents(_contents);
            if (_rifle != null) UnityEngine.Object.DestroyImmediate(_rifle);
        }

        private static Nameplate Plate(
            string name, bool teammate = true, byte weapon = WeaponIds.RK44,
            bool seated = false, bool water = false, bool leader = false, float metres = 24.4f)
            => new Nameplate(
                7, 100f, 100f, 1f, 1f, name, TeamId.Team0, 1f, teammate, metres, weapon, seated, water, leader);

        private void Show(Nameplate plate) => _view!.Show(in plate, Vector2.zero, Color.blue, 0.02f);

        private T Part<T>(string field) where T : class
            => (T)typeof(NameplateView)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_view);

        /// <summary>Owner, 2026-09-29: names are not put in capitals. Vietnamese marks survive.</summary>
        [Test]
        public void TheName_IsDrawnAsWritten()
        {
            Show(Plate("Nguyễn Văn Khoa"));

            Assert.AreEqual("Nguyễn Văn Khoa", Part<Text>("_name").text);
        }

        /// <summary>Friend and foe differ in shape, not only in a colour some players cannot tell apart.</summary>
        [Test]
        public void AFriendAndAFoe_WearDifferentEmblems()
        {
            Show(Plate("Minh", teammate: true));
            Assert.AreSame(HudSprites.Shield(), Part<Image>("_emblem").sprite);

            Show(Plate("Minh", teammate: false));
            Assert.AreSame(HudSprites.Hostile(), Part<Image>("_emblem").sprite);
        }

        [Test]
        public void TheWeaponInHand_IsDrawn_AndASeatSwapsItForAWheel()
        {
            Show(Plate("Minh"));
            Assert.IsTrue(Part<Image>("_weapon").gameObject.activeSelf, "the weapon in hand was not drawn.");
            Assert.AreSame(_rifle, Part<Image>("_weapon").sprite);
            Assert.IsFalse(Part<Image>("_state").gameObject.activeSelf);

            Show(Plate("Minh", seated: true));
            Assert.IsFalse(Part<Image>("_weapon").gameObject.activeSelf, "a seated player's weapon was drawn.");
            Assert.IsTrue(Part<Image>("_state").gameObject.activeSelf);
            Assert.AreSame(HudSprites.Wheel(), Part<Image>("_state").sprite);
        }

        [Test]
        public void ASwimmer_IsMarkedWithAWave()
        {
            Show(Plate("Minh", water: true));

            Assert.IsTrue(Part<Image>("_state").gameObject.activeSelf);
            Assert.AreSame(HudSprites.Wave(), Part<Image>("_state").sprite);
        }

        [Test]
        public void AWeaponWithNoPicture_LeavesNoEmptyIcon()
        {
            Show(Plate("Minh", weapon: WeaponIds.FRAG));

            Assert.IsFalse(Part<Image>("_weapon").gameObject.activeSelf);
        }

        [Test]
        public void OnlyTheLeader_WearsTheStar()
        {
            Show(Plate("Minh", leader: false));
            Assert.IsFalse(Part<Image>("_star").gameObject.activeSelf);

            Show(Plate("Minh", leader: true));
            Assert.IsTrue(Part<Image>("_star").gameObject.activeSelf);
        }

        [Test]
        public void TheDistance_ReadsInWholeMetres()
        {
            Show(Plate("Minh", metres: 24.4f));

            Assert.AreEqual("24 m", Part<Text>("_distance").text);
        }
    }
}
