using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One killfeed row: its parts, and how it arrives, moves down and leaves. Playtest
    /// 2026-09-28, feature 2; rebuilt for the owner's report of 2026-09-30 (more icons, better
    /// effects, every kind of match event).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Authored by <c>BuildMatchHud</c>, animated here.</b> The builder lays the row out once
    /// -- a dark rounded backing sized to its content, a side-coloured accent, a leading picture,
    /// the killer, a verb, the weapon or the vehicle, a chip saying how, the headshot and
    /// long-shot marks, the victim, a sentence and a badge -- and this component fills it and
    /// moves it. Three shapes use those parts: a kill (killer, how, victim, badge), a death
    /// nobody scored (picture, victim, sentence: "Minh drowned"), and a match event (picture,
    /// subject, verb, object: "BLUE TEAM captured FORTRESS").
    /// </para>
    /// <para>
    /// <b>Keyed on the line, not the slot.</b> <see cref="MatchHud"/> hands the same line back to
    /// the same row as newer lines push it down, so a row slides in once, glides to each new slot
    /// and fades out when the model drops it -- instead of every row re-typing itself on each kill.
    /// </para>
    /// <para>
    /// <b>The motion.</b> A row springs in from the right with a slight overshoot and a flash, a
    /// band of light sweeps across it, its badge pops and glows, a thin bar along its foot counts
    /// down the time it has left, and a line that names you pulses its gold or red edge. All in
    /// unscaled time: the feed reports what already happened rather than taking part in it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class KillfeedRowView : MonoBehaviour
    {
        /// <summary>A row's height at the 1920x1080 reference resolution.</summary>
        public const float RowHeight = 40f;

        /// <summary>From one row's top to the next one's.</summary>
        public const float RowPitch = RowHeight + 6f;

        /// <summary>The weapon picture's height, and the widest a long rifle may draw.</summary>
        public const float WeaponIconHeight = 30f;
        private const float WeaponIconMaxWidth = 100f;

        /// <summary>A vehicle's or a cause's picture between the names.</summary>
        public const float GlyphHeight = 30f;

        private const float SlideDistance = 90f;
        private const float ArriveSeconds = 0.34f;
        private const float LeaveSeconds = 0.42f;
        private const float FlashSeconds = 0.7f;
        private const float SheenSeconds = 0.6f;
        private const float BadgePopSeconds = 0.42f;
        private const float BadgeGlowSeconds = 2.2f;
        private const float EdgePulseSeconds = 2.4f;

        /// <summary>Per second: how fast a pushed-down row closes on its new slot.</summary>
        private const float SettleRate = 14f;

        /// <summary>Shown in the chip when the server named nothing at all (a 1.0 server).</summary>
        private const string NoLabel = "›";

        [SerializeField] private Image _backing;
        [SerializeField] private Outline _edge;
        [SerializeField] private Image _accent;
        [SerializeField] private Image _lead;
        [SerializeField] private Text _killer;
        [SerializeField] private Text _verb;
        [SerializeField] private Image _weapon;
        [SerializeField] private LayoutElement _weaponSize;
        [SerializeField] private Image _glyph;
        [SerializeField] private LayoutElement _glyphSize;
        [SerializeField] private GameObject _how;
        [SerializeField] private Text _howText;
        [SerializeField] private Image _headshot;
        [SerializeField] private Image _longShot;
        [SerializeField] private Text _distance;
        [SerializeField] private Text _victim;
        [SerializeField] private Text _sentence;
        [SerializeField] private GameObject _badge;
        [SerializeField] private Image _badgeBacking;
        [SerializeField] private Outline _badgeGlow;
        [SerializeField] private Image _badgeIcon;
        [SerializeField] private Text _badgeText;
        [SerializeField] private Image _sheen;
        [SerializeField] private Image _timer;

        private RectTransform _rect;
        private CanvasGroup _group;
        private RectTransform _badgeRect;
        private RectTransform _sheenRect;
        private RectTransform _timerRect;

        private float _y;
        private float _slotY;
        private float _age;
        private float _leaveAge = -1f;
        private float _hold;
        private Color _restBacking = HudStyle.RowBacking;
        private Color _edgeColour = HudStyle.Gold;
        private Color _badgeColour = HudStyle.Gold;
        private bool _flashSettled = true;
        private bool _hasBadge;
        private bool _pulseEdge;

        /// <summary>The line this row shows, 0 when it shows none.</summary>
        public long Sequence { get; private set; }

        public bool IsFree => Sequence == 0;

        public bool IsLeaving => _leaveAge >= 0f;

        /// <summary>How visible the row is now, 0 to 1.</summary>
        public float Opacity => _group != null ? _group.alpha : 0f;

        private void Awake()
        {
            _rect = (RectTransform)transform;
            _group = GetComponent<CanvasGroup>();

            if (_backing == null || _edge == null || _accent == null || _lead == null
                || _killer == null || _verb == null || _weapon == null || _weaponSize == null
                || _glyph == null || _glyphSize == null || _how == null || _howText == null
                || _headshot == null || _longShot == null || _distance == null || _victim == null
                || _sentence == null || _badge == null || _badgeBacking == null || _badgeGlow == null
                || _badgeIcon == null || _badgeText == null || _sheen == null || _timer == null
                || _group == null)
            {
                // The builder authors all of these. A missing one is a stale prefab, and a row
                // drawing half a line would read as a wording bug rather than a wiring one.
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the killfeed rows.", this);
                enabled = false;
                return;
            }

            _badgeRect = (RectTransform)_badge.transform;
            _sheenRect = (RectTransform)_sheen.transform;
            _timerRect = (RectTransform)_timer.transform;

            _headshot.sprite = HudSprites.Crosshair();
            _headshot.color = HudStyle.HeadshotInk;
            _longShot.sprite = HudSprites.Range();
            _longShot.color = HudStyle.LongShotInk;
            _sheen.sprite = HudSprites.Sheen();
        }

        /// <summary>
        /// Shows <paramref name="line"/> in <paramref name="slot"/>. A new line slides in after
        /// <paramref name="arrivalDelay"/> seconds; the line this row already shows only moves to
        /// its slot and takes any new names.
        /// </summary>
        public void Show(in KillfeedLine line, int slot, Color killerInk, Color victimInk, float arrivalDelay = 0f)
        {
            _slotY = -slot * RowPitch;

            bool arriving = line.Sequence != Sequence;
            if (arriving)
            {
                Sequence = line.Sequence;
                _age = -Mathf.Max(0f, arrivalDelay);
                _y = _slotY;
                _flashSettled = false;

                // Rows are authored inactive, so the first activation is also what runs Awake.
                gameObject.SetActive(true);
            }

            // Awake found a part missing and said so; the row stays invisible rather than
            // drawing half a line.
            if (!enabled) return;

            _leaveAge = -1f;
            _hold = line.HoldSeconds;

            Fill(in line, killerInk, victimInk);

            if (arriving)
            {
                if (_group != null) _group.alpha = 0f;
                _sheen.gameObject.SetActive(true);
                SetSheen(0f);
            }

            if (_flashSettled) _backing.color = _restBacking;
        }

        /// <summary>Puts the line's words, pictures and colours into the row's parts.</summary>
        private void Fill(in KillfeedLine line, Color killerInk, Color victimInk)
        {
            bool sentence = line.IsSentence;
            bool isEvent = line.IsEvent;
            bool kill = !sentence && !isEvent;
            var glyph = (KillfeedGlyph)line.Glyph;

            // The weapon's own picture stands in for its name when there is one, and the chip keeps
            // only what the picture cannot say ("MELEE"). Otherwise a drawn picture of the vehicle
            // or the cause does the same, and with neither the chip says it all.
            Sprite weaponPicture = kill && line.WeaponId != 0 && NetClientBindings.WeaponIcon != null
                ? NetClientBindings.WeaponIcon(line.WeaponId)
                : null;
            Sprite between = kill && weaponPicture == null ? HudSprites.Glyph(glyph) : null;
            Sprite lead = !kill ? HudSprites.Glyph(glyph) : null;

            string chip = weaponPicture != null ? line.RestAfterWeapon
                : between != null ? line.RestAfterGlyph
                : line.Label.Length > 0 ? line.Label
                : NoLabel;

            _lead.gameObject.SetActive(lead != null);
            _killer.gameObject.SetActive(line.KillerName.Length > 0 && !sentence);
            _verb.gameObject.SetActive(isEvent && line.Verb.Length > 0);
            _weapon.gameObject.SetActive(weaponPicture != null);
            _glyph.gameObject.SetActive(between != null);
            _how.SetActive(kill && chip.Length > 0);
            _headshot.gameObject.SetActive(kill && line.Headshot);
            _longShot.gameObject.SetActive(kill && line.Distance.Length > 0);
            _distance.gameObject.SetActive(kill && line.Distance.Length > 0);
            _victim.gameObject.SetActive(line.VictimName.Length > 0);
            _sentence.gameObject.SetActive(line.Sentence.Length > 0);

            if (weaponPicture != null)
                HudStyle.FitPicture(_weapon, _weaponSize, weaponPicture, WeaponIconHeight, WeaponIconMaxWidth);
            if (between != null)
                HudStyle.FitPicture(_glyph, _glyphSize, between, GlyphHeight, GlyphHeight * 2f);
            if (lead != null)
            {
                _lead.sprite = lead;
                _lead.color = LeadInk(glyph, sentence ? victimInk : killerInk);
            }

            _glyph.color = HudStyle.Ink;
            _killer.text = line.KillerName;
            _killer.color = killerInk;
            _verb.text = line.Verb;
            _howText.text = chip;
            _distance.text = line.Distance;
            _victim.text = line.VictimName;
            _victim.color = victimInk;
            _sentence.text = line.Sentence;
            _sentence.color = isEvent ? HudStyle.Ink : HudStyle.SentenceInk;

            // The side that acted: the killer's for a kill, the victim's for a death nobody scored,
            // the subject's for an event -- a gold one for an event about nobody's side.
            _accent.color = kill ? killerInk
                : sentence && !isEvent ? victimInk
                : line.KillerTeam == 0 || line.KillerTeam == 1 ? killerInk
                : HudStyle.Gold;

            FillBadge(in line);

            _restBacking = line.LocalIsKiller ? HudStyle.LocalKillBacking
                : line.LocalIsVictim ? HudStyle.LocalDeathBacking
                : isEvent ? HudStyle.EventBacking
                : HudStyle.RowBacking;

            _pulseEdge = line.LocalIsKiller || line.LocalIsVictim;
            _edgeColour = line.LocalIsKiller ? HudStyle.Gold : HudStyle.Blood;
            if (!_pulseEdge && _hasBadge)
            {
                // A badge lights the row's edge in its own colour, fainter than your own lines.
                _pulseEdge = true;
                _edgeColour = new Color(_badgeColour.r, _badgeColour.g, _badgeColour.b, 0.55f);
            }

            _edge.enabled = _pulseEdge;
            _edge.effectColor = _edgeColour;

            _timer.gameObject.SetActive(_hold > 0f);
            _timer.color = new Color(_accent.color.r, _accent.color.g, _accent.color.b, 0.55f);
        }

        /// <summary>The badge: its words, its tone's colour and picture, or nothing.</summary>
        private void FillBadge(in KillfeedLine line)
        {
            _hasBadge = line.Badge.Length > 0 && !line.IsEvent && !line.IsSentence;
            _badge.SetActive(_hasBadge);
            if (!_hasBadge) return;

            var tone = (KillfeedTone)line.BadgeTone;
            _badgeColour = HudStyle.ToneColour(tone);

            _badgeText.text = line.Badge;
            _badgeText.color = HudStyle.BadgeInk;
            _badgeBacking.color = new Color(_badgeColour.r, _badgeColour.g, _badgeColour.b, 0.86f);
            _badgeIcon.sprite = HudSprites.ToneIcon(tone);
            _badgeIcon.color = HudStyle.BadgeInk;
            _badgeGlow.effectColor = new Color(_badgeColour.r, _badgeColour.g, _badgeColour.b, 0f);
        }

        /// <summary>A leading picture's colour: the side's, or the cause's own for water and blood.</summary>
        private static Color LeadInk(KillfeedGlyph glyph, Color side)
        {
            switch (glyph)
            {
                case KillfeedGlyph.Drowned:   return HudStyle.WaterInk;
                case KillfeedGlyph.Explosion: return HudStyle.BlastInk;
                case KillfeedGlyph.Trophy:    return HudStyle.GoldInk;
                case KillfeedGlyph.Overflow:  return HudStyle.Muted;
                case KillfeedGlyph.Fall:
                case KillfeedGlyph.Skull:     return HudStyle.Ink;
                default:                      return side;
            }
        }

        /// <summary>Starts fading this row out. Its line has left the model.</summary>
        public void Leave()
        {
            if (IsFree || IsLeaving) return;
            _leaveAge = 0f;
        }

        /// <summary>Frees the row at once, with no fade. For a HUD being reset.</summary>
        public void Release()
        {
            Sequence = 0;
            _leaveAge = -1f;
            if (_group != null) _group.alpha = 0f;
            gameObject.SetActive(false);
        }

        /// <summary>Advances the row's motion. Called once a frame by <see cref="MatchHud"/>.</summary>
        public void Tick(float deltaSeconds)
        {
            if (IsFree || !enabled) return;

            _age += deltaSeconds;
            _y = Mathf.Lerp(_y, _slotY, 1f - Mathf.Exp(-SettleRate * deltaSeconds));

            // Waiting out a stagger: the row is placed but not yet shown.
            if (_age < 0f)
            {
                _group.alpha = 0f;
                _rect.anchoredPosition = new Vector2(SlideDistance, _y);
                return;
            }

            float arrive = HudStyle.EaseOutBack(_age / ArriveSeconds);
            float x = (1f - arrive) * SlideDistance;
            float alpha = Mathf.Clamp01(_age / (ArriveSeconds * 0.6f));

            if (IsLeaving)
            {
                _leaveAge += deltaSeconds;
                float leave = Mathf.Clamp01(_leaveAge / LeaveSeconds);

                if (leave >= 1f)
                {
                    Release();
                    return;
                }

                alpha *= 1f - leave;
                x += leave * leave * SlideDistance * 0.5f;
            }

            _rect.anchoredPosition = new Vector2(x, _y);
            _group.alpha = alpha;

            TickSheen();
            TickBadge();
            TickEdge();
            TickTimer();

            if (_flashSettled) return;

            // A short light on arrival, so a new line catches the eye without a sound.
            float flash = 1f - Mathf.Clamp01(_age / FlashSeconds);
            Color lit = Color.Lerp(_restBacking, new Color(1f, 1f, 1f, 0.92f), 0.32f);
            _backing.color = Color.Lerp(_restBacking, lit, flash * flash);
            _flashSettled = flash <= 0f;
        }

        /// <summary>A band of light crosses the row once, just after it arrives.</summary>
        private void TickSheen()
        {
            if (!_sheen.gameObject.activeSelf) return;

            float t = _age / SheenSeconds;
            if (t >= 1f)
            {
                _sheen.gameObject.SetActive(false);
                return;
            }

            SetSheen(t);
        }

        private void SetSheen(float t)
        {
            float width = _rect.rect.width;
            float band = _sheenRect.rect.width;
            _sheenRect.anchoredPosition = new Vector2(Mathf.Lerp(-band, width, HudStyle.EaseOut(t)), 0f);
            _sheen.color = new Color(1f, 1f, 1f, 0.34f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t)));
        }

        /// <summary>The badge springs up from larger than life, then glows in its colour.</summary>
        private void TickBadge()
        {
            if (!_hasBadge) return;

            float pop = HudStyle.EaseOutBack(_age / BadgePopSeconds);
            float scale = Mathf.Lerp(1.6f, 1f, pop);
            _badgeRect.localScale = new Vector3(scale, scale, 1f);

            float glow = _age < BadgeGlowSeconds
                ? 0.35f + 0.5f * (0.5f + 0.5f * Mathf.Sin(_age * Mathf.PI * 4f)) * (1f - _age / BadgeGlowSeconds)
                : 0.35f;
            _badgeGlow.effectColor = new Color(_badgeColour.r, _badgeColour.g, _badgeColour.b, glow);
        }

        /// <summary>A line that names you, or wears a badge, pulses its edge for a moment.</summary>
        private void TickEdge()
        {
            if (!_pulseEdge) return;

            float fade = Mathf.Clamp01(1f - _age / EdgePulseSeconds);
            float wave = 0.5f + 0.5f * Mathf.Sin(_age * Mathf.PI * 3f);
            float alpha = _edgeColour.a * (0.55f + 0.45f * Mathf.Lerp(1f, wave, fade));
            _edge.effectColor = new Color(_edgeColour.r, _edgeColour.g, _edgeColour.b, alpha);
        }

        /// <summary>The bar along the foot of the row shrinks as the line's time runs out.</summary>
        private void TickTimer()
        {
            if (_hold <= 0f || !_timer.gameObject.activeSelf) return;

            // Scaled from its left-hand pivot rather than re-anchored: a shrinking anchor would
            // turn the inset rect inside out in the last few pixels.
            float left = Mathf.Clamp01(1f - _age / _hold);
            _timerRect.localScale = new Vector3(left, 1f, 1f);
        }
    }
}
