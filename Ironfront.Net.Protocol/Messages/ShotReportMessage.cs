using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// One round the shooter's own game saw strike a body (<c>C_SHOT_REPORT</c>, 14.0.6).
    /// </summary>
    /// <remarks>
    /// The point is world space in millimetres, an <c>i32</c> per axis: the snapshot's 6.25 cm
    /// position code is coarser than a head box is worth arguing about.
    /// </remarks>
    public readonly struct ShotReportHit
    {
        /// <summary>u16 + u8 + u8 + 3 x i32 + u16 + u32 = 22 bytes.</summary>
        public const int Size = 22;

        public readonly ushort TargetActorId;
        public readonly HitboxType HitboxType;

        /// <summary>Which of the shot's rounds: 0 for a rifle, 0..19 for a shotgun's pellets.</summary>
        public readonly byte Pellet;

        public readonly int PointXMillimetres;
        public readonly int PointYMillimetres;
        public readonly int PointZMillimetres;

        /// <summary>How far the round had flown, decimetres.</summary>
        public readonly ushort TravelledDecimetres;

        /// <summary>The server tick the shooter's screen was drawing when the round struck.</summary>
        public readonly uint RenderTick;

        public ShotReportHit(ushort targetActorId, HitboxType hitboxType, byte pellet,
            int pointXMillimetres, int pointYMillimetres, int pointZMillimetres,
            ushort travelledDecimetres, uint renderTick)
        {
            TargetActorId = targetActorId;
            HitboxType = hitboxType;
            Pellet = pellet;
            PointXMillimetres = pointXMillimetres;
            PointYMillimetres = pointYMillimetres;
            PointZMillimetres = pointZMillimetres;
            TravelledDecimetres = travelledDecimetres;
            RenderTick = renderTick;
        }

        public float PointX => PointXMillimetres / 1000f;
        public float PointY => PointYMillimetres / 1000f;
        public float PointZ => PointZMillimetres / 1000f;
        public float TravelledMetres => TravelledDecimetres / 10f;

        /// <summary>Metres to the wire's millimetres, clamped rather than wrapped.</summary>
        public static int PackMillimetres(float metres)
        {
            double mm = Math.Round(metres * 1000.0);
            if (double.IsNaN(mm)) return 0;
            if (mm > int.MaxValue) return int.MaxValue;
            if (mm < int.MinValue) return int.MinValue;
            return (int)mm;
        }

        /// <summary>Metres to the wire's decimetres, clamped.</summary>
        public static ushort PackDecimetres(float metres)
        {
            float dm = metres * 10f;
            if (!(dm > 0f)) return 0;
            return dm >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Round(dm);
        }
    }

    /// <summary>
    /// <c>C_SHOT_REPORT</c> (0x29) body codec: the rounds of one trigger pull that the shooter's own
    /// game saw strike a body, sent by a client whose input frames carry
    /// <see cref="InputButtons.ReportsOwnHits"/> (14.0.6, "what you see is what you hit").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the shooter says what was hit.</b> The shooter's game draws every body where its
    /// screen puts it -- interpolated, extrapolated past 100 m, animated -- and flies its rounds
    /// against that picture in real time. The server judging the same pull again against a
    /// rewound box set disagreed with the picture in four ways (owner report 2026-10-10, phase P38
    /// finding F2). The server still decides: it checks every claim against the shot it accepted,
    /// the target's own recorded movement and the walls, and computes the damage itself.
    /// </para>
    /// <para>Layout: <c>u32 fireTick, u8 weaponId, u8 count</c>, then <c>count</c> x <see cref="ShotReportHit"/>.</para>
    /// </remarks>
    public static class ShotReportMessage
    {
        /// <summary>u32 + u8 + u8.</summary>
        public const int HeaderSize = 6;

        /// <summary>A shotgun's twenty pellets is the most one pull can strike with.</summary>
        public const int MaxHits = 20;

        public static int SizeFor(int count) => HeaderSize + count * ShotReportHit.Size;

        /// <summary>Writes the body; bytes written, or -1 when the count or the buffer is wrong.</summary>
        public static int Write(Span<byte> dst, uint fireTick, byte weaponId, ReadOnlySpan<ShotReportHit> hits)
        {
            if (hits.Length < 1 || hits.Length > MaxHits) return -1;
            int size = SizeFor(hits.Length);
            if (dst.Length < size) return -1;

            var w = new SpanWriter(dst);
            w.WriteU32(fireTick);
            w.WriteU8(weaponId);
            w.WriteU8((byte)hits.Length);
            for (int i = 0; i < hits.Length; i++)
            {
                ShotReportHit hit = hits[i];
                w.WriteU16(hit.TargetActorId);
                w.WriteU8((byte)hit.HitboxType);
                w.WriteU8(hit.Pellet);
                w.WriteI32(hit.PointXMillimetres);
                w.WriteI32(hit.PointYMillimetres);
                w.WriteI32(hit.PointZMillimetres);
                w.WriteU16(hit.TravelledDecimetres);
                w.WriteU32(hit.RenderTick);
            }
            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Reads a body into <paramref name="hits"/> (size it to <see cref="MaxHits"/>). False for a
        /// count out of range, a body of the wrong length or a hitbox the wire does not define.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> src, Span<ShotReportHit> hits,
            out uint fireTick, out byte weaponId, out int count)
        {
            fireTick = 0;
            weaponId = 0;
            count = 0;

            var r = new SpanReader(src);
            uint tick = r.ReadU32();
            byte weapon = r.ReadU8();
            byte n = r.ReadU8();
            if (!r.Ok || n < 1 || n > MaxHits || hits.Length < n) return false;
            if (src.Length != SizeFor(n)) return false;

            for (int i = 0; i < n; i++)
            {
                ushort target = r.ReadU16();
                byte box = r.ReadU8();
                byte pellet = r.ReadU8();
                int x = r.ReadI32();
                int y = r.ReadI32();
                int z = r.ReadI32();
                ushort travelled = r.ReadU16();
                uint renderTick = r.ReadU32();
                if (box > (byte)HitboxType.Limb) return false;
                hits[i] = new ShotReportHit(target, (HitboxType)box, pellet, x, y, z, travelled, renderTick);
            }
            if (!r.Ok) return false;

            fireTick = tick;
            weaponId = weapon;
            count = n;
            return true;
        }
    }
}
