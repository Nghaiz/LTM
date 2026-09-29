using Ironfront.Net.Replication.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The killfeed's pictures: what killed, what a kill earned, and the marks of the lines that
    /// are not deaths. Owner's report of 2026-09-30: more icons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn in code like the rest of <see cref="HudSprites"/>, for its reasons: white shapes the
    /// view tints, no icon set in the project, no glyph to trust in the font. Each is a 64-pixel
    /// square sampled four times a pixel, which leaves a clean edge at the 24-30 px they are shown.
    /// </para>
    /// <para>
    /// The vehicles face right, towards the victim's name, the way the weapon pictures already do.
    /// </para>
    /// </remarks>
    public static partial class HudSprites
    {
        private const int GlyphSize = 64;

        private static readonly Sprite[] _glyphs = new Sprite[32];
        private static readonly Sprite[] _toneIcons = new Sprite[16];
        private static Sprite _scope;
        private static Sprite _sheen;

        /// <summary>The picture for <paramref name="glyph"/>; null for <see cref="KillfeedGlyph.None"/>.</summary>
        public static Sprite Glyph(KillfeedGlyph glyph)
        {
            int index = (int)glyph;
            if (glyph == KillfeedGlyph.None || index >= _glyphs.Length) return null;
            if (_glyphs[index] != null) return _glyphs[index];

            System.Func<float, float, bool> inside = ShapeOf(glyph);
            _glyphs[index] = glyph == KillfeedGlyph.Flag
                ? Flag()
                : glyph == KillfeedGlyph.LongShot
                    ? Scope()
                    : inside != null ? DrawShape("Hud Glyph " + glyph, inside) : Skull();
            return _glyphs[index];
        }

        /// <summary>The picture on a badge of this tone: flames for a streak, chevrons for a multi-kill...</summary>
        public static Sprite ToneIcon(KillfeedTone tone)
        {
            int index = (int)tone;
            if (index >= _toneIcons.Length) return Skull();
            if (_toneIcons[index] != null) return _toneIcons[index];

            switch (tone)
            {
                case KillfeedTone.MultiKill:  _toneIcons[index] = DrawShape("Hud Chevrons", ChevronsShape); break;
                case KillfeedTone.Streak:     _toneIcons[index] = DrawShape("Hud Flame", FlameShape); break;
                case KillfeedTone.FirstBlood: _toneIcons[index] = DrawShape("Hud Blood", DropShape); break;
                case KillfeedTone.Revenge:    _toneIcons[index] = DrawShape("Hud Revenge", RevengeShape); break;
                case KillfeedTone.Shutdown:   _toneIcons[index] = Skull(); break;
                case KillfeedTone.TeamKill:   _toneIcons[index] = DrawShape("Hud Warning", WarningShape); break;
                case KillfeedTone.LongShot:   _toneIcons[index] = Scope(); break;
                default:                      _toneIcons[index] = Skull(); break;
            }

            return _toneIcons[index];
        }

        /// <summary>A ring crossed by long hairlines with a clear centre: a scope, for a long shot.</summary>
        public static Sprite Scope()
        {
            if (_scope != null) return _scope;

            const float centre = (GlyphSize - 1) * 0.5f;
            _scope = Draw("Hud Scope", GlyphSize, GlyphSize, (x, y) =>
            {
                float dx = x - centre;
                float dy = y - centre;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                float ring = Coverage(2.4f - Mathf.Abs(distance - 21f));
                bool clearOfCentre = distance > 7f && distance < 30f;
                float hair = clearOfCentre
                    ? Mathf.Max(Coverage(1.4f - Mathf.Abs(dx)), Coverage(1.4f - Mathf.Abs(dy)))
                    : 0f;
                float dot = Coverage(2.2f - distance);
                return Mathf.Max(ring, Mathf.Max(hair, dot));
            });

            return _scope;
        }

        private static Sprite _range;

        /// <summary>
        /// A double-headed arrow: how far a kill flew, beside its metres. Not a ring, which at row
        /// size reads as the headshot mark sitting right next to it.
        /// </summary>
        public static Sprite Range()
        {
            if (_range != null) return _range;

            _range = DrawShape("Hud Range", (x, y) =>
            {
                bool shaft = x >= 12f && x <= 52f && Mathf.Abs(y - 32f) <= 3f;
                bool left = Inside(new[] { new Vector2(3f, 32f), new Vector2(18f, 21f), new Vector2(18f, 43f) }, x, y);
                bool right = Inside(new[] { new Vector2(61f, 32f), new Vector2(46f, 21f), new Vector2(46f, 43f) }, x, y);
                return shaft || left || right;
            });

            return _range;
        }

        /// <summary>A soft vertical band of light, clear at both sides: the sweep across a new row.</summary>
        public static Sprite Sheen()
        {
            if (_sheen != null) return _sheen;

            const int width = 64;
            _sheen = Draw("Hud Sheen", width, 4, (x, y) =>
            {
                float s = Mathf.Sin(Mathf.PI * x / (width - 1f));
                return s * s * s;
            });

            return _sheen;
        }

        private static Sprite _skull;

        /// <summary>A skull: a death, and a shutdown.</summary>
        public static Sprite Skull()
        {
            if (_skull != null) return _skull;
            _skull = DrawShape("Hud Skull", SkullShape);
            return _skull;
        }

        private static System.Func<float, float, bool> ShapeOf(KillfeedGlyph glyph)
        {
            switch (glyph)
            {
                case KillfeedGlyph.Skull:      return SkullShape;
                case KillfeedGlyph.Explosion:  return BurstShape;
                case KillfeedGlyph.Melee:      return KnifeShape;
                case KillfeedGlyph.Tank:       return TankShape;
                case KillfeedGlyph.Jeep:       return JeepShape;
                case KillfeedGlyph.Helicopter: return HelicopterShape;
                case KillfeedGlyph.Boat:       return BoatShape;
                case KillfeedGlyph.QuadBike:   return QuadBikeShape;
                case KillfeedGlyph.Drowned:    return DropShape;
                case KillfeedGlyph.Fall:       return FallArrowShape;
                case KillfeedGlyph.Joined:     return PersonJoinedShape;
                case KillfeedGlyph.Left:       return PersonLeftShape;
                case KillfeedGlyph.Trophy:     return TrophyShape;
                case KillfeedGlyph.Overflow:   return StackShape;
                default:                       return null;
            }
        }

        private static Sprite DrawShape(string name, System.Func<float, float, bool> inside)
            => Draw(name, GlyphSize, GlyphSize, (x, y) => Supersample(x, y, inside));

        // ------------------------------------------------------------------ shapes, y up, 0..64

        private static bool SkullShape(float x, float y)
        {
            bool cranium = InCircle(x, y, 32f, 38f, 20f);
            bool jaw = InRoundedRect(x, y, 20f, 12f, 44f, 30f, 5f);
            if (!cranium && !jaw) return false;

            bool eye = InCircle(x, y, 24f, 36f, 6f) || InCircle(x, y, 40f, 36f, 6f);
            bool nose = Inside(new[] { new Vector2(28.5f, 27.5f), new Vector2(35.5f, 27.5f), new Vector2(32f, 22f) }, x, y);
            bool teeth = y < 19f && (Mathf.Abs(x - 27.5f) < 1.2f || Mathf.Abs(x - 36.5f) < 1.2f || Mathf.Abs(x - 32f) < 1.2f);
            return !eye && !nose && !teeth;
        }

        private static readonly Vector2[] BurstPoints = StarPoints(32f, 32f, 14,
            new[] { 30f, 15f, 25f, 13f, 29f, 16f, 23f, 14f, 30f, 12f, 26f, 15f, 28f, 13f });

        private static bool BurstShape(float x, float y)
            => Inside(BurstPoints, x, y) && !InCircle(x, y, 32f, 32f, 6f) || InCircle(x, y, 32f, 32f, 3.2f);

        private static bool KnifeShape(float x, float y)
        {
            // A knife pointing right, drawn level and then turned 35 degrees up.
            RotateAboutCentre(ref x, ref y, -35f);

            bool handle = InRoundedRect(x, y, 6f, 27f, 23f, 37f, 3f);
            bool guard = x >= 23f && x <= 27f && y >= 22f && y <= 42f;
            bool blade = Inside(new[]
            {
                new Vector2(27f, 27f), new Vector2(51f, 27f), new Vector2(60f, 31.5f),
                new Vector2(47f, 37f), new Vector2(27f, 37f),
            }, x, y);
            bool rivet = InCircle(x, y, 12f, 32f, 1.8f) || InCircle(x, y, 18f, 32f, 1.8f);
            return (handle && !rivet) || guard || blade;
        }

        private static bool TankShape(float x, float y)
        {
            bool tracks = InRoundedRect(x, y, 5f, 8f, 59f, 21f, 6.5f);
            bool wheel = false;
            for (int i = 0; i < 5; i++)
                if (InCircle(x, y, 14f + i * 9f, 14.5f, 3.2f)) wheel = true;

            bool hull = Inside(new[] { new Vector2(7f, 21f), new Vector2(57f, 21f), new Vector2(52f, 29f), new Vector2(11f, 29f) }, x, y);
            bool turret = Inside(new[] { new Vector2(19f, 29f), new Vector2(43f, 29f), new Vector2(40f, 39f), new Vector2(23f, 39f) }, x, y);
            bool barrel = x >= 40f && x <= 62f && y >= 32f && y <= 35.5f;
            bool muzzle = x >= 58f && x <= 63f && y >= 31f && y <= 36.5f;
            return (tracks && !wheel) || hull || turret || barrel || muzzle;
        }

        private static bool JeepShape(float x, float y)
        {
            bool frontWheel = InCircle(x, y, 47f, 14f, 8f);
            bool backWheel = InCircle(x, y, 16f, 14f, 8f);
            bool hub = InCircle(x, y, 47f, 14f, 3f) || InCircle(x, y, 16f, 14f, 3f);
            bool arches = InCircle(x, y, 47f, 14f, 10f) || InCircle(x, y, 16f, 14f, 10f);

            bool body = Inside(new[]
            {
                new Vector2(5f, 15f), new Vector2(59f, 15f), new Vector2(60f, 24f),
                new Vector2(56f, 28f), new Vector2(38f, 28f), new Vector2(36f, 30f), new Vector2(5f, 30f),
            }, x, y);
            bool windshield = SegmentDistance(x, y, 38f, 28f, 42f, 41f) < 1.8f;
            bool rollBar = SegmentDistance(x, y, 12f, 30f, 14f, 42f) < 1.8f || SegmentDistance(x, y, 14f, 42f, 26f, 42f) < 1.8f;
            bool spare = InCircle(x, y, 4f, 26f, 4f);
            return ((frontWheel || backWheel) && !hub) || (body && !arches) || windshield || rollBar || spare;
        }

        private static bool HelicopterShape(float x, float y)
        {
            bool body = InEllipse(x, y, 25f, 28f, 17f, 10f);
            bool window = InEllipse(x, y, 14f, 30.5f, 6f, 4.5f) && x < 18f;
            bool boom = Inside(new[] { new Vector2(36f, 31f), new Vector2(58f, 30f), new Vector2(58f, 27f), new Vector2(36f, 23f) }, x, y);
            bool fin = Inside(new[] { new Vector2(53f, 27f), new Vector2(60f, 27f), new Vector2(62f, 41f), new Vector2(57f, 41f) }, x, y);
            bool mast = x >= 23f && x <= 27f && y >= 37f && y <= 42f;
            bool rotor = InRoundedRect(x, y, 3f, 42f, 49f, 45f, 1.5f);
            bool skid = InRoundedRect(x, y, 9f, 11f, 41f, 13.5f, 1.2f);
            bool strut = (x >= 16f && x <= 18.5f || x >= 31f && x <= 33.5f) && y >= 13f && y <= 20f;
            return (body && !window) || boom || fin || mast || rotor || skid || strut;
        }

        private static bool BoatShape(float x, float y)
        {
            bool hull = Inside(new[] { new Vector2(8f, 17f), new Vector2(46f, 17f), new Vector2(61f, 30f), new Vector2(5f, 30f) }, x, y);
            bool tube = InRoundedRect(x, y, 4f, 28f, 58f, 34f, 3f);
            bool console = InRoundedRect(x, y, 26f, 34f, 35f, 44f, 1.5f);
            bool screen = SegmentDistance(x, y, 35f, 44f, 38f, 38f) < 1.6f;
            bool motor = InRoundedRect(x, y, 1f, 18f, 7f, 34f, 1.5f);
            bool wave = y < 13f && y > 9f && Mathf.Abs(Mathf.Sin(x * 0.35f) * 2f + 11f - y) < 1.4f;
            return hull || tube || console || screen || motor || wave;
        }

        private static bool QuadBikeShape(float x, float y)
        {
            bool wheels = InCircle(x, y, 15f, 14f, 9.5f) || InCircle(x, y, 49f, 14f, 9.5f);
            bool hubs = InCircle(x, y, 15f, 14f, 3.8f) || InCircle(x, y, 49f, 14f, 3.8f);
            bool fenders = Inside(new[]
            {
                new Vector2(6f, 24f), new Vector2(58f, 24f), new Vector2(60f, 29f),
                new Vector2(42f, 32f), new Vector2(22f, 32f), new Vector2(4f, 29f),
            }, x, y);
            bool seat = InRoundedRect(x, y, 16f, 32f, 34f, 37f, 2.5f);
            bool bar = SegmentDistance(x, y, 42f, 32f, 47f, 43f) < 1.8f || SegmentDistance(x, y, 43f, 43f, 53f, 43f) < 1.8f;
            return (wheels && !hubs) || fenders || seat || bar;
        }

        private static bool DropShape(float x, float y)
        {
            bool round = InCircle(x, y, 32f, 23f, 15f);
            bool tip = Inside(new[] { new Vector2(32f, 60f), new Vector2(18.6f, 29f), new Vector2(45.4f, 29f) }, x, y);
            bool shine = InEllipse(x, y, 25.5f, 22f, 3f, 5.5f);
            return (round || tip) && !shine;
        }

        private static bool FallArrowShape(float x, float y)
        {
            bool shaft = x >= 28f && x <= 36f && y >= 24f && y <= 58f;
            bool head = Inside(new[] { new Vector2(16f, 28f), new Vector2(48f, 28f), new Vector2(32f, 6f) }, x, y);
            bool streaks = (Mathf.Abs(x - 14f) < 1.6f || Mathf.Abs(x - 50f) < 1.6f) && y >= 38f && y <= 56f;
            return shaft || head || streaks;
        }

        private static bool PersonShape(float x, float y, float cx)
        {
            bool head = InCircle(x, y, cx, 44f, 9f);
            bool shoulders = InEllipse(x, y, cx, 16f, 17f, 16f) && y >= 8f && y <= 30f;
            return head || shoulders;
        }

        private static bool PersonJoinedShape(float x, float y)
        {
            bool plus = (Mathf.Abs(x - 52f) < 2.4f && Mathf.Abs(y - 38f) < 9f)
                        || (Mathf.Abs(y - 38f) < 2.4f && Mathf.Abs(x - 52f) < 9f);
            return PersonShape(x, y, 24f) || plus;
        }

        private static bool PersonLeftShape(float x, float y)
        {
            bool shaft = x >= 44f && x <= 56f && Mathf.Abs(y - 26f) < 2.4f;
            bool head = Inside(new[] { new Vector2(53f, 17f), new Vector2(53f, 35f), new Vector2(63f, 26f) }, x, y);
            return PersonShape(x, y, 22f) || shaft || head;
        }

        private static bool TrophyShape(float x, float y)
        {
            bool bowl = Inside(new[]
            {
                new Vector2(15f, 58f), new Vector2(49f, 58f), new Vector2(47f, 44f),
                new Vector2(40f, 34f), new Vector2(24f, 34f), new Vector2(17f, 44f),
            }, x, y);
            bool stem = x >= 29f && x <= 35f && y >= 18f && y <= 35f;
            bool baseBlock = InRoundedRect(x, y, 19f, 8f, 45f, 18f, 2f);
            bool leftHandle = Mathf.Abs(Vector2.Distance(new Vector2(x, y), new Vector2(15f, 48f)) - 7f) < 2f && x < 16f;
            bool rightHandle = Mathf.Abs(Vector2.Distance(new Vector2(x, y), new Vector2(49f, 48f)) - 7f) < 2f && x > 48f;
            bool star = InCircle(x, y, 32f, 47f, 4f);
            return (bowl && !star) || stem || baseBlock || leftHandle || rightHandle;
        }

        private static bool StackShape(float x, float y)
            => InRoundedRect(x, y, 10f, 42f, 46f, 50f, 3f)
               || InRoundedRect(x, y, 16f, 28f, 52f, 36f, 3f)
               || InRoundedRect(x, y, 22f, 14f, 58f, 22f, 3f);

        private static bool ChevronsShape(float x, float y)
            => SegmentDistance(x, y, 12f, 20f, 32f, 38f) < 4.4f || SegmentDistance(x, y, 32f, 38f, 52f, 20f) < 4.4f
               || SegmentDistance(x, y, 12f, 36f, 32f, 54f) < 4.4f || SegmentDistance(x, y, 32f, 54f, 52f, 36f) < 4.4f;

        private static readonly Vector2[] FlamePoints =
        {
            new Vector2(33f, 62f), new Vector2(38f, 50f), new Vector2(46f, 42f), new Vector2(50f, 30f),
            new Vector2(47f, 16f), new Vector2(38f, 6f), new Vector2(26f, 6f), new Vector2(17f, 15f),
            new Vector2(14f, 28f), new Vector2(19f, 40f), new Vector2(24f, 34f), new Vector2(26f, 46f),
        };

        private static readonly Vector2[] FlameCore =
        {
            new Vector2(32f, 36f), new Vector2(38f, 26f), new Vector2(37f, 15f),
            new Vector2(27f, 15f), new Vector2(26f, 25f),
        };

        private static bool FlameShape(float x, float y) => Inside(FlamePoints, x, y) && !Inside(FlameCore, x, y);

        private static bool RevengeShape(float x, float y)
        {
            float dx = x - 32f;
            float dy = y - 30f;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;

            // Most of a ring, open at the top right, ending in an arrowhead that points back round.
            bool arc = Mathf.Abs(distance - 18f) < 3.6f && !(angle > 20f && angle < 75f);
            bool head = Inside(new[] { new Vector2(44f, 40f), new Vector2(58f, 36f), new Vector2(50f, 50f) }, x, y);
            bool centre = InCircle(x, y, 32f, 30f, 4.5f);
            return arc || head || centre;
        }

        private static bool WarningShape(float x, float y)
        {
            bool triangle = Inside(new[] { new Vector2(32f, 60f), new Vector2(4f, 8f), new Vector2(60f, 8f) }, x, y);
            bool bar = x >= 29f && x <= 35f && y >= 24f && y <= 46f;
            bool dot = InCircle(x, y, 32f, 16.5f, 3.4f);
            return triangle && !bar && !dot;
        }

        // ------------------------------------------------------------------ geometry helpers

        private static bool InCircle(float x, float y, float cx, float cy, float r)
        {
            float dx = x - cx;
            float dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }

        private static bool InEllipse(float x, float y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx;
            float dy = (y - cy) / ry;
            return dx * dx + dy * dy <= 1f;
        }

        private static bool InRoundedRect(float x, float y, float x0, float y0, float x1, float y1, float r)
        {
            if (x < x0 || x > x1 || y < y0 || y > y1) return false;

            float cx = Mathf.Clamp(x, x0 + r, x1 - r);
            float cy = Mathf.Clamp(y, y0 + r, y1 - r);
            return InCircle(x, y, cx, cy, r);
        }

        /// <summary>Turns a sample point about the glyph's centre, to draw a shape at an angle.</summary>
        private static void RotateAboutCentre(ref float x, ref float y, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            float dx = x - 32f;
            float dy = y - 32f;
            x = 32f + dx * cos - dy * sin;
            y = 32f + dx * sin + dy * cos;
        }

        private static Vector2[] StarPoints(float cx, float cy, int points, float[] radii)
        {
            var polygon = new Vector2[points];
            for (int i = 0; i < points; i++)
            {
                float angle = Mathf.PI * 0.5f + i * Mathf.PI * 2f / points;
                float radius = radii[i % radii.Length];
                polygon[i] = new Vector2(cx + Mathf.Cos(angle) * radius, cy + Mathf.Sin(angle) * radius);
            }

            return polygon;
        }
    }
}
