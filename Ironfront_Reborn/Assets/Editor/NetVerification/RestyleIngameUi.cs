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
    /// <b>Presentation only.</b> The pause menu, the status panel, the loadout screen and the
    /// score bar are Ravenfield's, driven by <c>IngameMenuUi</c>, <c>IngameUi</c>,
    /// <c>LoadoutUi</c> and <c>ScoreUi</c>. Every object those scripts reach — by field, by
    /// <c>Find</c> name, or by <c>GetComponentInChildren&lt;Text&gt;</c> order — keeps its
    /// object, its name and its place, and every button keeps its persistent onClick calls. What
    /// changes is scale, type, colour, surfaces and captions.
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
        /// The bottom-left status panel: health, weapon and ammunition on the menu's glass.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It sat flush in the screen corner as a black 40% rectangle, the numbers in Roboto Light
        /// grey — the faintest type in the game, over the busiest background. The layout is
        /// rebuilt left to right with the same objects: the cross as a glyph with the health
        /// beside it, a rule, the weapon at its own aspect, the magazine large and the reserve
        /// small. <c>IngameUi</c> only ever writes these texts and the weapon sprite.
        /// </para>
        /// <para>
        /// The resupply badges stay anchored to fractions of the panel's top edge, so they still
        /// rise over the health and the ammunition respectively.
        /// </para>
        /// </remarks>
        private static void RestyleStatusPanel(Transform ingame, StringBuilder log)
        {
            ingame.GetComponent<Canvas>().pixelPerfect = true;

            Transform panelTransform = Child(ingame, "Panel");
            AngularPanel panel = AsAngular(panelTransform.gameObject);
            panel.color = WithAlpha(Surface, 0.82f);
            panel.Configure(CutAction, AngularEdge.All, 1f, Line);
            panel.raycastTarget = false;
            Pin(panel.rectTransform, Vector2.zero, Vector2.zero, new Vector2(Margin, Margin),
                new Vector2(374f, 72f));

            Image cross = Child(panelTransform, "Health Image").GetComponent<Image>();
            cross.color = CyanSoft;
            cross.preserveAspect = true;
            Pin(cross.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f),
                new Vector2(26f, 26f));

            Text health = Child(cross.transform, "Health Text").GetComponent<Text>();
            Style(health, BoldFont(), 28, Ink, TextAnchor.MiddleLeft);
            RectTransform healthRect = health.rectTransform;
            healthRect.anchorMin = new Vector2(1f, 0f);
            healthRect.anchorMax = new Vector2(1f, 1f);
            healthRect.pivot = new Vector2(0f, 0.5f);
            healthRect.anchoredPosition = new Vector2(8f, 0f);
            healthRect.sizeDelta = new Vector2(64f, 14f);

            AngularPanel rule = EnsureAngular(panelTransform, "Divider");
            rule.color = WithAlpha(Line, 0.6f);
            rule.Configure(0f, AngularEdge.None, 0f, Color.clear);
            rule.raycastTarget = false;
            Pin(rule.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(126f, 0f),
                new Vector2(1f, 40f));

            Image weapon = Child(panelTransform, "Weapon Image").GetComponent<Image>();
            weapon.color = Ink;
            weapon.preserveAspect = true;
            Pin(weapon.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(138f, 0f),
                new Vector2(112f, 40f));

            Text magazine = Child(panelTransform, "Current Ammo Text").GetComponent<Text>();
            Style(magazine, BoldFont(), 28, Ink, TextAnchor.MiddleRight);
            Pin(magazine.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(254f, 0f),
                new Vector2(52f, 40f));

            Text reserve = Child(panelTransform, "Spare Ammo Text").GetComponent<Text>();
            Style(reserve, RegularFont(), 16, Muted, TextAnchor.MiddleLeft);
            Pin(reserve.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(308f, -4f),
                new Vector2(58f, 30f));

            RectTransform vehicleBar = (RectTransform)Child(panelTransform, "Vehicle Health Background");
            vehicleBar.anchoredPosition = new Vector2(16f, -6f);
            vehicleBar.sizeDelta = new Vector2(192f, 6f);

            // Below the Match Readout's team chip (24 in, 62 tall), which took the top-left corner
            // it used to share with this ring.
            ((RectTransform)Child(ingame, "Flag Capture Indicator Edge")).anchoredPosition =
                new Vector2(Margin, -(Margin + 62f + 12f));

            log.AppendLine("status panel: glass, 24px from the corner, bold figures; capture ring below the team chip.");
        }

        /// <summary>
        /// The loadout screen, in the menu's navy and type, with its two actions as the menu's
        /// buttons.
        /// </summary>
        /// <returns>The arsenal button prefabs <c>LoadoutUi</c> instantiates.</returns>
        private static List<string> RestyleLoadout(Transform loadout, StringBuilder log)
        {
            Recolour(loadout);

            // The slot numbers had no font at all, which Unity draws as nothing: every "1" to "5"
            // on the slots was an empty label.
            foreach (Text number in loadout.GetComponentsInChildren<Text>(true).Where(t => t.name == "Number"))
            {
                number.font = BoldFont();
                number.color = CyanSoft;
            }

            ActionButton(Child(loadout, "Background Panel/Minimap Container/Deploy Button"), "DEPLOY", "primary");
            ActionButton(Child(loadout, "Background Panel/Arsenal Inspector Container/Select Button"), "SELECT",
                "secondary");

            Transform indicator = Child(loadout, "Background Panel/Scroll Indicator");
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

            log.AppendLine("loadout: navy surfaces, slot numbers given a font, DEPLOY and SELECT as menu buttons.");
            return prefabs;
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
            score.GetComponent<Canvas>().pixelPerfect = true;

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

            log.AppendLine("score bar: glass frame, navy track, bold figures, shadowed phase row.");
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
