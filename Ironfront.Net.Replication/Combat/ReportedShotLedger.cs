using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// One trigger pull the server accepted and left for the shooter's game to report (14.0.6):
    /// where it left, where it was aimed, and which of its rounds have been reported already.
    /// </summary>
    public struct ReportedShot
    {
        public ushort Shooter;

        /// <summary>The tick of the input frame that fired it: the shooter's own clock.</summary>
        public uint InputTick;

        /// <summary>The server tick that resolved it.</summary>
        public uint ServerTick;

        public byte WeaponId;

        /// <summary>The eye it left from, as the server placed it.</summary>
        public Vec3 Origin;

        /// <summary>The unit aim of the frame that fired it.</summary>
        public Vec3 Aim;

        /// <summary>
        /// Metres the shooter's own eye may have stood from <see cref="Origin"/>: 0 on foot, more
        /// for a passenger, whose game draws their own seat interpolated like everything else.
        /// </summary>
        public float OriginSlack;

        /// <summary>Server seconds when it was fired.</summary>
        public float FiredAt;

        /// <summary>Rounds the pull fired: 1, or a shotgun's pellets.</summary>
        public byte Rounds;

        /// <summary>One bit per round already reported.</summary>
        public uint Reported;

        /// <summary>The career's serial for the shot, so its hits count once for accuracy.</summary>
        public long CareerSerial;

        /// <summary>
        /// How far the shooter's aim swung around the pull, radians: the widest angle between its
        /// aim and the aims of the frames up to <see cref="ReportedShotLedger.SwingTicks"/> either
        /// side. Filled when the shot is taken for a report (<see cref="ReportedShotLedger.TryTake"/>).
        /// </summary>
        public float AimSwing;
    }

    /// <summary>
    /// The pulls each shooter's game still owes reports for, and the one rule that matches a report
    /// to a pull: same weapon, near enough in time, a round of it not yet reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A report names the shooter's tick, not the server's shot.</b> The shooter's gun fires on
    /// its own frame clock and the server's on input frames, so the two can stand a tick or two
    /// apart within one burst. The match is the nearest unreported pull of the same weapon within
    /// <see cref="TickWindow"/>; a report with none is refused. So a report can never buy a round
    /// the server did not accept, and each round is reported at most once.
    /// </para>
    /// <para>
    /// <b>Kept for <see cref="MaxAgeSeconds"/></b>: the longest flight in the game (SL-DEFENDER, 1,000 m,
    /// about two seconds) plus a round trip. A shooter who dies keeps their pulls -- a round in the
    /// air still lands -- and a shooter who leaves is forgotten.
    /// </para>
    /// <para>Fixed storage, nothing allocated after construction.</para>
    /// </remarks>
    public sealed class ReportedShotLedger
    {
        /// <summary>Pulls kept per shooter: three seconds of the fastest gun is about thirty.</summary>
        public const int ShotsKept = 40;

        /// <summary>How old a pull may be when its report arrives.</summary>
        public const float MaxAgeSeconds = 3.5f;

        /// <summary>Ticks between the shooter's fire tick and the frame the server fired on.</summary>
        public const uint TickWindow = 8;

        /// <summary>
        /// Frames either side of a pull whose aims measure the swing (<see cref="ReportedShot.AimSwing"/>):
        /// the shooter's game fires on its own frame, up to a tick or two from the frame the
        /// server fired on, and a round leaves along the aim of that moment.
        /// </summary>
        public const int SwingTicks = 2;

        /// <summary>Frame aims kept per shooter.</summary>
        public const int AimsKept = 16;

        private readonly uint[,] _aimTicks = new uint[ProtocolConstants.MAX_ACTORS, AimsKept];
        private readonly Vec3[,] _aims = new Vec3[ProtocolConstants.MAX_ACTORS, AimsKept];
        private readonly bool[,] _aimUsed = new bool[ProtocolConstants.MAX_ACTORS, AimsKept];

        private readonly ReportedShot[,] _shots = new ReportedShot[ProtocolConstants.MAX_ACTORS, ShotsKept];
        private readonly int[] _next = new int[ProtocolConstants.MAX_ACTORS];
        private readonly bool[,] _used = new bool[ProtocolConstants.MAX_ACTORS, ShotsKept];

        /// <summary>Pulls recorded.</summary>
        public long Recorded { get; private set; }

        /// <summary>Records a pull the server accepted.</summary>
        public void Record(in ReportedShot shot)
        {
            if (shot.Shooter >= ProtocolConstants.MAX_ACTORS || shot.Rounds == 0) return;
            int slot = _next[shot.Shooter];
            _shots[shot.Shooter, slot] = shot;
            _shots[shot.Shooter, slot].Reported = 0;
            _used[shot.Shooter, slot] = true;
            _next[shot.Shooter] = (slot + 1) % ShotsKept;
            Recorded++;
        }

        /// <summary>Records the aim of one of the shooter's accepted frames, for the swing.</summary>
        public void RecordAim(ushort shooter, uint inputTick, in Vec3 aim)
        {
            if (shooter >= ProtocolConstants.MAX_ACTORS) return;
            int slot = (int)(inputTick % AimsKept);
            _aimTicks[shooter, slot] = inputTick;
            _aims[shooter, slot] = aim;
            _aimUsed[shooter, slot] = true;
        }

        /// <summary>
        /// The widest angle, radians, between <paramref name="aim"/> and the shooter's recorded
        /// frame aims within <see cref="SwingTicks"/> of <paramref name="tick"/>.
        /// </summary>
        public float AimSwing(ushort shooter, uint tick, in Vec3 aim)
        {
            if (shooter >= ProtocolConstants.MAX_ACTORS) return 0f;
            Vec3 unit = aim.Normalized;
            double widest = 0.0;
            for (int offset = -SwingTicks; offset <= SwingTicks; offset++)
            {
                uint at = unchecked((uint)(tick + offset));
                int slot = (int)(at % AimsKept);
                if (offset == 0 || !_aimUsed[shooter, slot] || _aimTicks[shooter, slot] != at) continue;
                Vec3 other = _aims[shooter, slot].Normalized;
                double cos = unit.X * other.X + unit.Y * other.Y + unit.Z * other.Z;
                widest = Math.Max(widest, Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))));
            }
            return (float)widest;
        }

        /// <summary>
        /// Takes round <paramref name="round"/> of the pull of <paramref name="weaponId"/> nearest to
        /// <paramref name="fireTick"/>; false when there is none to take.
        /// </summary>
        public bool TryTake(ushort shooter, uint fireTick, byte weaponId, byte round, float now, out ReportedShot shot)
        {
            shot = default;
            if (shooter >= ProtocolConstants.MAX_ACTORS || round >= 32) return false;

            int best = -1;
            uint bestGap = uint.MaxValue;
            for (int i = 0; i < ShotsKept; i++)
            {
                if (!_used[shooter, i]) continue;
                ref ReportedShot candidate = ref _shots[shooter, i];
                if (candidate.WeaponId != weaponId) continue;
                if (round >= candidate.Rounds || (candidate.Reported & (1u << round)) != 0) continue;
                if (now - candidate.FiredAt > MaxAgeSeconds || now < candidate.FiredAt - 1f) continue;

                uint gap = candidate.InputTick >= fireTick ? candidate.InputTick - fireTick : fireTick - candidate.InputTick;
                if (gap > TickWindow || gap >= bestGap) continue;
                best = i;
                bestGap = gap;
            }

            if (best < 0) return false;
            _shots[shooter, best].Reported |= 1u << round;
            shot = _shots[shooter, best];
            shot.AimSwing = AimSwing(shooter, shot.InputTick, in shot.Aim);
            return true;
        }

        /// <summary>
        /// Whether a report for <paramref name="fireTick"/> may still find its pull: the newest pull
        /// of <paramref name="shooter"/> is older than it, so the frame that fires it may simply not
        /// have been applied yet (a report travels on the reliable channel, the frame on the
        /// unreliable one).
        /// </summary>
        public bool MayStillArrive(ushort shooter, uint fireTick)
        {
            if (shooter >= ProtocolConstants.MAX_ACTORS) return false;
            uint newest = 0;
            bool any = false;
            for (int i = 0; i < ShotsKept; i++)
            {
                if (!_used[shooter, i]) continue;
                uint tick = _shots[shooter, i].InputTick;
                if (!any || SequenceMath.IsNewer32(tick, newest)) newest = tick;
                any = true;
            }
            return !any || !SequenceMath.IsNewer32(newest, fireTick + TickWindow);
        }

        /// <summary>Forgets a shooter (they left; the slot may be reused).</summary>
        public void Forget(ushort shooter)
        {
            if (shooter >= ProtocolConstants.MAX_ACTORS) return;
            for (int i = 0; i < ShotsKept; i++) _used[shooter, i] = false;
            for (int i = 0; i < AimsKept; i++) _aimUsed[shooter, i] = false;
            _next[shooter] = 0;
        }
    }
}
