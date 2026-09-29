using System.Text;
using Ironfront.Net.Unity.Client.Hud;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Authors the in-match readout onto <c>Ingame UI Container.prefab</c> and assigns every
    /// reference. P17 3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A command, not a drag</b> — the rule <c>BuildMenuCanvas</c> already states and P3 § 3.3
    /// earned: fileIDs are Editor-assigned, so a hand-written reference in the YAML resolves to
    /// null while looking assigned, and the authoring gate cannot tell the two apart from what is
    /// on disk. Everything below goes through real Editor APIs, so every fileID it writes is one
    /// Unity minted, and a mistake is fixed by running it again.
    /// </para>
    /// <para>
    /// <b>A PREFAB, not a scene, and that changes the mechanics.</b> The in-match HUD is not
    /// authored into Dustbowl or Island at all: <c>GameManager.StartGame</c> instantiates
    /// <c>ingameUiPrefab</c> when <c>LocalClient.Exists</c>, so the asset is the only place the
    /// elements can live and every map gets them at once. Editing goes through
    /// <c>PrefabUtility.LoadPrefabContents</c> / <c>SaveAsPrefabAsset</c> rather than through the
    /// scene APIs <c>BuildMenuCanvas</c> uses.
    /// </para>
    /// <para>
    /// <b>Re-running REBUILDS the subtree it owns.</b> Everything under <see cref="RootName"/>
    /// was written by this file, so a deterministic rebuild makes the prefab a function of it.
    /// Nothing outside that root is touched — the five Canvases the prefab already carries, the
    /// EventSystem and the legacy HUD are left exactly as they are.
    /// </para>
    /// <para>
    /// <b>It lives in <c>Ironfront.Net.Unity.EditorHarness</c> because it must.</b>
    /// <c>Ironfront.Net.Unity.Client</c> ships <c>autoReferenced: false</c>, so
    /// <c>Assembly-CSharp-Editor</c> cannot name <see cref="MatchHud"/> — the seal is two-way
    /// (contracts § 6.1). This is that asmdef's third occupant, for the same reason as the other
    /// two.
    /// </para>
    /// <para>
    /// <b>Run headlessly:</b>
    /// <c>Unity -batchmode -nographics -quit -projectPath Ironfront_Reborn
    /// -executeMethod Ironfront.Net.Unity.EditorTools.BuildMatchHud.Run</c>.
    /// </para>
    /// </remarks>
    public static class BuildMatchHud
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";
        private const string ReportFile = "build-match-hud.txt";

        /// <summary>The root this script owns entirely and rebuilds on every run.</summary>
        public const string RootName = "Match Readout";

        /// <summary>
        /// Above the five Canvases the prefab already carries, all of which sit at 0.
        /// </summary>
        /// <remarks>
        /// Stated rather than left to hierarchy position, which changes whenever somebody
        /// reorders the prefab. The deploy screen is a death overlay and has to cover the loadout
        /// and minimap Canvases; the killfeed and the team readout ride the same Canvas because
        /// splitting them would buy a second sorting decision and nothing else.
        /// </remarks>
        private const int SortingOrder = 50;

        private static readonly Color Ink = HudStyle.Ink;
        private static readonly Color Backdrop = new Color(0.04f, 0.05f, 0.07f, 0.82f);

        [MenuItem("Ironfront/Net/Build in-match readout")]
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
            System.IO.File.WriteAllText(ReportFile, report);

            if (ok) Debug.Log("[build-match-hud]\n" + report);
            else Debug.LogError("[build-match-hud] FAILED\n" + report);

            if (!ok && exitOnFailure) EditorApplication.Exit(1);
        }

        private static bool Build(StringBuilder log)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);

            if (contents == null)
            {
                log.AppendLine("FAILED: could not load " + PrefabPath + ". Has it moved?");
                return false;
            }

            try
            {
                RemovePreviousRoot(contents, log);

                GameObject root = new GameObject(
                    RootName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                root.transform.SetParent(contents.transform, worldPositionStays: false);

                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;

                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                MatchHud hud = root.AddComponent<MatchHud>();

                // First, so everything else on this Canvas draws over the plates.
                NameplateLayer nameplates = BuildNameplates(root, log);
                Text team = BuildTeamReadout(root, log);
                KillfeedRowView[] killfeed = BuildKillfeed(root, log);
                GameObject deploy = BuildDeployScreen(
                    root, out Text killer, out Text timer, out Button deployButton, log);
                ScoreboardView scoreboard = BuildScoreboard(root, log);

                var so = new SerializedObject(hud);
                Assign(so, "_teamReadoutText", team);
                AssignArray(so, "_killfeedRows", killfeed);
                Assign(so, "_deployRoot", deploy);
                Assign(so, "_deployKillerText", killer);
                Assign(so, "_deployTimerText", timer);
                Assign(so, "_deployButton", deployButton);
                Assign(so, "_scoreboard", scoreboard);
                Assign(so, "_nameplates", nameplates);
                AssignArray(so, "_hiddenUnderScoreboard", new Object[]
                {
                    killfeed[0].transform.parent.GetComponent<CanvasGroup>(),
                    team.transform.parent.GetComponent<CanvasGroup>(),
                    nameplates.GetComponent<CanvasGroup>(),
                });
                so.ApplyModifiedPropertiesWithoutUndo();

                // The authored state is what a reader of the prefab sees, and what the offline
                // game gets if MatchHud.Awake never runs. Down is the only safe one: an overlay
                // authored visible is ledger X-48's failure, one screen over.
                deploy.SetActive(false);
                scoreboard.gameObject.SetActive(false);

                PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
                log.AppendLine("saved: " + PrefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Deletes the subtree a previous run authored, if there is one.
        /// </summary>
        /// <remarks>
        /// Matched by name among the prefab root's direct children, which is what this script
        /// controls. A component-type search would also match a Canvas somebody built by hand,
        /// and deleting authored work nobody asked to delete is the worse failure.
        /// </remarks>
        private static void RemovePreviousRoot(GameObject contents, StringBuilder log)
        {
            Transform previous = contents.transform.Find(RootName);
            if (previous == null) return;

            log.AppendLine("rebuilding: removed the previous '" + RootName + "'.");
            Object.DestroyImmediate(previous.gameObject);
        }

        // ------------------------------------------------------------------ the elements

        /// <summary>3.1 — which side you are on, top-left, above the ammo readout.</summary>
        private static Text BuildTeamReadout(GameObject root, StringBuilder log)
        {
            // Backdrop, the same way BuildDeployScreen and BuildScoreboard back their own text —
            // a bare Text over the killfeed and minimap underneath it was unreadable at a glance.
            var panel = new GameObject(
                "Team Readout", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            panel.transform.SetParent(root.transform, worldPositionStays: false);

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(36f, -36f);
            panelRect.sizeDelta = new Vector2(320f, 44f);

            var backdrop = panel.GetComponent<Image>();
            backdrop.color = Backdrop;
            backdrop.raycastTarget = false;

            Text label = Label(panel, "Text", string.Empty, 26, TextAnchor.UpperLeft);
            Stretch(label.GetComponent<RectTransform>());

            // Authored EMPTY, deliberately. Criterion 2 is graded on a screenshot at join, and a
            // placeholder string would render as an answer for however long the first snapshot
            // takes -- the fabricated zero ScoreUi refuses, wearing a different label.
            log.AppendLine("team readout: authored blank; MatchHud.SetLocalTeam writes it.");
            return label;
        }

        /// <summary>
        /// 3.3, rebuilt for feature 2 (playtest 2026-09-28) — the killfeed, top-right, newest
        /// first: one row per kill, each a dark rounded backing sized to what it says.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A row is a small layout, not a line of rich text.</b> The old row was one
        /// <c>Text</c> with colour tags, which can say who and whom but cannot give the "how" a
        /// chip, the headshot a mark, or the row a backing it can be read against. Each row
        /// here is a <see cref="HorizontalLayoutGroup"/> whose width follows its content, so a
        /// short kill is a short pill and nothing is clipped.
        /// </para>
        /// <para>
        /// <b>Authored hidden.</b> Every row starts inactive and transparent; <see cref="KillfeedRowView"/>
        /// shows one when a kill arrives. An authored-visible row would be an empty pill on the
        /// offline game's screen, which never runs the component that would hide it.
        /// </para>
        /// </remarks>
        private static KillfeedRowView[] BuildKillfeed(GameObject root, StringBuilder log)
        {
            Font bold = LoadFont(RobotoBoldPath);
            Font medium = LoadFont(RobotoMediumPath);
            Sprite rounded = RoundedSprite();

            var feed = new GameObject("Killfeed", typeof(RectTransform), typeof(CanvasGroup));
            feed.transform.SetParent(root.transform, worldPositionStays: false);

            RectTransform feedRect = feed.GetComponent<RectTransform>();
            feedRect.anchorMin = new Vector2(1f, 1f);
            feedRect.anchorMax = new Vector2(1f, 1f);
            feedRect.pivot = new Vector2(1f, 1f);
            // Below the original game's score bar, whose right-hand flag counter the first two rows
            // covered at every aspect (release test 2026-09-29).
            feedRect.anchoredPosition = new Vector2(-28f, -KillfeedTop);
            feedRect.sizeDelta = new Vector2(760f, MatchHud.KillfeedRows * KillfeedRowView.RowPitch);

            var rows = new KillfeedRowView[MatchHud.KillfeedRows];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = BuildKillfeedRow(feed, i, bold, medium, rounded);

            // Read off MatchHud.KillfeedRows, which reads off KillfeedModel.DefaultCapacity, so
            // raising the model's capacity authors the rows to match instead of silently
            // dropping the oldest lines on the floor.
            log.AppendLine("killfeed: " + rows.Length + " rows, from KillfeedModel.DefaultCapacity; "
                           + "Roboto, rounded backings, authored hidden.");
            return rows;
        }

        private static KillfeedRowView BuildKillfeedRow(
            GameObject feed, int index, Font bold, Font medium, Sprite rounded)
        {
            var row = new GameObject(
                "Killfeed Row " + index,
                typeof(RectTransform), typeof(CanvasGroup), typeof(Image), typeof(Outline),
                typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            row.transform.SetParent(feed.transform, worldPositionStays: false);

            RectTransform rect = row.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(0f, -index * KillfeedRowView.RowPitch);
            rect.sizeDelta = new Vector2(0f, KillfeedRowView.RowHeight);

            var group = row.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var backing = row.GetComponent<Image>();
            backing.sprite = rounded;
            backing.type = Image.Type.Sliced;
            backing.color = HudStyle.RowBacking;
            backing.raycastTarget = false;

            var edge = row.GetComponent<Outline>();
            edge.effectDistance = new Vector2(1.5f, -1.5f);
            edge.useGraphicAlpha = true;
            edge.enabled = false;

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 14, 5, 5);
            layout.spacing = 9f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            // Width follows the content; height is the row's own, so every row lines up.
            var fitter = row.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            // The sweep of light across a new row, clipped to the row by a mask of its own so a
            // badge popping larger than the row is not clipped with it. First, so it draws under
            // the words.
            Image sheen = SheenLayer(row);

            Image accent = Block(row, "Accent", new Vector2(4f, 26f));

            // A death nobody scored and a match event lead with a picture of what happened.
            Image lead = Block(row, "Lead", new Vector2(26f, 26f));
            lead.preserveAspect = true;
            ShadowUnder(lead);

            Text killer = BoardText(row, "Killer", bold, 24, Ink);
            Text verb = BoardText(row, "Verb", medium, 21, HudStyle.SentenceInk);

            // The weapon's own silhouette, from the loadout screen, drawn between the names when the
            // kill names a weapon that has one. KillfeedRowView sizes it to the picture's shape.
            Image weapon = Block(row, "Weapon", new Vector2(60f, KillfeedRowView.WeaponIconHeight));
            weapon.color = Ink;
            weapon.preserveAspect = true;
            weapon.material = LoadMaterial(WeaponSilhouettePath);
            ShadowUnder(weapon);

            // A drawn picture in the same place when no weapon picture says how: the tank, the
            // blast. Not through the silhouette material, which would turn its clear parts white.
            Image glyph = Block(row, "Glyph", new Vector2(44f, KillfeedRowView.GlyphHeight));
            glyph.color = Ink;
            glyph.preserveAspect = true;
            ShadowUnder(glyph);

            var how = new GameObject(
                "How", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
            how.transform.SetParent(row.transform, worldPositionStays: false);

            var howBacking = how.GetComponent<Image>();
            howBacking.sprite = rounded;
            howBacking.type = Image.Type.Sliced;
            howBacking.color = HudStyle.ChipBacking;
            howBacking.raycastTarget = false;

            var howLayout = how.GetComponent<HorizontalLayoutGroup>();
            howLayout.padding = new RectOffset(8, 8, 3, 3);
            howLayout.childAlignment = TextAnchor.MiddleCenter;
            howLayout.childControlWidth = true;
            howLayout.childControlHeight = true;
            howLayout.childForceExpandWidth = false;
            howLayout.childForceExpandHeight = false;

            Text howText = BoardText(how, "Text", bold, 17, HudStyle.ChipInk, shadowed: false);

            Image headshot = Block(row, "Headshot", new Vector2(22f, 22f));
            headshot.color = HudStyle.HeadshotInk;
            headshot.preserveAspect = true;

            Image longShot = Block(row, "Long Shot", new Vector2(20f, 20f));
            longShot.color = HudStyle.LongShotInk;
            longShot.preserveAspect = true;

            Text distance = BoardText(row, "Distance", bold, 18, HudStyle.LongShotInk);

            Text victim = BoardText(row, "Victim", bold, 24, Ink);
            Text sentence = BoardText(row, "Sentence", medium, 21, HudStyle.SentenceInk);

            GameObject badge = BuildBadge(row, bold, rounded, out Image badgeBacking, out Outline badgeGlow,
                                          out Image badgeIcon, out Text badgeText);

            Image timer = TimerBar(row);

            KillfeedRowView view = row.AddComponent<KillfeedRowView>();

            var so = new SerializedObject(view);
            Assign(so, "_backing", backing);
            Assign(so, "_edge", edge);
            Assign(so, "_accent", accent);
            Assign(so, "_lead", lead);
            Assign(so, "_killer", killer);
            Assign(so, "_verb", verb);
            Assign(so, "_weapon", weapon);
            Assign(so, "_weaponSize", weapon.GetComponent<LayoutElement>());
            Assign(so, "_glyph", glyph);
            Assign(so, "_glyphSize", glyph.GetComponent<LayoutElement>());
            Assign(so, "_how", how);
            Assign(so, "_howText", howText);
            Assign(so, "_headshot", headshot);
            Assign(so, "_longShot", longShot);
            Assign(so, "_distance", distance);
            Assign(so, "_victim", victim);
            Assign(so, "_sentence", sentence);
            Assign(so, "_badge", badge);
            Assign(so, "_badgeBacking", badgeBacking);
            Assign(so, "_badgeGlow", badgeGlow);
            Assign(so, "_badgeIcon", badgeIcon);
            Assign(so, "_badgeText", badgeText);
            Assign(so, "_sheen", sheen);
            Assign(so, "_timer", timer);
            so.ApplyModifiedPropertiesWithoutUndo();

            row.SetActive(false);
            return view;
        }

        /// <summary>A soft drop shadow under a picture, so a white shape reads over a bright sky.</summary>
        private static void ShadowUnder(Image image)
        {
            var shadow = image.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            shadow.effectDistance = new Vector2(1f, -1f);
        }

        /// <summary>
        /// The badge at the end of a kill: a chip in its tone's colour with a picture and the words
        /// ("TRIPLE KILL"), and a glow the row pulses. Its pivot is its centre, so it pops in place.
        /// </summary>
        private static GameObject BuildBadge(
            GameObject row, Font bold, Sprite rounded,
            out Image backing, out Outline glow, out Image icon, out Text text)
        {
            var badge = new GameObject(
                "Badge", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(HorizontalLayoutGroup));
            badge.transform.SetParent(row.transform, worldPositionStays: false);
            ((RectTransform)badge.transform).pivot = new Vector2(0.5f, 0.5f);

            backing = badge.GetComponent<Image>();
            backing.sprite = rounded;
            backing.type = Image.Type.Sliced;
            backing.color = HudStyle.Gold;
            backing.raycastTarget = false;

            glow = badge.GetComponent<Outline>();
            glow.effectDistance = new Vector2(2.5f, -2.5f);
            glow.useGraphicAlpha = false;
            glow.effectColor = new Color(1f, 1f, 1f, 0f);

            var layout = badge.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(7, 10, 3, 3);
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            icon = Block(badge, "Icon", new Vector2(18f, 18f));
            icon.preserveAspect = true;
            icon.color = HudStyle.BadgeInk;

            text = BoardText(badge, "Text", bold, 16, HudStyle.BadgeInk);
            return badge;
        }

        /// <summary>The band of light a new row sweeps, inside a mask stretched over the row.</summary>
        private static Image SheenLayer(GameObject row)
        {
            var clip = new GameObject("Sheen Clip", typeof(RectTransform), typeof(RectMask2D), typeof(LayoutElement));
            clip.transform.SetParent(row.transform, worldPositionStays: false);
            clip.GetComponent<LayoutElement>().ignoreLayout = true;
            Stretch(clip.GetComponent<RectTransform>());

            var band = new GameObject("Sheen", typeof(RectTransform), typeof(Image));
            band.transform.SetParent(clip.transform, worldPositionStays: false);

            RectTransform rect = band.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(120f, 0f);
            rect.anchoredPosition = new Vector2(-120f, 0f);

            var image = band.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);
            image.raycastTarget = false;
            band.SetActive(false);
            return image;
        }

        /// <summary>The thin bar along the foot of a row that counts its time down.</summary>
        private static Image TimerBar(GameObject row)
        {
            var bar = new GameObject("Timer", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            bar.transform.SetParent(row.transform, worldPositionStays: false);
            bar.GetComponent<LayoutElement>().ignoreLayout = true;

            RectTransform rect = bar.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.offsetMin = new Vector2(8f, 2f);
            rect.offsetMax = new Vector2(-8f, 4f);

            var image = bar.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.5f);
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A text part of the killfeed or the scoreboard, in Roboto and shadowed.</summary>
        /// <remarks>
        /// The shadow is what keeps a name legible where a backing's alpha lets a bright sky
        /// through; a chip's own text drops it, sitting on a pane of its own.
        /// </remarks>
        private static Text BoardText(
            GameObject parent, string name, Font font, int size, Color ink,
            TextAnchor anchor = TextAnchor.MiddleLeft, bool shadowed = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.text = string.Empty;
            text.color = ink;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.raycastTarget = false;

            if (shadowed)
            {
                var shadow = go.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
                shadow.effectDistance = new Vector2(1f, -1f);
            }

            return text;
        }

        /// <summary>A fixed-size image part of a killfeed row.</summary>
        private static Image Block(GameObject parent, string name, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            var element = go.GetComponent<LayoutElement>();
            element.minWidth = size.x;
            element.preferredWidth = size.x;
            element.minHeight = size.y;
            element.preferredHeight = size.y;

            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private const string RobotoBoldPath = "Assets/Font/Roboto-Bold.ttf";

        /// <summary>
        /// The weapon pictures are white guns on black with no alpha, so the killfeed draws them
        /// through Ironfront/UI/Silhouette, which turns their brightness into alpha.
        /// </summary>
        private const string WeaponSilhouettePath = "Assets/Material/UI Weapon Silhouette.mat";

        /// <summary>A material the readout needs; missing is an error, like a missing font.</summary>
        private static Material LoadMaterial(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
                throw new System.InvalidOperationException(
                    "The killfeed's weapon icons draw through " + path + ", which is not there. "
                    + "Without it every gun sits in a black box.");
            return material;
        }
        private const string RobotoMediumPath = "Assets/Font/Roboto-Medium.ttf";

        /// <summary>
        /// The game's own UI face. Missing is an error, not a quiet fall back to Arial: a killfeed
        /// in the wrong font looks finished, and nobody would go looking for why.
        /// </summary>
        private static Font LoadFont(string path)
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>(path);
            if (font == null)
                throw new System.InvalidOperationException(
                    "The killfeed is authored in " + path + ", which is not there. Has the font moved?");
            return font;
        }

        /// <summary>
        /// Unity's built-in nine-sliced rounded rectangle, the one its default Button uses.
        /// </summary>
        /// <remarks>
        /// A built-in extra resource serializes as a reference Unity itself ships in every build,
        /// so the prefab needs no texture asset of its own for a rounded corner.
        /// </remarks>
        private static Sprite RoundedSprite()
        {
            Sprite sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            if (sprite == null)
                throw new System.InvalidOperationException(
                    "Unity's built-in UI/Skin/UISprite.psd did not load; the killfeed's rounded "
                    + "backings need it.");
            return sprite;
        }

        /// <summary>
        /// Feature 1 (playtest 2026-09-28) — the name plates, in the holo frame the owner chose on
        /// 2026-09-29: a full-screen layer and the one plate it clones per person.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The plate's pivot is its bottom centre, the point the presenter places over a head:
        /// the pointer sits on that point, and the glass pane sits on the pointer.
        /// </para>
        /// <para>
        /// <b>The pane sizes itself</b>: a vertical layout of two rows under a content fitter. The
        /// top row is the emblem, the name as written, the leader's star, then -- pushed right by
        /// a flexible gap -- the vehicle or water mark and the weapon's silhouette. The bottom row
        /// is the medic cross, the bar, the pin and the metres. The bar is flexible, so it takes
        /// whatever width a long name gives the pane; its fill and trail are anchored as a share
        /// of it. The scanlines and the four brackets sit outside the layout, over the pane.
        /// </para>
        /// <para>
        /// Every icon is drawn in code (<see cref="HudSprites"/>) and handed over by
        /// <see cref="NameplateView"/> at runtime, so none is authored here. Authored hidden; a
        /// plate appears when the presenter sets one.
        /// </para>
        /// </remarks>
        private static NameplateLayer BuildNameplates(GameObject root, StringBuilder log)
        {
            Font bold = LoadFont(RobotoBoldPath);
            Font medium = LoadFont(RobotoMediumPath);

            var layer = new GameObject("Nameplates", typeof(RectTransform), typeof(CanvasGroup));
            layer.transform.SetParent(root.transform, worldPositionStays: false);
            Stretch(layer.GetComponent<RectTransform>());

            var group = layer.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            // A point over the head: the parts below hang off its bottom centre.
            var plate = new GameObject("Nameplate Template", typeof(RectTransform), typeof(CanvasGroup));
            plate.transform.SetParent(layer.transform, worldPositionStays: false);
            Place(plate.GetComponent<RectTransform>(), Middle, BottomCentre, Vector2.zero, Vector2.zero);

            var plateGroup = plate.GetComponent<CanvasGroup>();
            plateGroup.interactable = false;
            plateGroup.blocksRaycasts = false;

            Image pointer = Picture(plate, "Pointer", Ink);
            Place(pointer, BottomCentre, BottomCentre, Vector2.zero, new Vector2(PlatePointerWidth, PlatePointerHeight));

            // The glass pane, sized by its rows.
            var pane = new GameObject(
                "Pane", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            pane.transform.SetParent(plate.transform, worldPositionStays: false);
            Place(pane.GetComponent<RectTransform>(), BottomCentre, BottomCentre,
                new Vector2(0f, PlatePointerHeight + 4f), Vector2.zero);

            var glass = pane.GetComponent<Image>();
            glass.color = HudStyle.PlateGlass;
            glass.raycastTarget = false;

            var paneLayout = pane.GetComponent<VerticalLayoutGroup>();
            paneLayout.padding = new RectOffset(12, 12, 7, 9);
            paneLayout.spacing = 5f;
            paneLayout.childAlignment = TextAnchor.UpperLeft;
            paneLayout.childControlWidth = true;
            paneLayout.childControlHeight = true;
            paneLayout.childForceExpandWidth = true;
            paneLayout.childForceExpandHeight = false;

            var paneFit = pane.GetComponent<ContentSizeFitter>();
            paneFit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            paneFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Image scanlines = Overlay(pane, "Scanlines");
            Stretch(scanlines.rectTransform);

            // The top row: who, and what they carry.
            GameObject header = PlateRow(pane, "Header");

            Image emblem = Block(header, "Emblem", new Vector2(16f, 16f));
            emblem.color = Ink;
            emblem.preserveAspect = true;

            Text name = BoardText(header, "Name", bold, PlateNameSize, Ink);

            Image star = Block(header, "Star", new Vector2(14f, 14f));
            star.preserveAspect = true;
            star.gameObject.SetActive(false);

            var gap = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
            gap.transform.SetParent(header.transform, worldPositionStays: false);
            var gapSize = gap.GetComponent<LayoutElement>();
            gapSize.minWidth = 10f;
            gapSize.flexibleWidth = 1f;

            Image state = Block(header, "State", new Vector2(16f, 16f));
            state.color = HudStyle.BoardMuted;
            state.preserveAspect = true;
            state.gameObject.SetActive(false);

            // The weapon in hand, as the loadout draws it, through the killfeed's silhouette.
            Image weapon = Block(header, "Weapon", new Vector2(44f, NameplateView.WeaponHeight));
            weapon.color = Ink;
            weapon.preserveAspect = true;
            weapon.material = LoadMaterial(WeaponSilhouettePath);
            var weaponShadow = weapon.gameObject.AddComponent<Shadow>();
            weaponShadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            weaponShadow.effectDistance = new Vector2(1f, -1f);
            weapon.gameObject.SetActive(false);

            // The bottom row: health, and how far away.
            GameObject status = PlateRow(pane, "Status");

            Image medic = Block(status, "Medic", new Vector2(12f, 12f));
            medic.color = Ink;
            medic.preserveAspect = true;

            Image track = Block(status, "Bar", new Vector2(PlateBarWidth, PlateBarHeight));
            track.color = new Color(1f, 1f, 1f, 0.16f);
            track.GetComponent<LayoutElement>().flexibleWidth = 1f;

            Image trail = BarShare(track, "Trail");
            Image fill = BarShare(track, "Fill");
            fill.color = Ink;

            // Quarter ticks, cut through the bar in the pane's own dark.
            foreach (float quarter in new[] { 0.25f, 0.5f, 0.75f })
            {
                Image tick = Picture(track.gameObject, "Tick " + quarter.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    new Color(HudStyle.PlateGlass.r, HudStyle.PlateGlass.g, HudStyle.PlateGlass.b, 0.95f));
                Place(tick, new Vector2(quarter, 0.5f), Middle, Vector2.zero, new Vector2(2f, PlateBarHeight + 2f));
            }

            Image pin = Block(status, "Pin", new Vector2(9f, 12f));
            pin.color = HudStyle.BoardFaint;
            pin.preserveAspect = true;

            Text distance = BoardText(status, "Distance", medium, 14, HudStyle.BoardFaint, TextAnchor.MiddleRight, shadowed: false);
            distance.gameObject.AddComponent<LayoutElement>().minWidth = 36f;

            // The frame's four corners, over everything, out of the layout.
            Image topLeft = Bracket(pane, "Bracket Top Left", new Vector2(0f, 1f), new Vector3(1f, 1f, 1f));
            Image topRight = Bracket(pane, "Bracket Top Right", new Vector2(1f, 1f), new Vector3(-1f, 1f, 1f));
            Image bottomLeft = Bracket(pane, "Bracket Bottom Left", new Vector2(0f, 0f), new Vector3(1f, -1f, 1f));
            Image bottomRight = Bracket(pane, "Bracket Bottom Right", new Vector2(1f, 0f), new Vector3(-1f, -1f, 1f));

            NameplateView view = plate.AddComponent<NameplateView>();
            var viewSo = new SerializedObject(view);
            Assign(viewSo, "_glass", glass);
            Assign(viewSo, "_scanlines", scanlines);
            Assign(viewSo, "_bracketTopLeft", topLeft);
            Assign(viewSo, "_bracketTopRight", topRight);
            Assign(viewSo, "_bracketBottomLeft", bottomLeft);
            Assign(viewSo, "_bracketBottomRight", bottomRight);
            Assign(viewSo, "_emblem", emblem);
            Assign(viewSo, "_name", name);
            Assign(viewSo, "_star", star);
            Assign(viewSo, "_state", state);
            Assign(viewSo, "_weapon", weapon);
            Assign(viewSo, "_weaponSize", weapon.GetComponent<LayoutElement>());
            Assign(viewSo, "_medic", medic);
            Assign(viewSo, "_trail", trail);
            Assign(viewSo, "_fill", fill);
            Assign(viewSo, "_pin", pin);
            Assign(viewSo, "_distance", distance);
            Assign(viewSo, "_pointer", pointer);
            viewSo.ApplyModifiedPropertiesWithoutUndo();
            plate.SetActive(false);

            NameplateLayer layerView = layer.AddComponent<NameplateLayer>();
            var layerSo = new SerializedObject(layerView);
            Assign(layerSo, "_template", view);
            layerSo.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine("name plates: a full-screen layer under the readout and one holo-frame template plate.");
            return layerView;
        }

        private const int PlateNameSize = 20;
        private const float PlateBarWidth = 92f;
        private const float PlateBarHeight = 4f;
        private const float PlatePointerWidth = 14f;
        private const float PlatePointerHeight = 9f;

        /// <summary>A row of a plate's pane: parts side by side, centred on one line.</summary>
        private static GameObject PlateRow(GameObject pane, string name)
        {
            var row = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(pane.transform, worldPositionStays: false);

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return row;
        }

        /// <summary>An image over a pane that its layout leaves alone.</summary>
        private static Image Overlay(GameObject pane, string name)
        {
            Image image = Picture(pane, name, Color.white);
            image.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            return image;
        }

        /// <summary>
        /// One corner of the frame: <see cref="HudSprites.Bracket"/>'s corner pinned to the pane's
        /// corner at <paramref name="anchor"/>, mirrored by <paramref name="flip"/> to face in.
        /// </summary>
        private static Image Bracket(GameObject pane, string name, Vector2 anchor, Vector3 flip)
        {
            Image bracket = Overlay(pane, name);
            RectTransform rect = bracket.rectTransform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(
                HudSprites.BracketCornerX / HudSprites.BracketSize,
                HudSprites.BracketCornerY / HudSprites.BracketSize);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(HudSprites.BracketSize, HudSprites.BracketSize);
            rect.localScale = flip;
            return bracket;
        }

        /// <summary>
        /// A part of a bar's track, stretched over its full height and the first share of its
        /// width; <see cref="NameplateView"/> moves the right edge as health changes.
        /// </summary>
        private static Image BarShare(Image track, string name)
        {
            Image part = Picture(track.gameObject, name, Color.white);
            RectTransform rect = part.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return part;
        }

        /// <summary>3.2 — the deploy screen.</summary>
        private static GameObject BuildDeployScreen(
            GameObject root, out Text killer, out Text timer, out Button deploy, StringBuilder log)
        {
            var panel = new GameObject("Deploy Screen", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(root.transform, worldPositionStays: false);
            Stretch(panel.GetComponent<RectTransform>());

            var backdrop = panel.GetComponent<Image>();
            backdrop.color = Backdrop;

            // Raycast target ON, and that is the point: the overlay swallows clicks meant for the
            // loadout and minimap Canvases underneath it, which are still live while dead.
            backdrop.raycastTarget = true;

            Text heading = Label(panel, "Heading", "YOU WERE KILLED", 56, TextAnchor.MiddleCenter);
            Centre(heading.GetComponent<RectTransform>(), new Vector2(0f, 160f), new Vector2(900f, 80f));

            killer = Label(panel, "Killer", string.Empty, 36, TextAnchor.MiddleCenter);
            Centre(killer.GetComponent<RectTransform>(), new Vector2(0f, 80f), new Vector2(900f, 56f));

            timer = Label(panel, "Timer", string.Empty, 34, TextAnchor.MiddleCenter);
            Centre(timer.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(900f, 52f));

            deploy = MakeButton(panel, "Deploy", "DEPLOY", new Vector2(0f, -100f), new Vector2(400f, 84f));

            log.AppendLine("deploy screen: heading, killer, countdown and one button.");
            return panel;
        }

        /// <summary>
        /// P18 3.3, rebuilt for feature 2 (the owner's request, 2026-09-29) — the Tab scoreboard:
        /// the match across the top, both sides below, the rules at the foot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A row per player, cloned from one template per side.</b> The old board was two
        /// multi-line labels a side, which cannot give a row its own backing, a star, a BOT tag
        /// or a light when that player scores. <see cref="ScoreboardTeamView"/> clones the
        /// template, so the prefab still carries one row a side rather than thirty-two.
        /// </para>
        /// <para>
        /// <b>Authored in the neutral ink, like every other element here.</b> The side colours are
        /// <c>ITeamPalette</c>'s and are written at runtime; a red and a blue baked in would be
        /// the second copy of a mapping the game already owns, which is what
        /// <c>MatchHudTeamColoursComeFromThePalette</c> forbids (contracts § 6.3).
        /// </para>
        /// <para>
        /// <b>Authored hidden and transparent.</b> The board fades in when it opens; authored
        /// visible it would sit over the offline game, which never runs the component that hides it.
        /// </para>
        /// </remarks>
        private static ScoreboardView BuildScoreboard(GameObject root, StringBuilder log)
        {
            Font black = LoadFont(RobotoBlackPath);
            Font bold = LoadFont(RobotoBoldPath);
            Font medium = LoadFont(RobotoMediumPath);
            Sprite rounded = RoundedSprite();

            var board = new GameObject(
                "Scoreboard", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            board.transform.SetParent(root.transform, worldPositionStays: false);
            Stretch(board.GetComponent<RectTransform>());

            var group = board.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            // Raycast target OFF, unlike the deploy screen's. This board comes up while the
            // player is alive and still shooting; swallowing their clicks would be a scoreboard
            // that disarms them.
            var vignette = board.GetComponent<Image>();
            vignette.color = new Color(0.01f, 0.015f, 0.03f, 1f);
            vignette.raycastTarget = false;

            var panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(board.transform, worldPositionStays: false);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            Centre(panelRect, Vector2.zero, new Vector2(BoardWidth, BoardHeight));

            // ---- the match, across the top
            Text map = BoardText(panel, "Map", black, 46, Ink, TextAnchor.UpperLeft);
            Place(map, TopLeft, TopLeft, new Vector2(8f, -14f), new Vector2(620f, 58f));

            Text summary = BoardText(panel, "Summary", medium, 18, HudStyle.BoardMuted, TextAnchor.UpperLeft);
            Place(summary, TopLeft, TopLeft, new Vector2(10f, -74f), new Vector2(620f, 26f));

            Text team0Label = BoardText(panel, "Team 0 Label", bold, 18, Ink, TextAnchor.UpperRight);
            Place(team0Label, TopCentre, TopRight, new Vector2(-40f, -12f), new Vector2(260f, 26f));

            Text score0 = BoardText(panel, "Score 0", black, 72, Ink, TextAnchor.UpperRight);
            Place(score0, TopCentre, TopRight, new Vector2(-40f, -32f), new Vector2(320f, 86f));
            Outline glow0 = Glow(score0);

            Text team1Label = BoardText(panel, "Team 1 Label", bold, 18, Ink, TextAnchor.UpperLeft);
            Place(team1Label, TopCentre, TopLeft, new Vector2(40f, -12f), new Vector2(260f, 26f));

            Text score1 = BoardText(panel, "Score 1", black, 72, Ink, TextAnchor.UpperLeft);
            Place(score1, TopCentre, TopLeft, new Vector2(40f, -32f), new Vector2(320f, 86f));
            Outline glow1 = Glow(score1);

            Image versus = Picture(panel, "Versus", new Color(1f, 1f, 1f, 0.22f));
            Place(versus, TopCentre, TopCentre, new Vector2(0f, -40f), new Vector2(2f, 66f));

            Image track = Picture(panel, "Lead Track", new Color(1f, 1f, 1f, 0.1f), rounded);
            Place(track, TopCentre, TopCentre, new Vector2(0f, -128f), new Vector2(600f, 10f));

            Image fill0 = Picture(track.gameObject, "Lead Fill 0", Ink, rounded);
            Place(fill0, Middle, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(0f, 10f));

            Image fill1 = Picture(track.gameObject, "Lead Fill 1", Ink, rounded);
            Place(fill1, Middle, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(0f, 10f));

            Image centreMark = Picture(track.gameObject, "Lead Centre", new Color(1f, 1f, 1f, 0.85f));
            Place(centreMark, Middle, Middle, Vector2.zero, new Vector2(2f, 20f));

            Text leadLine = BoardText(panel, "Lead Line", bold, 18, HudStyle.BoardMuted, TextAnchor.UpperCenter);
            Place(leadLine, TopCentre, TopCentre, new Vector2(0f, -146f), new Vector2(1000f, 26f));

            Text clock = BoardText(panel, "Clock", black, 46, Ink, TextAnchor.UpperRight);
            Place(clock, TopRight, TopRight, new Vector2(-8f, -14f), new Vector2(320f, 58f));

            Text phase = BoardText(panel, "Phase", bold, 20, HudStyle.BoardMuted, TextAnchor.UpperRight);
            Place(phase, TopRight, TopRight, new Vector2(-10f, -74f), new Vector2(320f, 26f));

            Image divider = Picture(panel, "Divider", new Color(1f, 1f, 1f, 0.28f));
            Place(divider, TopCentre, TopCentre, new Vector2(0f, -180f), new Vector2(BoardWidth, 2f));

            // ---- both sides
            ScoreboardTeamView team0 = BuildScoreboardSide(panel, "Team 0", false, black, bold, medium, rounded);
            Place(team0, TopCentre, TopRight, new Vector2(-SideGap * 0.5f, -SideTop), new Vector2(SideWidth, SideHeight));

            ScoreboardTeamView team1 = BuildScoreboardSide(panel, "Team 1", true, black, bold, medium, rounded);
            Place(team1, TopCentre, TopLeft, new Vector2(SideGap * 0.5f, -SideTop), new Vector2(SideWidth, SideHeight));

            // ---- the rules, at the foot
            Text rules = BoardText(panel, "Rules", medium, 17, HudStyle.BoardFaint, TextAnchor.LowerCenter);
            Place(rules, BottomCentre, BottomCentre, new Vector2(0f, 8f), new Vector2(BoardWidth - 360f, 26f));

            Text hint = BoardText(panel, "Hint", bold, 15, HudStyle.BoardFaint, TextAnchor.LowerRight);
            hint.text = "TAB  ·  CLOSE";
            Place(hint, BottomRight, BottomRight, new Vector2(-8f, 10f), new Vector2(200f, 20f));

            ScoreboardView view = board.AddComponent<ScoreboardView>();

            var so = new SerializedObject(view);
            Assign(so, "_group", group);
            Assign(so, "_vignette", vignette);
            Assign(so, "_panel", panelRect);
            Assign(so, "_map", map);
            Assign(so, "_summary", summary);
            Assign(so, "_team0Label", team0Label);
            Assign(so, "_team1Label", team1Label);
            Assign(so, "_score0", score0);
            Assign(so, "_score1", score1);
            Assign(so, "_score0Glow", glow0);
            Assign(so, "_score1Glow", glow1);
            Assign(so, "_leadTrack", track.rectTransform);
            Assign(so, "_leadFill0", fill0);
            Assign(so, "_leadFill1", fill1);
            Assign(so, "_leadLine", leadLine);
            Assign(so, "_clock", clock);
            Assign(so, "_phase", phase);
            Assign(so, "_divider", divider);
            Assign(so, "_team0", team0);
            Assign(so, "_team1", team1);
            Assign(so, "_rules", rules);
            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine(
                "scoreboard: match header, two sides of up to " + MatchHud.ScoreboardRowsPerTeam
                + " cloned rows (from ProtocolConstants.MAX_ACTORS), rules line; authored hidden.");

            return view;
        }

        /// <summary>One side: its band, name, head count, flags, totals, column heads and rows.</summary>
        private static ScoreboardTeamView BuildScoreboardSide(
            GameObject panel, string name, bool mirrored, Font black, Font bold, Font medium, Sprite rounded)
        {
            var side = new GameObject(name, typeof(RectTransform), typeof(Image));
            side.transform.SetParent(panel.transform, worldPositionStays: false);

            var pane = side.GetComponent<Image>();
            pane.sprite = rounded;
            pane.type = Image.Type.Sliced;
            pane.color = new Color(HudStyle.Pane.r, HudStyle.Pane.g, HudStyle.Pane.b, 0.96f);
            pane.raycastTarget = false;

            // The side's colour is strongest at the board's outer edge: the right-hand side's band
            // is the same fade, mirrored.
            Image band = Picture(side, "Band", Ink);
            RectTransform bandRect = band.rectTransform;
            bandRect.anchorMin = new Vector2(0f, 1f);
            bandRect.anchorMax = new Vector2(1f, 1f);
            bandRect.pivot = new Vector2(0.5f, 1f);
            bandRect.anchoredPosition = Vector2.zero;
            bandRect.sizeDelta = new Vector2(0f, BandHeight);
            if (mirrored) bandRect.localScale = new Vector3(-1f, 1f, 1f);

            Text teamName = BoardText(side, "Name", black, 36, Ink, TextAnchor.UpperLeft);
            Place(teamName, TopLeft, TopLeft, new Vector2(24f, -10f), new Vector2(400f, 46f));

            Text players = BoardText(side, "Players", bold, 17, Ink, TextAnchor.UpperLeft);
            Place(players, TopLeft, TopLeft, new Vector2(26f, -60f), new Vector2(480f, 24f));

            Image flagIcon = Picture(side, "Flag Icon", Ink);
            Place(flagIcon, TopRight, TopRight, new Vector2(-74f, -16f), new Vector2(32f, 32f));

            Text flags = BoardText(side, "Flags", black, 36, Ink, TextAnchor.UpperRight);
            Place(flags, TopRight, TopRight, new Vector2(-22f, -8f), new Vector2(50f, 46f));

            // What one enemy death is worth to this side now: its flag count, spelled as the rule.
            Text perKill = BoardText(side, "Per Kill", bold, 20, Ink, TextAnchor.UpperRight);
            Place(perKill, TopRight, TopRight, new Vector2(-118f, -17f), new Vector2(260f, 28f));

            Text totals = BoardText(side, "Totals", bold, 17, Ink, TextAnchor.UpperRight);
            Place(totals, TopRight, TopRight, new Vector2(-24f, -60f), new Vector2(420f, 24f));

            // Column heads, on the same insets as the rows so each sits over its numbers.
            var heads = new GameObject("Columns", typeof(RectTransform));
            heads.transform.SetParent(side.transform, worldPositionStays: false);
            RectTransform headsRect = heads.GetComponent<RectTransform>();
            headsRect.anchorMin = new Vector2(0f, 1f);
            headsRect.anchorMax = new Vector2(1f, 1f);
            headsRect.pivot = new Vector2(0.5f, 1f);
            headsRect.anchoredPosition = new Vector2(0f, -(BandHeight + 8f));
            headsRect.sizeDelta = new Vector2(-2f * RowInset, 28f);

            ColumnHead(heads, "#", bold, TextAnchor.MiddleCenter, fromRight: false, RankX, RankWidth);
            ColumnHead(heads, "PLAYER", bold, TextAnchor.MiddleLeft, fromRight: false, NameX, 300f);
            ColumnHead(heads, "K", bold, TextAnchor.MiddleRight, fromRight: true, KillsX, NumberWidth);
            ColumnHead(heads, "D", bold, TextAnchor.MiddleRight, fromRight: true, DeathsX, NumberWidth);
            ColumnHead(heads, "K/D", bold, TextAnchor.MiddleRight, fromRight: true, RatioX, RatioWidth);

            Image rule = Picture(side, "Column Rule", new Color(1f, 1f, 1f, 0.12f));
            RectTransform ruleRect = rule.rectTransform;
            ruleRect.anchorMin = new Vector2(0f, 1f);
            ruleRect.anchorMax = new Vector2(1f, 1f);
            ruleRect.pivot = new Vector2(0.5f, 1f);
            ruleRect.anchoredPosition = new Vector2(0f, -(BandHeight + 38f));
            ruleRect.sizeDelta = new Vector2(-2f * RowInset, 1f);

            var rows = new GameObject("Rows", typeof(RectTransform));
            rows.transform.SetParent(side.transform, worldPositionStays: false);
            RectTransform rowsRect = rows.GetComponent<RectTransform>();
            rowsRect.anchorMin = Vector2.zero;
            rowsRect.anchorMax = Vector2.one;
            rowsRect.offsetMin = new Vector2(RowInset, RowInset);
            rowsRect.offsetMax = new Vector2(-RowInset, -(BandHeight + 44f));

            Text empty = BoardText(rows, "Empty", medium, 18, HudStyle.BoardFaint, TextAnchor.MiddleCenter);
            empty.text = "NO PLAYERS YET";
            Stretch(empty.rectTransform);

            ScoreboardRowView template = BuildScoreboardRow(rows, bold, medium, rounded);

            ScoreboardTeamView view = side.AddComponent<ScoreboardTeamView>();

            var so = new SerializedObject(view);
            Assign(so, "_band", band);
            Assign(so, "_teamName", teamName);
            Assign(so, "_flagIcon", flagIcon);
            Assign(so, "_flags", flags);
            Assign(so, "_perKill", perKill);
            Assign(so, "_players", players);
            Assign(so, "_totals", totals);
            Assign(so, "_rows", rowsRect);
            Assign(so, "_empty", empty);
            Assign(so, "_rowTemplate", template);
            so.ApplyModifiedPropertiesWithoutUndo();

            return view;
        }

        /// <summary>The one row a side's column clones: rank, star, name, K, D, K/D. A bot's name says it is one.</summary>
        private static ScoreboardRowView BuildScoreboardRow(
            GameObject rows, Font bold, Font medium, Sprite rounded)
        {
            var row = new GameObject(
                "Row Template", typeof(RectTransform), typeof(CanvasGroup), typeof(Image), typeof(Outline));
            row.transform.SetParent(rows.transform, worldPositionStays: false);

            RectTransform rect = row.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0f, 28f);

            var group = row.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var backing = row.GetComponent<Image>();
            backing.sprite = rounded;
            backing.type = Image.Type.Sliced;
            backing.color = new Color(1f, 1f, 1f, 0.05f);
            backing.raycastTarget = false;

            var edge = row.GetComponent<Outline>();
            edge.effectDistance = new Vector2(1.5f, -1.5f);
            edge.enabled = false;

            Text rank = BoardText(row, "Rank", medium, 16, HudStyle.BoardFaint, TextAnchor.MiddleCenter, shadowed: false);
            Column(rank, fromRight: false, RankX, RankWidth);

            Image star = Picture(row, "Star", HudStyle.Gold);
            Place(star, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(StarX, 0f), new Vector2(18f, 18f));
            star.preserveAspect = true;

            var identity = new GameObject("Identity", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            identity.transform.SetParent(row.transform, worldPositionStays: false);
            RectTransform identityRect = identity.GetComponent<RectTransform>();
            identityRect.anchorMin = Vector2.zero;
            identityRect.anchorMax = Vector2.one;
            identityRect.offsetMin = new Vector2(NameX, 0f);
            identityRect.offsetMax = new Vector2(-(KillsX + NumberWidth + 16f), 0f);

            var identityLayout = identity.GetComponent<HorizontalLayoutGroup>();
            identityLayout.childAlignment = TextAnchor.MiddleLeft;
            identityLayout.spacing = 8f;
            identityLayout.childControlWidth = true;
            identityLayout.childControlHeight = true;
            identityLayout.childForceExpandWidth = false;
            identityLayout.childForceExpandHeight = false;

            Text name = BoardText(identity, "Name", bold, 18, Ink, TextAnchor.MiddleLeft);

            Text kills = BoardText(row, "Kills", bold, 18, Ink, TextAnchor.MiddleRight);
            Column(kills, fromRight: true, KillsX, NumberWidth);

            Text deaths = BoardText(row, "Deaths", bold, 18, HudStyle.BoardMuted, TextAnchor.MiddleRight);
            Column(deaths, fromRight: true, DeathsX, NumberWidth);

            Text ratio = BoardText(row, "Ratio", medium, 16, HudStyle.BoardFaint, TextAnchor.MiddleRight, shadowed: false);
            Column(ratio, fromRight: true, RatioX, RatioWidth);

            ScoreboardRowView view = row.AddComponent<ScoreboardRowView>();

            var so = new SerializedObject(view);
            Assign(so, "_backing", backing);
            Assign(so, "_edge", edge);
            Assign(so, "_rank", rank);
            Assign(so, "_star", star);
            Assign(so, "_name", name);
            Assign(so, "_kills", kills);
            Assign(so, "_deaths", deaths);
            Assign(so, "_ratio", ratio);
            so.ApplyModifiedPropertiesWithoutUndo();

            row.SetActive(false);
            return view;
        }

        // Nearly the whole 1920x1080 reference: the board is a full-screen read, and its rows are
        // what has to be legible (owner report 2026-09-29: "HUD GUI bé và khó nhìn").
        /// <summary>Canvas units from the top edge to the killfeed's first row: clear of the score bar.</summary>
        private const float KillfeedTop = 190f;

        private const float BoardWidth = 1760f;
        private const float BoardHeight = 1040f;
        private const float SideGap = 28f;
        private const float SideWidth = (BoardWidth - SideGap) * 0.5f;
        private const float SideTop = 194f;
        private const float SideHeight = BoardHeight - SideTop - 44f;
        private const float BandHeight = 96f;
        private const float RowInset = 12f;

        // Columns, from a row's left edge or (fromRight) its right edge.
        private const float RankX = 0f;
        private const float RankWidth = 40f;
        private const float StarX = 44f;
        private const float NameX = 68f;
        private const float KillsX = 190f;
        private const float DeathsX = 110f;
        private const float RatioX = 12f;
        private const float NumberWidth = 60f;
        private const float RatioWidth = 80f;

        private static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        private static readonly Vector2 TopCentre = new Vector2(0.5f, 1f);
        private static readonly Vector2 TopRight = new Vector2(1f, 1f);
        private static readonly Vector2 Middle = new Vector2(0.5f, 0.5f);
        private static readonly Vector2 BottomCentre = new Vector2(0.5f, 0f);
        private static readonly Vector2 BottomRight = new Vector2(1f, 0f);

        private static void ColumnHead(
            GameObject heads, string caption, Font font, TextAnchor anchor, bool fromRight, float x, float width)
        {
            Text head = BoardText(heads, caption, font, 15, HudStyle.BoardFaint, anchor, shadowed: false);
            head.text = caption;
            Column(head, fromRight, x, width);
        }

        /// <summary>A full-height column in a row, <paramref name="x"/> in from one edge.</summary>
        private static void Column(Component part, bool fromRight, float x, float width)
        {
            var rect = (RectTransform)part.transform;
            rect.anchorMin = new Vector2(fromRight ? 1f : 0f, 0f);
            rect.anchorMax = new Vector2(fromRight ? 1f : 0f, 1f);
            rect.pivot = new Vector2(fromRight ? 1f : 0f, 0.5f);
            rect.anchoredPosition = new Vector2(fromRight ? -x : x, 0f);
            rect.sizeDelta = new Vector2(width, 0f);
        }

        /// <summary>Places a part by one anchor point, its pivot, an offset and a size.</summary>
        private static void Place(Component part, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            var rect = (RectTransform)part.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        /// <summary>An image part, optionally a nine-sliced one.</summary>
        private static Image Picture(GameObject parent, string name, Color colour, Sprite sliced = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            var image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;

            if (sliced != null)
            {
                image.sprite = sliced;
                image.type = Image.Type.Sliced;
            }

            return image;
        }

        /// <summary>
        /// The breathing edge on a leading score. Authored off; the board turns it on for the
        /// side ahead and paints it that side's colour.
        /// </summary>
        private static Outline Glow(Text score)
        {
            var glow = score.gameObject.AddComponent<Outline>();
            glow.effectDistance = new Vector2(1.6f, -1.6f);
            glow.useGraphicAlpha = true;
            glow.enabled = false;
            return glow;
        }

        private const string RobotoBlackPath = "Assets/Font/Roboto-Black.ttf";

        // ------------------------------------------------------------------ helpers

        private static Text Label(
            GameObject parent, string name, string content, int size, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            Text text = go.AddComponent<Text>();
            text.font = DefaultFont();
            text.fontSize = size;
            text.text = content;
            text.color = Ink;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            return text;
        }

        private static Button MakeButton(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            var background = go.GetComponent<Image>();
            background.color = new Color(0.16f, 0.19f, 0.24f, 1f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = background;

            Text text = Label(go, "Text", caption, 34, TextAnchor.MiddleCenter);
            Stretch(text.GetComponent<RectTransform>());

            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Centre(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        /// <summary>
        /// The built-in font every legacy <c>Text</c> in this project already uses.
        /// </summary>
        /// <remarks>
        /// <c>LegacyRuntime.ttf</c> is where Unity moved Arial. A null font renders nothing at all
        /// — no error, no warning, an empty rect — which on a screenshot-graded phase reads as an
        /// unassigned label and sends the reader after the wrong fault.
        /// </remarks>
        private static Font DefaultFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static void Assign(SerializedObject so, string field, Object value)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + " has no serialized field '" + field
                    + "'. The builder and the component have drifted; fix the builder rather "
                    + "than assigning by hand.");

            property.objectReferenceValue = value;
        }

        /// <summary>
        /// Assigns a serialized array of object references, resizing it to match.
        /// </summary>
        /// <remarks>
        /// Resized rather than assumed, for <c>BuildMenuCanvas.AssignArray</c>'s reason: a
        /// component added to a REBUILT object deserializes whatever the previous authoring held,
        /// so setting the size here is what keeps the gate's per-entry check pointed at exactly
        /// the rows this run created.
        /// </remarks>
        private static void AssignArray(SerializedObject so, string field, Object[] values)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + " has no serialized field '" + field
                    + "'. The builder and the component have drifted; fix the builder rather "
                    + "than assigning by hand.");

            if (!property.isArray)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + "." + field + " is not an array, so the "
                    + "builder is assigning it the wrong way round.");

            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }
}
