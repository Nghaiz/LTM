#nullable enable

using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>Where a round crossed a body the server streams to this client.</summary>
    public readonly struct RemoteBodyHit
    {
        public RemoteBodyHit(ushort actorId, bool head, Vector3 point, float fraction)
        {
            ActorId = actorId;
            Head = head;
            Point = point;
            Fraction = fraction;
        }

        public ushort ActorId { get; }

        /// <summary>The round met the head's box before the body's.</summary>
        public bool Head { get; }

        /// <summary>World point of entry.</summary>
        public Vector3 Point { get; }

        /// <summary>0..1 along the segment that was tested.</summary>
        public float Fraction { get; }
    }

    /// <summary>Tests a segment against the bodies this client draws for the server's actors.</summary>
    public interface IRemoteBodyHitTester
    {
        /// <summary>The nearest living remote body the segment enters; false when none.</summary>
        bool TryHitBody(Vector3 from, Vector3 to, out RemoteBodyHit hit);
    }

    /// <summary>Sends what this client's rounds struck (<c>C_SHOT_REPORT</c>).</summary>
    public interface IShotReportSink
    {
        void Report(uint fireTick, byte weaponId, byte pellet, in RemoteBodyHit hit, float travelledMetres);
    }

    /// <summary>
    /// "What you see is what you hit" (14.0.6, owner's run of 2026-10-10: "aimed dead on and it
    /// does not hit"): the seam between the game's rounds and the client that reports them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A networked client's bodies have no colliders -- a client must not deal damage -- so the
    /// local player's rounds used to fly straight through them, and the server swept the pull
    /// again against boxes it held where the screen did not draw them. Now each round of the
    /// local player's carried firearm is tested against the bodies as drawn
    /// (<see cref="Tester"/>), and what it struck is reported to the server
    /// (<see cref="Sink"/>), which judges every claim and does the damage itself.
    /// </para>
    /// <para>
    /// Assembly-CSharp fires the rounds and cannot see the client assembly, so the client
    /// installs both halves here, as it does for <see cref="NightVisionReport"/>. With either half
    /// missing nothing is reported and the input frames do not carry
    /// <see cref="InputButtons.ReportsOwnHits"/>, so the server sweeps as it always did.
    /// </para>
    /// </remarks>
    public static class NetShotReports
    {
        public static IRemoteBodyHitTester? Tester { get; set; }

        public static IShotReportSink? Sink { get; set; }

        /// <summary>Whether this client reports its own hits: the input frames say so.</summary>
        public static bool IsActive => NetContext.IsClient && Tester != null && Sink != null;

        /// <summary>
        /// The bit every input frame carries while this client reports its own hits, so the server
        /// leaves its pulls unswept: <see cref="InputButtons.ReportsOwnHits"/>, or none.
        /// </summary>
        public static InputButtons FrameButtons => IsActive ? InputButtons.ReportsOwnHits : InputButtons.None;

        /// <summary>
        /// Whether a pull of <paramref name="weaponId"/> is one the server leaves to this client to
        /// report: a carried firearm the server would otherwise sweep. Launchers, throwables and
        /// mounted weapons keep their own paths.
        /// </summary>
        public static bool Reports(byte weaponId)
        {
            if (!IsActive || weaponId == WeaponIds.NONE || WeaponCatalog.IsMelee(weaponId)) return false;
            WeaponConfig config = WeaponCatalog.For(weaponId);
            return config.Delivery == WeaponDelivery.Hitscan && config.Damage > 0f && !config.HasDelayedRelease;
        }

        /// <summary>The input tick a pull fired now is carried by.</summary>
        public static uint CurrentFireTick
            => NetPredictionClock.Current != null ? NetPredictionClock.Current.InputTick : 0u;

        /// <summary>The nearest remote body on the segment, when this client tests them.</summary>
        public static bool TryHitBody(Vector3 from, Vector3 to, out RemoteBodyHit hit)
        {
            hit = default;
            return Tester != null && Tester.TryHitBody(from, to, out hit);
        }

        public static void Report(uint fireTick, byte weaponId, byte pellet, in RemoteBodyHit hit, float travelledMetres)
            => Sink?.Report(fireTick, weaponId, pellet, in hit, travelledMetres);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Tester = null;
            Sink = null;
        }
    }
}
