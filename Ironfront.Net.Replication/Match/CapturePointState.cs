using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// One capture point's authoritative state: where it is, who holds it, how far its flag is
    /// up, and what the clients were last told.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Engine-free. The Unity wrapper reads position and radius off the existing
    /// <c>CapturePoint</c> component in the original codebase and copies them in — the
    /// gameplay object stays the client track's, and the authority over its value stays here.
    /// </para>
    /// <para>
    /// <b>The rule is the original game's <c>CapturePoint.UpdateOwner</c>, step for step</b>
    /// (tmp/recovered/src/Assembly-CSharp/CapturePoint.cs). A point has an OWNER and a CONTROL
    /// between 0 and 1 that is the flag's height. Once a second the team with the most living
    /// bodies inside the radius moves the control by its lead times the capture rate: toward 1
    /// if it already owns the point, toward 0 if it does not — and the moment the control
    /// reaches 0 the point changes hands outright, with the new owner's flag at 0.01 at the
    /// bottom of the pole. An owner's flag therefore stays its own all the way down, and a
    /// point is never "nobody's" in the middle of a capture.
    /// </para>
    /// <para>
    /// <b>What this replaced.</b> A signed ownership that slid continuously from -1 to +1 at
    /// 30 Hz, counted as owned only past ±0.9, and capped the lead at four bodies. One enemy
    /// standing on a base for under two seconds took it below 0.9, so the owner lost the point
    /// — its flag, its spawn and its share of the kill multiplier — long before the attacker
    /// had captured anything, and a contested point spent most of a fight belonging to nobody.
    /// None of that is in the original.
    /// </para>
    /// <para>
    /// <b>On the wire</b> (<see cref="CapturePointMessage"/>) the owner is the SIGN of the
    /// ownership byte and the control its magnitude, so <see cref="Owner"/> is
    /// <c>-control</c> for team 0, <c>+control</c> for team 1 and 0 for a neutral point.
    /// </para>
    /// </remarks>
    public sealed class CapturePointState
    {
        /// <summary>Seconds between two ownership steps. The original's
        /// <c>InvokeRepeating("UpdateOwner", 1f, 1f)</c>.</summary>
        public const float UpdateIntervalSeconds = 1f;

        /// <summary>
        /// The control a point is left with when it changes hands. The original's
        /// <c>control = 0.01f</c> after <c>SetOwner</c>.
        /// </summary>
        public const float ControlAfterCapture = 0.01f;

        public CapturePointState(byte pointId, in Vec3 position, float radius, float captureSpeed = 0.2f)
        {
            if (radius <= 0f) throw new ArgumentOutOfRangeException(nameof(radius));

            PointId      = pointId;
            Position     = position;
            Radius       = radius;
            CaptureSpeed = captureSpeed;
            LastSentQ    = CapturePointMessage.PackOwner(0f);
        }

        // The original spells teams 0 and 1 and neutral -1; the wire spells neutral 255. Kept in
        // the original's spelling so the step below reads as a port rather than a translation.
        private const int Neutral = -1;

        private int _owner = Neutral;
        private float _control;
        private float _sinceStep;

        private int _openingOwner = Neutral;
        private float _openingControl;

        /// <summary>
        /// Adopts the map's OPENING ownership: who holds this point before anybody has fought
        /// over it, and what every later round resets to.
        /// </summary>
        /// <param name="owner">-1 team 0, +1 team 1, 0 neutral.</param>
        /// <remarks>
        /// <para>
        /// <b>What went wrong without it (X-53).</b> The host built these states from position,
        /// radius and capture speed and never read the authored owner, so every point on every
        /// map started NEUTRAL on the server. <c>MatchController</c> then wrote that neutrality
        /// onto every scene spawn point, so <c>CountSpawnPointsOwnedBy</c> answered 0 for BOTH
        /// teams, and <c>MatchStateMachine.ApplyElimination</c> read "no spawn points" as "wiped
        /// out" one second into <c>Playing</c> — every round, on both sides at once.
        /// </para>
        /// <para>
        /// An owned point opens with its flag at the top and a neutral one with no flag, as the
        /// original's <c>CapturePoint.Start</c> leaves them (<c>control</c> is 1 by default and
        /// set to 0 for a neutral point).
        /// </para>
        /// <para>
        /// <b><see cref="LastSentQ"/> moves with it, deliberately.</b> It records what clients
        /// have been TOLD. Leaving it at neutral while the point jumps to a base would make the
        /// opening state look already-sent to the dirty check, and the first client to join
        /// would render both bases neutral until somebody walked onto one.
        /// </para>
        /// </remarks>
        public void AdoptOpeningOwner(float owner)
        {
            _openingOwner   = owner < 0f ? 0 : owner > 0f ? 1 : Neutral;
            _openingControl = _openingOwner == Neutral ? 0f : 1f;

            Reset();
        }

        public byte PointId { get; }

        public Vec3 Position { get; }

        public float Radius { get; }

        /// <summary>
        /// Control gained or lost per step for each body of lead. The original's
        /// <c>CAPTURE_RATE_PER_PERSON</c>; zero is an uncapturable HQ.
        /// </summary>
        public float CaptureSpeed { get; }

        /// <summary>How far up the pole the owner's flag is, 0..1.</summary>
        public float Control => _control;

        /// <summary>
        /// The signed control: negative for team 0, positive for team 1, 0 for neutral. Its
        /// magnitude is <see cref="Control"/>.
        /// </summary>
        public float Owner => _owner == 0 ? -_control : _owner == 1 ? _control : 0f;

        /// <summary>Both teams present inside the radius this tick.</summary>
        /// <remarks>
        /// The wire's sense. The original's own <c>isContested</c> — a non-owner is present,
        /// which decides safe spawning — is computed on the scene component from presence.
        /// </remarks>
        public bool IsContested { get; private set; }

        /// <summary>The quantized value the clients were last sent.</summary>
        public sbyte LastSentQ { get; private set; }

        /// <summary>Which team holds it, or <see cref="TeamId.None"/>.</summary>
        public byte OwningTeam => _owner == 0 ? TeamId.Team0 : _owner == 1 ? TeamId.Team1 : TeamId.None;

        /// <summary>
        /// Advances the point by <paramref name="deltaSeconds"/>, taking one ownership step for
        /// every <see cref="UpdateIntervalSeconds"/> that has passed.
        /// </summary>
        /// <returns>True when the change is worth a message, per the rules' send threshold.</returns>
        /// <remarks>
        /// <para>
        /// <b>Stepped, not integrated.</b> The original moves a point once a second by a whole
        /// step, and the tick rate of whatever calls this is not allowed to change how a capture
        /// feels: the first step lands one second after the round opens, as the original's first
        /// <c>UpdateOwner</c> lands one second after <c>Start</c>.
        /// </para>
        /// <para>
        /// <b>Trap 3, and the two halves of the send test.</b> A message goes out only when the
        /// flag has moved by <see cref="MatchRules.CaptureSendThreshold"/> <b>or</b> the point
        /// has changed hands; both compare the quantized byte, because a move that packs to the
        /// same byte changes nothing on the client.
        /// </para>
        /// </remarks>
        public bool Tick(int team0Count, int team1Count, float deltaSeconds, MatchRules rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            IsContested = team0Count > 0 && team1Count > 0;
            if (deltaSeconds <= 0f) return false;

            byte ownerBefore = OwningTeam;

            _sinceStep += deltaSeconds;
            while (_sinceStep >= UpdateIntervalSeconds)
            {
                _sinceStep -= UpdateIntervalSeconds;
                Step(team0Count, team1Count);
            }

            if (OwningTeam != ownerBefore) return true;

            sbyte packed = PackForWire();
            int threshold = (int)(rules.CaptureSendThreshold * 100f);
            return Math.Abs(packed - LastSentQ) >= Math.Max(1, threshold);
        }

        /// <summary>
        /// One <c>CapturePoint.UpdateOwner</c>, ported as the original wrote it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The runner-up is the original's, quirk and all.</b> Its loop over teams 0 and 1
        /// only moves the old leader into second place when a NEW leader is found, so when team
        /// 0 leads, team 1's count is never recorded and the lead is team 0's whole headcount
        /// (3 against 2 moves the flag by 3 steps, not 1); when team 1 leads, the lead is the
        /// true difference; and a tie goes to team 0 with its full count. That asymmetry is in
        /// the shipped game, and it is kept rather than corrected because this port exists to
        /// stop the networked capture from being a different game.
        /// </para>
        /// <para>
        /// <b><c>pendingOwner</c> is not modelled separately.</b> The original writes it only in
        /// <c>SetOwner</c>, together with <c>owner</c>, so the two are equal after <c>Start</c>
        /// and its <c>control == 1 &amp;&amp; owner != pendingOwner</c> branch never runs.
        /// </para>
        /// </remarks>
        private void Step(int team0Count, int team1Count)
        {
            // `if (!canBeCaptured) return;` -- an HQ reports a capture speed of zero.
            if (CaptureSpeed <= 0f) return;

            int leader = Neutral;
            int runnerUp = 0;
            int best = 0;
            for (int team = 0; team < 2; team++)
            {
                int count = team == 0 ? team0Count : team1Count;
                if (count > best)
                {
                    leader = team;
                    runnerUp = best;
                    best = count;
                }
            }

            if (leader == Neutral) return;

            int lead = best - runnerUp;
            if (leader != _owner)
            {
                _control -= lead * CaptureSpeed;
                if (_control <= 0f)
                {
                    _owner = leader;
                    _control = ControlAfterCapture;
                }
            }
            else
            {
                _control = Math.Min(1f, Math.Max(0f, _control + lead * CaptureSpeed));
            }
        }

        /// <summary>The message describing the current state.</summary>
        public CapturePointMessage ToMessage()
            => new CapturePointMessage(
                PointId,
                PackForWire(),
                IsContested ? CaptureFlags.Contested : CaptureFlags.None);

        /// <summary>
        /// Records that clients have been told about the current value. Call this only after
        /// the message has actually been handed to the transport — marking it sent first and
        /// failing to send leaves the point frozen on every client until it next moves.
        /// </summary>
        public void MarkSent() => LastSentQ = PackForWire();

        /// <summary>
        /// Returns the point to the map's OPENING ownership for a new match.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>To the opening owner, not to neutral</b> — see <see cref="AdoptOpeningOwner"/> for
        /// what resetting to neutral cost. Untouched by <see cref="AdoptOpeningOwner"/>, this is
        /// neutral, which is what it always was.
        /// </para>
        /// <para>
        /// The step clock restarts, so the first step of the next round lands one second in.
        /// <see cref="LastSentQ"/> is deliberately reset too: leaving it at the old value would
        /// mean a point that ended the last match where it starts the next one never sends its
        /// opening state to the clients that joined in between.
        /// </para>
        /// </remarks>
        public void Reset()
        {
            _owner      = _openingOwner;
            _control    = _openingControl;
            _sinceStep  = 0f;
            IsContested = false;
            LastSentQ   = PackForWire();
        }

        /// <summary>Squared distance test, so the caller never needs a square root.</summary>
        public bool Contains(in Vec3 point)
        {
            Vec3 d = point - Position;
            return d.SqrMagnitude <= Radius * Radius;
        }

        /// <summary>
        /// <see cref="Owner"/> as the wire's byte, never letting an owned point round to the
        /// neutral 0: the sign IS the owner, and a flag at 0.004 still belongs to someone.
        /// </summary>
        private sbyte PackForWire()
        {
            sbyte packed = CapturePointMessage.PackOwner(Owner);
            if (packed != 0 || _owner == Neutral) return packed;
            return _owner == 0 ? (sbyte)-1 : (sbyte)1;
        }
    }
}
