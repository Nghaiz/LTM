using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The server half of "what you see is what you hit" (14.0.6, owner's run of 2026-10-10: "aimed
    /// dead on and it does not hit", phase P38 finding F2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pull from a client whose frames carry <see cref="InputButtons.ReportsOwnHits"/> is not
    /// swept: it is recorded in <see cref="ReportedShotLedger"/>, and the client's own game -- which
    /// flew the rounds against the bodies as its screen drew them -- reports what they struck as
    /// <c>C_SHOT_REPORT</c>. Every claim is matched to an accepted pull, checked by
    /// <see cref="ReportedHitJudge"/> and then against the walls, and only then does the server's
    /// own damage, through the same death path a swept hit takes.
    /// </para>
    /// <para>
    /// <b>A report can overtake its pull.</b> It travels on the reliable channel and the frame
    /// that fires the pull on the unreliable one, so a point-blank hit can arrive first. Such a
    /// report waits here for up to <see cref="PendingReportSeconds"/> and is retried each tick
    /// after the players step.
    /// </para>
    /// </remarks>
    internal sealed partial class ServerCombatBridge : IShotReportHandler
    {
        /// <summary>Reports waiting for the frame that fires their pull, at most.</summary>
        private const int MaxPendingReports = 256;

        /// <summary>How long a report may wait for its pull, seconds.</summary>
        private const float PendingReportSeconds = 1f;

        /// <summary>Recorded poses before the tick the shooter drew that a claim is judged against.</summary>
        private const int PosesBefore = 2;

        /// <summary>Recorded poses after it: a body past 100 m is drawn up to eight ticks ahead.</summary>
        private const int PosesAfter = 8;

        /// <summary>
        /// The target's speed when the history holds one pose only, metres a second: a sprinter's,
        /// so a body whose speed is unknown is never judged as standing still.
        /// </summary>
        private const float UnknownSpeedMetresPerSecond = 7f;

        /// <summary>
        /// Metres of the eye-to-point line, at each end, the wall check leaves out: the shooter's
        /// eye stands inside their own rig, and the point sits on a body the server holds up to a
        /// tolerance away from where the shooter drew it.
        /// </summary>
        private const float OcclusionTrimMetres = 0.3f;

        /// <summary>Seconds between the verdict summaries in the log.</summary>
        private const float VerdictSummarySeconds = 60f;

        private static readonly int VerdictCount = Enum.GetValues(typeof(ReportVerdict)).Length;

        private readonly ReportedShotLedger _reportedShots = new ReportedShotLedger();
        private readonly List<PendingReport> _pendingReports = new List<PendingReport>(MaxPendingReports);
        private readonly HitboxSet[] _poses = new HitboxSet[PosesBefore + PosesAfter + 1];
        private readonly Vec3[] _chords = new Vec3[9];
        private readonly long[] _verdicts = new long[VerdictCount];
        private readonly long[] _verdictsAtSummary = new long[VerdictCount];
        private float _nextVerdictSummary = VerdictSummarySeconds;

        private struct PendingReport
        {
            public ushort ConnectionId;
            public ushort Shooter;
            public uint FireTick;
            public byte WeaponId;
            public ShotReportHit Hit;
            public float ArrivedAt;
        }

        /// <summary>Claims judged so far with <paramref name="verdict"/>.</summary>
        public long ReportVerdicts(ReportVerdict verdict) => _verdicts[(int)verdict];

        /// <summary>Reports waiting for their pull right now.</summary>
        public int PendingReportCount => _pendingReports.Count;

        private float Now => _loop.CurrentTick / (float)ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Records a pull the authority accepted and left for the shooter to report.</summary>
        private void RecordReportedShot(
            ClientSession session, uint frameTick, uint tick, float now, in WeaponConfig weapon,
            in CombatTickResult result)
        {
            bool passenger = _authority.RidesAsPassenger?.Invoke(session.ActorId) == true;

            _reportedShots.Record(new ReportedShot
            {
                Shooter = session.ActorId,
                InputTick = frameTick,
                ServerTick = tick,
                WeaponId = session.WeaponId,
                Origin = result.Origin,
                Aim = result.AimDirection,
                OriginSlack = passenger ? ReportedHitJudge.PassengerOriginSlackMetres : 0f,
                FiredAt = now,
                Rounds = (byte)Math.Max(1, Math.Min(32, weapon.ProjectilesPerShot)),
                CareerSerial = _loop.NoteCareerReportedShot(session.ActorId, session.WeaponId),
            });
        }

        void IShotReportHandler.OnShotReport(
            ClientSession session, uint fireTick, byte weaponId, ReadOnlySpan<ShotReportHit> hits)
        {
            float now = Now;
            for (int i = 0; i < hits.Length; i++)
            {
                ShotReportHit hit = hits[i];
                if (TryResolveReport(session, fireTick, weaponId, in hit, now)) continue;

                if (_reportedShots.MayStillArrive(session.ActorId, fireTick)
                    && _pendingReports.Count < MaxPendingReports)
                {
                    _pendingReports.Add(new PendingReport
                    {
                        ConnectionId = session.ConnectionId,
                        Shooter = session.ActorId,
                        FireTick = fireTick,
                        WeaponId = weaponId,
                        Hit = hit,
                        ArrivedAt = now,
                    });
                    continue;
                }

                Tally(ReportVerdict.NoSuchShot, session.ActorId, weaponId, in hit, 0f);
            }
        }

        /// <summary>
        /// Retries the reports that arrived before the frame firing their pull, and writes the
        /// verdict summary when it is due. Once per tick, after the players stepped.
        /// </summary>
        public void StepPendingReports(IReadOnlyList<ServerPlayer> players)
        {
            float now = Now;
            for (int i = 0; i < _pendingReports.Count; )
            {
                PendingReport pending = _pendingReports[i];
                ClientSession session = SessionOf(players, pending.ConnectionId, pending.Shooter);

                bool resolved = session != null
                                && TryResolveReport(session, pending.FireTick, pending.WeaponId, in pending.Hit, now);
                if (!resolved
                    && session != null
                    && now - pending.ArrivedAt < PendingReportSeconds
                    && _reportedShots.MayStillArrive(pending.Shooter, pending.FireTick))
                {
                    i++;
                    continue;
                }

                if (!resolved) Tally(ReportVerdict.NoSuchShot, pending.Shooter, pending.WeaponId, in pending.Hit, 0f);
                _pendingReports.RemoveAt(i);
            }

            if (now >= _nextVerdictSummary) LogVerdictSummary(now);
        }

        /// <summary>Forgets a departing shooter's pulls and the reports still waiting for them.</summary>
        public void ForgetReportedShots(ushort actorId)
        {
            _reportedShots.Forget(actorId);
            _pendingReports.RemoveAll(pending => pending.Shooter == actorId);
        }

        private static ClientSession SessionOf(IReadOnlyList<ServerPlayer> players, ushort connectionId, ushort actorId)
        {
            for (int i = 0; i < players.Count; i++)
            {
                ClientSession session = players[i].Session;
                if (session.ConnectionId == connectionId) return session.ActorId == actorId ? session : null;
            }

            return null;
        }

        private bool TryResolveReport(ClientSession session, uint fireTick, byte weaponId, in ShotReportHit hit, float now)
        {
            if (!_reportedShots.TryTake(session.ActorId, fireTick, weaponId, hit.Pellet, now, out ReportedShot shot))
                return false;

            JudgeAndApply(session, in shot, in hit);
            return true;
        }

        private void JudgeAndApply(ClientSession session, in ReportedShot shot, in ShotReportHit claim)
        {
            WeaponConfig config = WeaponCatalog.For(shot.WeaponId);
            ushort targetId = claim.TargetActorId;

            bool alive = _registry.TryFind(targetId, out NetServerActor target)
                         && target != null && target.isActiveAndEnabled && target.IsAlive;
            bool enclosed = alive && target.IsInEnclosedSeat;

            float speed = 0f;
            Vec3 travel = Vec3.Zero;
            int poses = alive ? CollectPoses(target, claim.RenderTick, out speed, out travel) : 0;

            ReportVerdict verdict = ReportedHitJudge.Judge(
                in shot, in config, in claim, targetId, alive, enclosed,
                new ReadOnlySpan<HitboxSet>(_poses, 0, poses), speed,
                out HitboxType hitbox, out float distance);

            if (verdict == ReportVerdict.Accepted && IsReportOccluded(in shot, in config, in claim, targetId, in travel))
                verdict = ReportVerdict.Occluded;

            Tally(verdict, shot.Shooter, shot.WeaponId, in claim, distance);
            if (verdict != ReportVerdict.Accepted) return;

            DamageOutcome outcome = _authority.ApplyReportedHit(
                in config, shot.Shooter, targetId, hitbox, distance, out float damage);

            _loop.NoteCareerHit(shot.Shooter, targetId, shot.WeaponId, shot.CareerSerial);
            SendHitConfirm(session, targetId, damage, hitbox, outcome.Died);

            if (!outcome.Died) return;

            // The killfeed names the weapon that fired the round, which a quick swap after the
            // pull may already have put away.
            Vec3 force = shot.Aim * config.Force;
            _loop.EmitDeath(targetId, shot.Shooter, in force, (byte)hitbox, CauseOfDeath.Bullet, shot.WeaponId);
            DeathsReported++;
        }

        /// <summary>
        /// The target's recorded boxes around the tick the shooter drew, its speed over them, and
        /// how far it has moved since (the hull check needs that for a crew).
        /// </summary>
        private int CollectPoses(NetServerActor target, uint renderTick, out float speed, out Vec3 travel)
        {
            HitboxHistory history = _loop.HitboxHistory;
            HitboxSet present = target.CaptureHitboxes();

            int count = 0;
            bool haveDrawn = false;
            Vec3 drawn = present.Torso.Center;
            Vec3 first = default, last = default;
            uint firstTick = 0, lastTick = 0;

            for (int offset = -PosesBefore; offset <= PosesAfter; offset++)
            {
                uint tick = unchecked((uint)(renderTick + offset));
                if (!history.TryGetFrame(target.ActorId, tick, out HitboxHistory.Frame frame)) continue;

                if (count == 0)
                {
                    first = frame.Boxes.Torso.Center;
                    firstTick = tick;
                }

                if (offset == 0 || !haveDrawn)
                {
                    drawn = frame.Boxes.Torso.Center;
                    haveDrawn = offset >= 0;
                }

                last = frame.Boxes.Torso.Center;
                lastTick = tick;
                _poses[count++] = frame.Boxes;
            }

            if (count == 0)
            {
                // Out of the ring: a report more than a second late, or a body nobody could see
                // until now. The present pose, with a sprinter's slack.
                _poses[count++] = present;
                speed = UnknownSpeedMetresPerSecond;
                travel = Vec3.Zero;
                return count;
            }

            float seconds = unchecked(lastTick - firstTick) / (float)ProtocolConstants.SIM_TICK_RATE;
            speed = seconds > 0f ? Vec3.Distance(in first, in last) / seconds : UnknownSpeedMetresPerSecond;
            travel = present.Torso.Center - drawn;
            return count;
        }

        /// <summary>
        /// Whether a wall stands between the shooter's eye and the reported point, along the arc a
        /// long round flies (<see cref="ReportedHitJudge.Chords"/>), with the ends trimmed.
        /// </summary>
        private bool IsReportOccluded(
            in ReportedShot shot, in WeaponConfig config, in ShotReportHit claim, ushort targetId, in Vec3 victimTravel)
        {
            var point = new Vec3(claim.PointX, claim.PointY, claim.PointZ);
            float distance = Vec3.Distance(in shot.Origin, in point);
            if (distance <= 2f * OcclusionTrimMetres) return false;

            RoundBallistics round = config.Round;
            int count = ReportedHitJudge.Chords(in shot.Origin, in point, in round, _chords);
            if (count < 2) return false;

            // Trimmed along the straight line between the ends: the arc's own points stay put.
            Vec3 along = (point - shot.Origin) * (1f / distance);
            _chords[0] = shot.Origin + along * OcclusionTrimMetres;
            _chords[count - 1] = point - along * OcclusionTrimMetres;

            for (int i = 0; i + 1 < count; i++)
            {
                Vec3 from = _chords[i];
                Vec3 to = _chords[i + 1];
                var query = new OcclusionQuery(
                    in from, in to, Vec3.Distance(in from, in to), targetId, shot.Shooter,
                    in victimTravel, Vec3.Zero);
                if (ServerTickLoop.IsOccluded(query)) return true;
            }

            return false;
        }

        private void SendHitConfirm(ClientSession shooter, ushort targetId, float damage, HitboxType hitbox, bool killed)
        {
            HitFlags flags = HitFlags.None;
            if (hitbox == HitboxType.Head) flags |= HitFlags.Headshot;
            if (killed) flags |= HitFlags.Killed;

            var message = new HitConfirmMessage(targetId, HitConfirmMessage.PackDamage(damage), hitbox, flags);
            int written = ServerEventWriter.WriteHitConfirm(_eventPayload, in message);
            if (written < 0) return;

            // To the shooter alone, as EmitHitConfirms does and for its reason.
            _loop.SendTo(
                shooter.ConnectionId,
                (byte)ServerEventWriter.ReliableChannel,
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                reliable: true);
        }

        private void Tally(ReportVerdict verdict, ushort shooter, byte weaponId, in ShotReportHit claim, float distance)
        {
            _verdicts[(int)verdict]++;
            if (!ShotLoggingEnabled) return;

            Debug.Log(
                $"[shot-report] actor={shooter} weapon={weaponId} target={claim.TargetActorId} "
                + $"claimed={claim.HitboxType} pellet={claim.Pellet} verdict={verdict} d={distance:F1}m "
                + $"point={claim.PointX:F2},{claim.PointY:F2},{claim.PointZ:F2} "
                + $"drawnTick={claim.RenderTick} tick={_loop.CurrentTick}");
        }

        /// <summary>
        /// One line a minute while reports arrive: how many claims each verdict took. A rising
        /// share of anything but Accepted is the first sign a tolerance is too tight for an honest
        /// shot, which is the bug this protocol exists to fix.
        /// </summary>
        private void LogVerdictSummary(float now)
        {
            _nextVerdictSummary = now + VerdictSummarySeconds;

            long judged = 0;
            for (int i = 0; i < VerdictCount; i++) judged += _verdicts[i] - _verdictsAtSummary[i];
            if (judged == 0) return;

            var line = new System.Text.StringBuilder("[shot-report] last 60 s:");
            for (int i = 0; i < VerdictCount; i++)
            {
                long delta = _verdicts[i] - _verdictsAtSummary[i];
                _verdictsAtSummary[i] = _verdicts[i];
                if (delta == 0) continue;
                line.Append(' ').Append((ReportVerdict)i).Append('=').Append(delta);
            }

            Debug.Log(line.ToString());
        }
    }
}
