using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ironfront.Net.Unity.Client.Menu;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using static Ironfront.Net.Unity.EditorTools.IronfrontUiKit;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Brings the legacy in-match canvases on <c>Ingame UI Container.prefab</c> into the menu's
    /// design language, and imports the textures they draw so they stay sharp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Presentation, laid out after the HUD pack</b> (<c>ironfront-reborn-ui-pack-HUD</c>: its
    /// deploy screen and its in-game HUD). The pause menu, the status readout, the loadout screen
    /// and the score bar are Ravenfield's, driven by <c>IngameMenuUi</c>, <c>IngameUi</c>,
    /// <c>LoadoutUi</c> and <c>ScoreUi</c>. Every object those scripts reach — by field, by
    /// <c>Find</c> name, or by <c>GetComponentInChildren&lt;Text&gt;</c> order — keeps its
    /// object, its name and its component, and every button keeps its persistent onClick calls.
    /// Objects reached only through serialized fields may move to a new parent. The one
    /// behaviour added is <c>IngameUi.healthBar</c>, the health meter.
    /// </para>
    /// <para>
    /// <b>Why it was needed.</b> The menu had been rebuilt on the pack while the match still
    /// drew Ravenfield's greys: flat white pause buttons, Roboto Light hairlines over the world,
    /// block-compressed icons sampled from a single mip, and four canvases at a constant pixel
    /// size, so the HUD shrank on every screen larger than 1080p while the menu scaled.
    /// </para>
    /// <para>
    /// <b>A command, like <c>BuildMenuCanvas</c> and <c>BuildMatchHud</c>, and idempotent.</b>
    /// A second run finds the surfaces and children the first one made and restyles them in
    /// place. The <c>Match Readout</c> canvas is <c>BuildMatchHud</c>'s and is not touched here.
    /// </para>
    /// </remarks>
    public static class RestyleIngameUi
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";
        private const string ManagersPath = "Assets/Resources/_Managers.prefab";
        private const string ReportFile = "restyle-ingame-ui.txt";

        /// <summary>The HUD's distance from the screen edge; the Match Readout's too.</summary>
        private const float Margin = 24f;

        /// <summary>Ravenfield's surface grey, which the loadout screen is built from.</summary>
        private static readonly Color LegacyGrey = Hex("191717");

        /// <summary><c>secondary.svg</c>'s fill: the navy every surface in the menu is.</summary>
        private static readonly Color Navy = Hex("0A1927");

        /// <summary>The HUD pack's in-match glass, <c>#061A29</c>.</summary>
        private static readonly Color HudGlass = Hex("061A29");

        /// <summary>The HUD pack's small captions over the world: <c>HEALTH</c>, <c>WEAPON</c>.</summary>
        private static readonly Color HudCaption = Hex("B6D1DC");

        // ---- the deploy screen's grid, in reference pixels from the canvas centre.
        //
        // The HUD pack's `.deploy-shell`: a heading, then the equipment and the deployment zone
        // side by side. The heading starts 200px down so it clears the score bar and its phase
        // row, which stay up over this screen; the two surfaces share a top and a bottom.

        /// <summary>Half the width of everything on the deploy screen.</summary>
        private const float DeployHalfWidth = 649f;

        /// <summary>The top of the heading's kicker line.</summary>
        private const float HeadingTop = 340f;

        /// <summary>The top edge of the surfaces.</summary>
        private const float SurfacesTop = 214f;

        /// <summary>A surface's padding round its content, and its header's height.</summary>
        private const float SurfacePad = 16f;
        private const float SurfaceHead = 52f;

        /// <summary>
        /// The band under a surface's content: the equipment's hint row, and the map's DEPLOY.
        /// </summary>
        private const float SurfaceFoot = 92f;

        /// <summary>
        /// The equipment grid's width. Its height follows from the Loadout Container's own aspect
        /// ratio, which its slots are anchored in, and the map is made that tall and square.
        /// </summary>
        private const float KitWidth = 760f;

        /// <summary>The armory list's width while a slot is being chosen.</summary>
        private const float ArmoryListWidth = 552f;

        /// <summary>
        /// The HUD's smooth art — gradients and soft masks — which keeps its compression. Every
        /// other texture the canvases draw is line art.
        /// </summary>
        private static readonly HashSet<string> SmoothArt = new HashSet<string>
        {
            "damage_vignette", "damage_indicator", "damage_indicator_mask", "background_gradient",
            "minimap_bg",
        };

        [MenuItem("Ironfront/Net/Restyle in-match HUD")]
        public static void RunFromMenu() => Execute(exitOnFailure: false);

        /// <summary>The <c>-executeMethod</c> entry point.</summary>
        public static void Run() => Execute(exitOnFailure: Application.isBatchMode);

        private static void Execute(bool exitOnFailure)
        {
            var log = new StringBuilder();
            bool ok;

            try
            {
                ok = Build(log);
            }
            catch (System.Exception ex)
            {
                log.AppendLine("FAILED: " + ex);
                ok = false;
            }

            string report = log.ToString();
            File.WriteAllText(ReportFile, report);

            if (ok) Debug.Log("[restyle-ingame-ui]\n" + report);
            else Debug.LogError("[restyle-ingame-ui] FAILED\n" + report);

            if (!ok && exitOnFailure) EditorApplication.Exit(1);
        }

        private static bool Build(StringBuilder log)
        {
            SharpenTextures(log);

            GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (contents == null)
            {
                log.AppendLine("FAILED: could not load " + PrefabPath + ". Has it moved?");
                return false;
            }

            List<string> arsenalPrefabs;
            try
            {
                Transform root = contents.transform;
                foreach (string canvas in new[] { "Menu UI", "Ingame UI", "Loadout UI Canvas", "Minimap UI" })
                    ScaleWithScreen(Child(root, canvas), log);

                RestylePauseMenu(Child(root, "Menu UI"), log);
                RestyleStatusPanel(Child(root, "Ingame UI"), log);
                arsenalPrefabs = RestyleLoadout(Child(root, "Loadout UI Canvas"), log);
                RestyleScoreBar(Child(root, "Score UI Canvas"), log);

                PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
                log.AppendLine("saved: " + PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            // The arsenal's item buttons are instantiated from their own prefabs at runtime, so the
            // loadout screen is only one design if those are restyled with it.
            foreach (string path in arsenalPrefabs)
            {
                GameObject button = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Recolour(button.transform);
                    Cardify(button.transform);
                    PrefabUtility.SaveAsPrefabAsset(button, path);
                    log.AppendLine("saved: " + path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(button);
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>
        /// Imports every texture the in-match canvases draw — their own icons and every weapon's
        /// picture — with <see cref="IronfrontUiKit.SharpenUiTexture"/>.
        /// </summary>
        /// <remarks>
        /// The icons were DXT-compressed and sampled bilinear from one mip: the health cross, the
        /// resupply badges, the hitmarker and the capture flag drawn at a fifth of their size came
        /// out soft and blocky. The weapon pictures are the loadout's and the status panel's; they
        /// are listed by <c>WeaponManager</c> in <c>_Managers.prefab</c>, read here from the asset
        /// database's dependency lists so no weapon prefab has to be loaded to find them.
        /// </remarks>
        private static void SharpenTextures(StringBuilder log)
        {
            var paths = new SortedSet<string>(System.StringComparer.Ordinal);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            foreach (Graphic graphic in prefab.GetComponentsInChildren<Graphic>(true))
            {
                Texture texture = graphic is RawImage raw ? raw.texture
                    : graphic is Image image && image.sprite != null ? image.sprite.texture : null;
                string path = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/")) paths.Add(path);
            }

            // The weapon pictures are Sprite assets of their own (Assets/Sprite/*.asset) cut from
            // textures in Assets/Texture2D, so the texture is one dependency further on.
            foreach (string dependency in AssetDatabase.GetDependencies(ManagersPath, recursive: false))
            {
                if (AssetDatabase.GetMainAssetTypeAtPath(dependency) != typeof(Sprite)) continue;
                foreach (string texture in AssetDatabase.GetDependencies(dependency, recursive: false))
                    if (AssetImporter.GetAtPath(texture) is TextureImporter) paths.Add(texture);
            }

            int reimported = 0;
            foreach (string path in paths)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                bool smooth = SmoothArt.Contains(Path.GetFileNameWithoutExtension(path));
                TextureImporterCompression compression = smooth
                    ? importer.textureCompression
                    : TextureImporterCompression.Uncompressed;

                if (importer.GetPlatformTextureSettings("Standalone").overridden)
                    log.AppendLine("  note: " + path + " has a Standalone override, which still decides its format there.");

                if (!SharpenUiTexture(importer, compression)) continue;

                importer.SaveAndReimport();
                reimported++;
            }

            log.AppendLine("textures: " + paths.Count + " drawn by the in-match canvases, "
                           + reimported + " reimported sharper.");
        }

        // ------------------------------------------------------------------ canvases

        /// <summary>
        /// The menu's scaling: 1920×1080 reference, width and height weighted equally.
        /// </summary>
        /// <remarks>
        /// These four were Constant Pixel Size, so the HUD was laid out for 1080p and simply got
        /// smaller on every larger screen — a quarter of its size at 4K — while the menu and the
        /// Match Readout scaled. At 1080p nothing moves.
        /// </remarks>
        private static void ScaleWithScreen(Transform canvas, StringBuilder log)
        {
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            log.AppendLine("scaled with the screen: " + canvas.name);
        }

        /// <summary>
        /// The Esc menu: the menu's operations panel and its buttons over a dimmed world.
        /// </summary>
        /// <remarks>
        /// It was four 180×40 white buttons captioned "&lt; RESUME &gt;" on a 200px white card.
        /// The buttons keep their objects, and so their persistent calls into
        /// <c>IngameMenuUi</c>; only their faces, captions and places change.
        /// </remarks>
        private static void RestylePauseMenu(Transform menu, StringBuilder log)
        {
            menu.GetComponent<Canvas>().pixelPerfect = true;

            Image dim = Ensure<Image>(menu, "Dim");
            Stretch(dim.rectTransform);
            dim.color = new Color(3f / 255f, 9f / 255f, 19f / 255f, 0.55f);
            dim.raycastTarget = true;
            dim.transform.SetAsFirstSibling();

            // "Image" as authored, "Panel" after the first run.
            Transform panelTransform = menu.Find("Panel");
            if (panelTransform == null) panelTransform = Child(menu, "Image");
            panelTransform.name = "Panel";
            AngularPanel panel = AsAngular(panelTransform.gameObject);
            Centre(panel.rectTransform, Vector2.zero, new Vector2(460f, 460f));
            StyleOperationsPanel(panel);
            panel.raycastTarget = true;

            Text kicker = EnsureLabel(panelTransform, "Kicker", "IRONFRONT REBORN", 11, bold: true);
            kicker.color = CyanSoft;
            kicker.alignment = TextAnchor.MiddleCenter;
            Centre(kicker.rectTransform, new Vector2(0f, 192f), new Vector2(380f, 20f));

            Text heading = EnsureLabel(panelTransform, "Heading", "PAUSED", 34, bold: true);
            heading.alignment = TextAnchor.MiddleCenter;
            Centre(heading.rectTransform, new Vector2(0f, 154f), new Vector2(380f, 48f));

            MenuRow(panelTransform, "Resume Button", "RESUME", "primary", "icons/chevron.png", 64f);
            MenuRow(panelTransform, "Options Button", "OPTIONS", "secondary", "icons/settings.png", -12f);
            MenuRow(panelTransform, "Menu Button", "QUIT TO MENU", "danger", "icons/leave.png", -88f);
            MenuRow(panelTransform, "Quit Button", "EXIT GAME", "secondary", "icons/power.png", -164f);

            log.AppendLine("pause menu: operations panel, four menu rows, dimmed world.");
        }

        /// <summary>One <c>.menu-button</c> row, on an existing legacy button.</summary>
        private static void MenuRow(Transform panel, string name, string caption, string kind,
            string icon, float y)
        {
            const float Width = 380f;
            const float Height = 64f;

            Transform row = Child(panel, name);
            Button button = row.GetComponent<Button>();
            AngularPanel face = AsAngular(row.gameObject);
            Centre(face.rectTransform, new Vector2(0f, y), new Vector2(Width, Height));
            StyleButtonFace(face, kind, Height);
            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = ButtonColours(button.colors, kind);

            // `.menu-button` reserves 68px on the left for a 28px glyph, as the title screen's do.
            Image glyph = Ensure<Image>(row, "Icon");
            glyph.sprite = IronfrontRebornUiAssetCatalog.Sprite(icon);
            glyph.preserveAspect = true;
            glyph.raycastTarget = false;
            Centre(glyph.rectTransform, new Vector2(-Width * 0.5f + 23f + Height * 0.21f, 0f),
                new Vector2(Height * 0.38f, Height * 0.38f));

            Text text = Child(row, "Text").GetComponent<Text>();
            int size = CaptionSize(Height);
            text.text = caption;
            text.font = BoldFont();
            text.fontSize = size;
            text.fontStyle = FontStyle.Bold;
            text.color = CaptionInk(kind);
            text.alignment = TextAnchor.MiddleCenter;
            text.resizeTextForBestFit = false;
            Centre(text.rectTransform, new Vector2(16f, 0f), new Vector2(Width - 72f, Height));
        }

        /// <summary>
        /// The bottom of the screen as the HUD pack draws it: health at the left, the weapon at
        /// the right, each on a strip of glass that fades toward the middle of the screen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why two panels.</b> The health, the weapon and the ammunition shared one box in the
        /// bottom-left corner, so the figure a player checks most in a fight — the magazine —
        /// sat furthest from where the pack's <c>.weapon-hud</c> puts it, and nothing said which
        /// number was which. The objects <c>IngameUi</c> writes keep their names and components;
        /// the weapon image and the two ammunition texts move into a <c>Weapon Panel</c> of
        /// their own, which is safe because <c>IngameUi</c> reaches every one of them through a
        /// serialized reference rather than a path.
        /// </para>
        /// <para>
        /// <b>The health meter is the one new moving part.</b> <c>IngameUi.healthBar</c> is sized
        /// by its right anchor on every <c>SetHealth</c>, the way the vehicle bar already is.
        /// </para>
        /// <para>
        /// The resupply badges stay anchored to fractions of a panel's top edge — health over the
        /// health, ammunition over the weapon — so each still rises out of the number it refills.
        /// </para>
        /// </remarks>
        private static void RestyleStatusPanel(Transform ingame, StringBuilder log)
        {
            ingame.GetComponent<Canvas>().pixelPerfect = true;

            // ---- `.vitals`
            Transform vitals = Child(ingame, "Panel");
            AngularPanel vitalsFace = AsAngular(vitals.gameObject);
            HudStrip(vitalsFace, AngularEdge.Left, Hex("79CFFF"), 90f);
            Pin(vitalsFace.rectTransform, Vector2.zero, Vector2.zero, new Vector2(Margin, Margin),
                new Vector2(270f, 84f));

            Image cross = Child(vitals, "Health Image").GetComponent<Image>();
            cross.color = Hex("8FEBC9");
            cross.preserveAspect = true;
            Pin(cross.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(20f, 0f),
                new Vector2(28f, 28f));

            Text caption = EnsureLabel(vitals, "Health Caption", "HEALTH", 11, bold: true);
            caption.color = HudCaption;
            caption.alignment = TextAnchor.MiddleLeft;
            Pin(caption.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(64f, 26f),
                new Vector2(120f, 14f));

            Text health = Child(cross.transform, "Health Text").GetComponent<Text>();
            Style(health, BoldFont(), 34, Ink, TextAnchor.MiddleLeft);
            RectTransform healthRect = health.rectTransform;
            healthRect.anchorMin = new Vector2(1f, 0.5f);
            healthRect.anchorMax = new Vector2(1f, 0.5f);
            healthRect.pivot = new Vector2(0f, 0.5f);
            healthRect.anchoredPosition = new Vector2(16f, 1f);
            healthRect.sizeDelta = new Vector2(96f, 40f);

            AngularPanel track = EnsureAngular(vitals, "Health Meter");
            track.color = Hex("274759");
            track.Configure(0f, AngularEdge.None, 0f, Color.clear);
            track.raycastTarget = false;
            Pin(track.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(64f, -27f),
                new Vector2(180f, 4f));

            AngularPanel fill = EnsureAngular(track.transform, "Fill");
            fill.color = Green;
            fill.Configure(0f, AngularEdge.None, 0f, Color.clear);
            fill.raycastTarget = false;
            RectTransform fillRect = fill.rectTransform;
            Stretch(fillRect);
            fillRect.pivot = new Vector2(0f, 0.5f);

            MonoBehaviour ingameUi = ingame.GetComponents<MonoBehaviour>()
                .First(behaviour => behaviour != null && behaviour.GetType().Name == "IngameUi");
            var so = new SerializedObject(ingameUi);
            SerializedProperty healthBar = so.FindProperty("healthBar");
            if (healthBar == null)
                throw new System.InvalidOperationException(
                    "IngameUi has no serialized 'healthBar'; the restyle and the component have drifted.");
            healthBar.objectReferenceValue = fillRect;
            so.ApplyModifiedPropertiesWithoutUndo();

            // Just above the strip: a vehicle's health is a second bar, not part of the player's.
            RectTransform vehicleBar = (RectTransform)Child(vitals, "Vehicle Health Background");
            vehicleBar.anchorMin = new Vector2(0f, 1f);
            vehicleBar.anchorMax = new Vector2(0f, 1f);
            vehicleBar.pivot = new Vector2(0f, 0f);
            vehicleBar.anchoredPosition = new Vector2(0f, 6f);
            vehicleBar.sizeDelta = new Vector2(270f, 5f);

            // ---- `.weapon-hud`
            AngularPanel weaponFace = Ensure<AngularPanel>(ingame, "Weapon Panel");
            HudStrip(weaponFace, AngularEdge.Right, Hex("FF9B43"), 270f);
            Pin(weaponFace.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-Margin, Margin),
                new Vector2(340f, 84f));
            // Drawn where the status panel is: under the hitmarker and the damage overlays.
            weaponFace.transform.SetSiblingIndex(vitals.GetSiblingIndex() + 1);
            Transform weaponPanel = weaponFace.transform;

            foreach (string moved in new[] { "Weapon Image", "Current Ammo Text", "Spare Ammo Text", "Resupply Ammo" })
                Adopt(weaponPanel, vitals, moved);

            Text weaponCaption = EnsureLabel(weaponPanel, "Weapon Caption", "WEAPON", 11, bold: true);
            weaponCaption.color = HudCaption;
            weaponCaption.alignment = TextAnchor.MiddleRight;
            Pin(weaponCaption.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-20f, 26f),
                new Vector2(200f, 14f));

            Image weapon = Child(weaponPanel, "Weapon Image").GetComponent<Image>();
            weapon.color = Ink;
            weapon.preserveAspect = true;
            Pin(weapon.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-160f, -6f),
                new Vector2(124f, 44f));

            Text magazine = Child(weaponPanel, "Current Ammo Text").GetComponent<Text>();
            Style(magazine, BoldFont(), 40, Ink, TextAnchor.MiddleRight);
            Pin(magazine.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-84f, -6f),
                new Vector2(70f, 48f));

            Text reserve = Child(weaponPanel, "Spare Ammo Text").GetComponent<Text>();
            Style(reserve, RegularFont(), 19, Hex("BDD1DA"), TextAnchor.MiddleLeft);
            Pin(reserve.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-18f, -13f),
                new Vector2(64f, 28f));

            RectTransform ammoBadge = (RectTransform)Child(weaponPanel, "Resupply Ammo");
            ammoBadge.anchorMin = new Vector2(0.45f, 1f);
            ammoBadge.anchorMax = new Vector2(0.8f, 1f);

            // Retired: the rule that divided the one shared box.
            Transform divider = vitals.Find("Divider");
            if (divider != null) Object.DestroyImmediate(divider.gameObject);

            // Below the Match Readout's team chip (24 in, 62 tall), which took the top-left corner
            // it used to share with this ring.
            ((RectTransform)Child(ingame, "Flag Capture Indicator Edge")).anchoredPosition =
                new Vector2(Margin, -(Margin + 62f + 12f));

            log.AppendLine("status: health strip bottom-left with a meter, weapon strip bottom-right; capture ring below the team chip.");
        }

        /// <summary>
        /// <c>.vitals</c> / <c>.weapon-hud</c>: dark glass fading toward the screen's middle, and a
        /// 3px accent on the outer edge.
        /// </summary>
        /// <param name="fadeTowards">The CSS gradient angle: 90 fades to the right, 270 to the left.</param>
        private static void HudStrip(AngularPanel face, AngularEdge edge, Color accent, float fadeTowards)
        {
            face.color = WithAlpha(HudGlass, 0.88f);
            face.SetGradient(WithAlpha(HudGlass, 0.05f), fadeTowards);
            face.Configure(0f, edge, 3f, accent);
            face.raycastTarget = false;
        }

        /// <summary>Moves <paramref name="name"/> under <paramref name="parent"/>, wherever it is now.</summary>
        /// <remarks>Idempotent: a second run finds it already moved.</remarks>
        private static void Adopt(Transform parent, Transform formerParent, string name)
        {
            Transform found = parent.Find(name);
            if (found == null) found = Child(formerParent, name);
            found.SetParent(parent, worldPositionStays: false);
        }

        /// <summary>
        /// The loadout screen as the HUD pack's deploy screen: a heading, the equipment and the
        /// deployment zone on two surfaces, and the orange DEPLOY.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the screen a player deploys from</b> — the first spawn of every networked
        /// life goes through it (<c>OpenInitialNetworkLoadout</c>) — and it was Ravenfield's grey
        /// layout recoloured: bare slots with a number in the corner, and a map floating on its
        /// own with DEPLOY under it.
        /// </para>
        /// <para>
        /// <b>Every object <c>LoadoutUi</c> and <c>MinimapUi</c> reach keeps its name, its place in
        /// its parent and its component.</b> They find a slot's picture with <c>Find("Image")</c>
        /// and its name with <c>GetComponentInChildren&lt;Text&gt;</c>, so the name label stays a
        /// slot's first text; the armory is measured off <c>gearContainer.GetChild(0)</c>, so
        /// nothing is added ahead of it. What is new is added after, and is decoration: surfaces,
        /// captions, rules.
        /// </para>
        /// <para>
        /// <b>The text says only what is true in both modes.</b> The pack's spawn summary tells a
        /// player to click a spawn point; a networked client cannot pick one yet (the request
        /// carries <c>NoSpawnPointPreference</c>), so no caption here asks for it.
        /// </para>
        /// </remarks>
        /// <returns>The arsenal button prefabs <c>LoadoutUi</c> instantiates.</returns>
        private static List<string> RestyleLoadout(Transform loadout, StringBuilder log)
        {
            Recolour(loadout);

            Transform background = Child(loadout, "Background Panel");
            background.GetComponent<RawImage>().color = new Color(3f / 255f, 16f / 255f, 28f / 255f, 0.88f);

            DeployHeading(background);

            // ---- 01 / EQUIPMENT
            RectTransform kit = (RectTransform)Child(background, "Loadout Container");
            float kitHeight = KitWidth / kit.GetComponent<AspectRatioFitter>().aspectRatio;
            float contentTop = SurfacesTop - SurfaceHead;
            TopLeft(kit, new Vector2(-DeployHalfWidth + SurfacePad, contentTop), new Vector2(KitWidth, kitHeight));
            DeploySurface(kit, "01 / EQUIPMENT", "YOUR KIT  <color=#FF8A2E>●</color>",
                "CLICK A SLOT TO CHANGE ITS EQUIPMENT", "<color=#3BDB83>●</color>  KIT READY");

            foreach ((string slot, string caption) in new[]
                     {
                         ("Primary Button", "01 / PRIMARY"),
                         ("Secondary Button", "02 / SECONDARY"),
                         ("Gear 1 Button", "03 / GEAR"),
                         ("Gear 2 Button", "04 / GEAR"),
                         ("Gear 3 Button", "05 / GEAR"),
                         ("Large Gear 2 Button", "04 / GEAR"),
                     })
                SlotCard(Child(kit, slot), caption);

            // ---- 02 / DEPLOYMENT ZONE: the map, as tall as the equipment and square.
            RectTransform map = (RectTransform)Child(background, "Minimap Container");
            TopLeft(map, new Vector2(DeployHalfWidth - SurfacePad - kitHeight, contentTop),
                new Vector2(kitHeight, kitHeight));

            // Hidden behind the map's own frame; the surface and the frame draw the backdrop now.
            map.GetComponent<RawImage>().enabled = false;
            DeploySurface(map, "02 / DEPLOYMENT ZONE", "TACTICAL MAP", null, null);

            AngularPanel frame = EnsureAngular(map, "Map Frame");
            frame.transform.SetSiblingIndex(1);
            frame.color = Hex("0B1B26");
            frame.Configure(0f, AngularEdge.All, 1f, WithAlpha(Hex("90AAB3"), 0.45f));
            frame.raycastTarget = false;
            Stretch(frame.rectTransform);
            frame.rectTransform.offsetMin = new Vector2(-1f, -1f);
            frame.rectTransform.offsetMax = new Vector2(1f, 1f);

            DeployAction(Child(map, "Deploy Button"));

            // ---- the armory, while a slot is being chosen: the list where the equipment was, and
            // the selected item where the map was, both on the same top and bottom.
            RectTransform list = (RectTransform)Child(background, "Arsenal Scroll Rect");
            float surfaceHeight = SurfaceHead + kitHeight + SurfaceFoot;
            TopLeft(list, new Vector2(-DeployHalfWidth, SurfacesTop), new Vector2(ArmoryListWidth, surfaceHeight));

            // The list scrolled with no viewport, so an item scrolled past the rect was drawn over
            // the heading and off the bottom of the screen. The rect is its viewport.
            if (list.GetComponent<RectMask2D>() == null) list.gameObject.AddComponent<RectMask2D>();
            Cardify(list);
            ClippablePictureMaterial(kit, log);

            RectTransform inspector = (RectTransform)Child(background, "Arsenal Inspector Container");
            float inspectorLeft = -DeployHalfWidth + ArmoryListWidth + 20f + SurfacePad;
            float inspectorHeight = kitHeight + SurfaceFoot - SurfacePad;
            TopLeft(inspector, new Vector2(inspectorLeft, contentTop),
                new Vector2(DeployHalfWidth - SurfacePad - inspectorLeft, inspectorHeight));
            AngularPanel inspectorSurface = DeploySurface(inspector, "ARMORY", "SELECTED ITEM", null, null);
            inspectorSurface.rectTransform.offsetMin = new Vector2(-SurfacePad, -SurfacePad);

            Image picture = Child(inspector, "Inspector Image").GetComponent<Image>();
            picture.preserveAspect = true;
            ActionButton(Child(inspector, "Select Button"), "SELECT", "secondary");

            // Centred under the list. LoadoutUi bobs it by its anchored position every frame, so
            // it is placed by its anchors, as fractions of the 1920×1080 reference.
            Transform indicator = Child(background, "Scroll Indicator");
            RectTransform indicatorRect = (RectTransform)indicator;
            const float IndicatorWidth = 0.03f;
            float indicatorX = 0.5f + (-DeployHalfWidth + ArmoryListWidth * 0.5f) / 1920f - IndicatorWidth * 0.5f;
            float indicatorY = 0.5f + (SurfacesTop - surfaceHeight - 64f) / 1080f;
            indicatorRect.anchorMin = new Vector2(indicatorX, indicatorY);
            indicatorRect.anchorMax = new Vector2(indicatorX + IndicatorWidth, indicatorY);
            indicator.GetComponent<RawImage>().color = CyanSoft;
            Child(indicator, "Text").GetComponent<Text>().color = CyanSoft;

            MonoBehaviour loadoutUi = loadout.GetComponents<MonoBehaviour>()
                .First(behaviour => behaviour != null && behaviour.GetType().Name == "LoadoutUi");
            var so = new SerializedObject(loadoutUi);
            var prefabs = new List<string>();
            foreach (string field in new[] { "arsenalSmallButtonPrefab", "arsenalLargeButtonPrefab" })
            {
                string path = AssetDatabase.GetAssetPath(so.FindProperty(field).objectReferenceValue);
                if (!string.IsNullOrEmpty(path) && !prefabs.Contains(path)) prefabs.Add(path);
            }

            log.AppendLine("loadout: deploy heading, equipment and deployment-zone surfaces, cards, the orange DEPLOY.");
            return prefabs;
        }

        /// <summary><c>.deploy-heading</c>: kicker, title and a line of instruction.</summary>
        private static void DeployHeading(Transform background)
        {
            Text kicker = EnsureLabel(background, "Deploy Kicker", "IRONFRONT REBORN  •  DEPLOYMENT", 13, bold: true);
            kicker.color = Hex("8FC8E4");
            kicker.alignment = TextAnchor.MiddleLeft;
            TopLeft(kicker.rectTransform, new Vector2(-DeployHalfWidth, HeadingTop), new Vector2(900f, 18f));

            Text title = EnsureLabel(background, "Deploy Title", "CHOOSE YOUR LOADOUT", 46, bold: true);
            title.alignment = TextAnchor.MiddleLeft;
            EnsureShadow(title);
            TopLeft(title.rectTransform, new Vector2(-DeployHalfWidth, HeadingTop - 22f), new Vector2(900f, 56f));

            Text subtitle = EnsureLabel(background, "Deploy Subtitle",
                "Set your kit, then deploy. Click a slot to change what it carries.", 17, bold: false);
            subtitle.color = Hex("B1C5D0");
            subtitle.alignment = TextAnchor.MiddleLeft;
            TopLeft(subtitle.rectTransform, new Vector2(-DeployHalfWidth, HeadingTop - 84f), new Vector2(900f, 24f));
        }

        /// <summary>
        /// <c>.loadout-surface</c> / <c>.tactical-surface</c>: the operations panel round a
        /// content rect, a header row over it and, optionally, a hint row under it.
        /// </summary>
        /// <remarks>
        /// A child of the content, and its first, so it draws behind the content and appears and
        /// disappears with it — <c>LoadoutUi</c> swaps the equipment and the map for the armory by
        /// deactivating the containers.
        /// </remarks>
        private static AngularPanel DeploySurface(RectTransform content, string heading, string headingRight,
            string footer, string footerRight)
        {
            AngularPanel surface = EnsureAngular(content, "Surface");
            surface.transform.SetAsFirstSibling();
            StyleOperationsPanel(surface);
            surface.raycastTarget = false;
            RectTransform rect = surface.rectTransform;
            Stretch(rect);
            rect.offsetMin = new Vector2(-SurfacePad, -SurfaceFoot);
            rect.offsetMax = new Vector2(SurfacePad, SurfaceHead);

            Text head = EnsureLabel(surface.transform, "Heading", heading, 13, bold: true);
            head.alignment = TextAnchor.MiddleLeft;
            Pin(head.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(24f, -26f),
                new Vector2(400f, 20f));

            Text right = EnsureLabel(surface.transform, "Heading Right", headingRight, 12, bold: true);
            right.alignment = TextAnchor.MiddleRight;
            right.color = Hex("9EB9CA");
            right.supportRichText = true;
            Pin(right.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-24f, -26f),
                new Vector2(320f, 20f));

            Rule(surface.transform, "Heading Rule", top: true, SurfaceHead - 6f);

            Transform oldFooter = surface.transform.Find("Footer");
            if (footer == null)
            {
                if (oldFooter != null) Object.DestroyImmediate(oldFooter.gameObject);
                return surface;
            }

            Text foot = EnsureLabel(surface.transform, "Footer", footer, 11, bold: true);
            foot.alignment = TextAnchor.MiddleLeft;
            foot.color = Hex("8FAABA");
            Pin(foot.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0.5f), new Vector2(24f, 30f),
                new Vector2(460f, 18f));

            Text footRight = EnsureLabel(surface.transform, "Footer Right", footerRight, 11, bold: true);
            footRight.alignment = TextAnchor.MiddleRight;
            footRight.color = Hex("8FAABA");
            footRight.supportRichText = true;
            Pin(footRight.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0.5f), new Vector2(-24f, 30f),
                new Vector2(260f, 18f));

            Rule(surface.transform, "Footer Rule", top: false, 60f);
            return surface;
        }

        /// <summary>A 1px rule across a surface, <paramref name="inset"/> in from its top or bottom.</summary>
        private static void Rule(Transform surface, string name, bool top, float inset)
        {
            AngularPanel rule = EnsureAngular(surface, name);
            rule.color = new Color(113f / 255f, 143f / 255f, 160f / 255f, 0.32f);
            rule.Configure(0f, AngularEdge.None, 0f, Color.clear);
            rule.raycastTarget = false;
            RectTransform rect = rule.rectTransform;
            float edge = top ? 1f : 0f;
            rect.anchorMin = new Vector2(0f, edge);
            rect.anchorMax = new Vector2(1f, edge);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, top ? -inset : inset);
            rect.sizeDelta = new Vector2(-2f, 1f);
        }

        /// <summary>
        /// One equipment slot as <c>.loadout-card</c>: glass, an accent bar, its index caption,
        /// the item, its name and a change hint.
        /// </summary>
        /// <remarks>
        /// <c>Text</c> stays the slot's first label — <c>LoadoutUi</c> writes the item's name into
        /// <c>GetComponentInChildren&lt;Text&gt;</c> — so the hint is added after it. The index is
        /// the slot's old <c>Number</c>, which said "1" to "5" and nothing else.
        /// </remarks>
        private static void SlotCard(Transform slot, string caption)
        {
            Button button = slot.GetComponent<Button>();
            AngularPanel face = AsAngular(slot.gameObject);
            StyleCard(face);
            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = ButtonColours(button.colors, "secondary");

            Text number = Child(slot, "Number").GetComponent<Text>();
            number.text = caption;
            Style(number, BoldFont(), 12, Hex("9FC2D2"), TextAnchor.MiddleLeft);
            number.raycastTarget = false;
            Pin(number.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(20f, -20f),
                new Vector2(240f, 16f));

            Image picture = Child(slot, "Image").GetComponent<Image>();
            picture.preserveAspect = true;
            RectTransform pictureRect = picture.rectTransform;
            pictureRect.anchorMin = Vector2.zero;
            pictureRect.anchorMax = Vector2.one;
            pictureRect.offsetMin = new Vector2(20f, 52f);
            pictureRect.offsetMax = new Vector2(-20f, -38f);

            Text name = Child(slot, "Text").GetComponent<Text>();
            name.font = BoldFont();
            name.fontStyle = FontStyle.Normal;
            name.color = Ink;
            name.alignment = TextAnchor.MiddleLeft;
            name.resizeTextForBestFit = true;
            name.resizeTextMinSize = 12;
            name.resizeTextMaxSize = 22;
            RectTransform nameRect = name.rectTransform;
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(1f, 0f);
            nameRect.pivot = new Vector2(0f, 0f);
            nameRect.offsetMin = new Vector2(20f, 14f);
            nameRect.offsetMax = new Vector2(-104f, 44f);

            // "CHANGE ›" rather than the pack's "↻ CHANGE": Roboto has no ↻, and the fallback font
            // drew it as a speck.
            Text hint = EnsureLabel(slot, "Change Hint", "CHANGE  ›", 11, bold: true);
            hint.color = Hex("FFA35A");
            hint.alignment = TextAnchor.MiddleRight;
            Pin(hint.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0.5f), new Vector2(-20f, 29f),
                new Vector2(90f, 16f));

            AngularPanel accent = EnsureAngular(slot, "Accent");
            accent.color = Hex("53B9EE");
            accent.Configure(0f, AngularEdge.None, 0f, Color.clear);
            accent.raycastTarget = false;
            Pin(accent.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, 0f),
                new Vector2(56f, 3f));
        }

        /// <summary>The loadout's DEPLOY: the pack's orange action, full width under the map.</summary>
        private static void DeployAction(Transform buttonTransform)
        {
            RectTransform rect = (RectTransform)buttonTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -14f);
            rect.sizeDelta = new Vector2(0f, 64f);

            Button button = buttonTransform.GetComponent<Button>();
            AngularPanel face = AsAngular(buttonTransform.gameObject);
            StyleDeployFace(face);
            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = ButtonColours(button.colors, "primary");

            Text text = Child(buttonTransform, "Text").GetComponent<Text>();
            text.text = DeployCaption;
            Style(text, BoldFont(), 26, DeployInk, TextAnchor.MiddleCenter);
            text.raycastTarget = false;
            Stretch(text.rectTransform);
        }

        /// <summary>
        /// The weapon pictures' material, on a UI shader the armory list's mask can clip.
        /// </summary>
        /// <remarks>
        /// <c>LoadoutWeaponImage</c> drew with the legacy <c>Particles/Additive</c> shader, which
        /// implements neither UI clipping nor the stencil: in the list, which scrolls inside a
        /// <see cref="RectMask2D"/>, the cards stopped at the edge and the weapons carried on past
        /// it. The pictures are line art on black and need the additive blend — on the default
        /// UI material the black shows — so the material keeps its blend on
        /// <c>Ironfront/UI Additive</c>. One material, so the slots, the list and the inspector
        /// stay alike.
        /// </remarks>
        private static void ClippablePictureMaterial(Transform kit, StringBuilder log)
        {
            Material material = Child(kit, "Primary Button/Image").GetComponent<Image>().material;
            if (material == null || !AssetDatabase.Contains(material))
                throw new System.InvalidOperationException(
                    "The loadout's pictures have no material asset; the restyle and the prefab have drifted.");

            Shader shader = Shader.Find("Ironfront/UI Additive");
            if (shader == null)
                throw new System.InvalidOperationException(
                    "Shader 'Ironfront/UI Additive' is missing (Assets/Shader/IronfrontUiAdditive.shader).");

            if (material.shader != shader)
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
            }

            log.AppendLine("pictures: " + material.name + " on " + shader.name + ", so the armory list clips them.");
        }

        /// <summary>
        /// Solid navy surfaces under <paramref name="root"/> as equipment cards, with any button
        /// that drew with one pointed at its card.
        /// </summary>
        /// <remarks>
        /// Only solid, sprite-less navy is converted: the half-transparent row behind a small
        /// item, and every picture, stay as they are.
        /// </remarks>
        private static void Cardify(Transform root)
        {
            foreach (Image image in root.GetComponentsInChildren<Image>(true).ToList())
            {
                if (image.sprite != null || image.color.a < 0.99f || !Near(image.color, Navy)) continue;

                GameObject owner = image.gameObject;
                StyleCard(AsAngular(owner));
            }

            foreach (Button button in root.GetComponentsInChildren<Button>(true))
            {
                if (button.targetGraphic != null) continue;
                AngularPanel face = button.GetComponent<AngularPanel>();
                if (face == null) continue;
                button.targetGraphic = face;
                button.transition = Selectable.Transition.ColorTint;
                button.colors = ButtonColours(button.colors, "secondary");
            }
        }

        /// <summary>A loadout action on the menu's face; it keeps the rect its anchors give it.</summary>
        private static void ActionButton(Transform buttonTransform, string caption, string kind)
        {
            // The menu-row class, so the caption is the title screen's size rather than the
            // compact 16px of a table button.
            const float Height = 64f;

            RectTransform rect = (RectTransform)buttonTransform;
            if (rect.anchorMin.y == rect.anchorMax.y) rect.sizeDelta = new Vector2(rect.sizeDelta.x, Height);

            Button button = buttonTransform.GetComponent<Button>();
            AngularPanel face = AsAngular(buttonTransform.gameObject);
            StyleButtonFace(face, kind, Height);
            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = ButtonColours(button.colors, kind);

            Text text = Child(buttonTransform, "Text").GetComponent<Text>();
            text.text = caption;
            text.font = BoldFont();
            text.fontStyle = FontStyle.Bold;
            text.color = CaptionInk(kind);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 14;
            text.resizeTextMaxSize = CaptionSize(Height);
        }

        /// <summary>
        /// The score bar: its frame on the menu's glass, its figures bold, its captions shadowed.
        /// </summary>
        /// <remarks>
        /// The bars themselves are <c>ScoreUi</c>'s — it pulses them between white and the side's
        /// colour — so only the frame and track round them are restyled.
        /// </remarks>
        private static void RestyleScoreBar(Transform score, StringBuilder log)
        {
            Canvas canvas = score.GetComponent<Canvas>();
            canvas.pixelPerfect = true;

            // Above the loadout screen's shade, which it shared order 0 with: the score and the
            // clock stay readable while a player deploys, as the pack's deploy screen keeps its
            // score line, and the victory banner is not left to hierarchy order either.
            canvas.sortingOrder = 1;

            AngularPanel frame = AsAngular(Child(score, "Panel/Bar Background").gameObject);
            frame.color = WithAlpha(Navy, 0.92f);
            frame.Configure(8f, AngularEdge.All, 1f, Line);
            frame.raycastTarget = false;

            Child(score, "Panel/Bar Background/Bar Container").GetComponent<Image>().color = Hex("13283B");

            foreach (Text text in score.GetComponentsInChildren<Text>(true))
            {
                text.font = BoldFont();
                if (text.transform.parent != null && text.transform.parent.name == "Phase Row")
                    EnsureShadow(text);
            }

            // The flag counts on the pack's angular badges rather than white discs.
            foreach (Transform circle in Child(score, "Panel").Cast<Transform>().Where(t => t.name == "Circle"))
            {
                AngularPanel badge = AsAngular(circle.gameObject);
                badge.color = WithAlpha(Navy, 0.92f);
                badge.Configure(8f, AngularEdge.All, 1f, Line);
                badge.raycastTarget = false;
            }

            // The phase and its clock meet at the middle, under the bar's centre line, as the
            // pack's "CONQUEST ◈ 00:00" does, instead of sitting under the two far ends.
            Transform phaseRow = Child(score, "Phase Row");
            Text phase = Child(phaseRow, "Phase Label").GetComponent<Text>();
            Style(phase, BoldFont(), 17, Ink, TextAnchor.MiddleRight);
            phase.rectTransform.offsetMax = new Vector2(-12f, 0f);
            Text clock = Child(phaseRow, "Phase Timer").GetComponent<Text>();
            Style(clock, BoldFont(), 17, Ink, TextAnchor.MiddleLeft);
            clock.rectTransform.offsetMin = new Vector2(12f, 0f);
            Text humans = Child(phaseRow, "Human Count").GetComponent<Text>();
            Style(humans, RegularFont(), 14, Muted, TextAnchor.MiddleCenter);

            log.AppendLine("score bar: glass frame, navy track, angular flag badges, phase and clock centred.");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Ravenfield's grey surfaces in the menu's navy, and its hairline type in Roboto Regular.
        /// </summary>
        /// <remarks>
        /// Colour and font only, so a subtree <c>LoadoutUi</c> walks by name and by child order is
        /// left structurally as it was. Alpha is kept: the half-transparent grey stays
        /// half-transparent navy.
        /// </remarks>
        private static void Recolour(Transform root)
        {
            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                Color colour = image.color;
                if (image.sprite == null && Near(colour, LegacyGrey))
                    image.color = WithAlpha(Navy, colour.a);
            }

            foreach (Text text in root.GetComponentsInChildren<Text>(true))
            {
                if (text.font == null) text.font = BoldFont();
                else if (text.font.name == "Roboto-Light") text.font = RegularFont();
            }
        }

        private static bool Near(Color a, Color b)
            => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        /// <summary>The object's surface as an <see cref="AngularPanel"/>, replacing a flat Image.</summary>
        /// <remarks>
        /// A Button's <c>targetGraphic</c> pointing at the Image is left null by the swap; every
        /// caller re-points it at the returned panel.
        /// </remarks>
        private static AngularPanel AsAngular(GameObject go)
        {
            AngularPanel panel = go.GetComponent<AngularPanel>();
            if (panel != null) return panel;

            Image image = go.GetComponent<Image>();
            bool raycast = image == null || image.raycastTarget;
            if (image != null) Object.DestroyImmediate(image);

            panel = go.AddComponent<AngularPanel>();
            panel.raycastTarget = raycast;
            return panel;
        }

        private static AngularPanel EnsureAngular(Transform parent, string name)
            => Ensure<AngularPanel>(parent, name);

        private static T Ensure<T>(Transform parent, string name) where T : Component
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform));
            if (existing == null) go.transform.SetParent(parent, worldPositionStays: false);

            T component = go.GetComponent<T>();
            return component != null ? component : go.AddComponent<T>();
        }

        private static Text EnsureLabel(Transform parent, string name, string content, int size, bool bold)
        {
            Text text = Ensure<Text>(parent, name);
            text.text = content;
            Style(text, bold ? BoldFont() : RegularFont(), size, Ink, TextAnchor.MiddleCenter);
            text.raycastTarget = false;
            return text;
        }

        private static void Style(Text text, Font font, int size, Color colour, TextAnchor alignment)
        {
            text.font = font;
            text.fontSize = size;
            text.fontStyle = FontStyle.Normal;
            text.color = colour;
            text.alignment = alignment;
            text.resizeTextForBestFit = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
        }

        /// <summary>A drop shadow, for type that sits straight on the world.</summary>
        private static void EnsureShadow(Text text)
        {
            // Compared with Unity's ==, not ??: in the Editor a missing component comes back as a
            // placeholder object that ?? takes for a real one.
            Shadow shadow = text.GetComponent<Shadow>();
            if (shadow == null) shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
        }

        /// <summary>A descendant by path, or a thrown exception naming what was missing.</summary>
        /// <remarks>
        /// Throws for <c>IronfrontRebornUiAssetCatalog.Sprite</c>'s reason: a restyle that
        /// silently skipped an element it could not find would report success over a HUD that is
        /// half old and half new.
        /// </remarks>
        private static Transform Child(Transform parent, string path)
        {
            Transform found = parent.Find(path);
            if (found == null)
                throw new System.InvalidOperationException(
                    "'" + parent.name + "' has no '" + path + "'. The prefab and the restyle have drifted; "
                    + "fix this file rather than the prefab by hand.");
            return found;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Centre(RectTransform rect, Vector2 position, Vector2 size)
            => Pin(rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), position, size);

        /// <summary>Places a rect by its top-left corner, in reference pixels from the canvas centre.</summary>
        private static void TopLeft(RectTransform rect, Vector2 corner, Vector2 size)
            => Pin(rect, new Vector2(0.5f, 0.5f), new Vector2(0f, 1f), corner, size);

        private static void Pin(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
