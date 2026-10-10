using System;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Sends what the local player's rounds struck as <c>C_SHOT_REPORT</c> (14.0.6, "what you see
    /// is what you hit"): one message per trigger pull, its pellets batched, at the end of the
    /// frame they struck in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each hit carries the tick the screen was drawing</b> (the router's interpolation
    /// clock), so the server judges the claim against the target's recorded pose at the moment
    /// the shooter saw it, not where the body has gone since.
    /// </para>
    /// <para>
    /// Added in code by <see cref="NetClientBootstrap"/>, as the chat sender is, and installed
    /// into <see cref="NetShotReports.Sink"/> while enabled. Disabled, nothing is reported and the
    /// frames stop carrying <see cref="InputButtons.ReportsOwnHits"/>, so the server sweeps.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ClientShotReporter : MonoBehaviour, IShotReportSink
    {
        private readonly ShotReportHit[] _hits = new ShotReportHit[ShotReportMessage.MaxHits];
        private readonly byte[] _body = new byte[ShotReportMessage.SizeFor(ShotReportMessage.MaxHits)];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private NetClientBootstrap _client;
        private int _count;
        private uint _fireTick;
        private byte _weaponId;

        /// <summary>Messages sent.</summary>
        public int ReportsSent { get; private set; }

        /// <summary>Hits sent, across every message.</summary>
        public int HitsSent { get; private set; }

        private void Awake() => _client = GetComponent<NetClientBootstrap>();

        private void OnEnable() => NetShotReports.Sink = this;

        private void OnDisable()
        {
            if (ReferenceEquals(NetShotReports.Sink, this)) NetShotReports.Sink = null;
            _count = 0;
        }

        /// <summary>Queues one struck round; a round of another pull sends the batch before it.</summary>
        public void Report(uint fireTick, byte weaponId, byte pellet, in RemoteBodyHit hit, float travelledMetres)
        {
            if (_count > 0 && (fireTick != _fireTick || weaponId != _weaponId || _count == _hits.Length)) Flush();

            _fireTick = fireTick;
            _weaponId = weaponId;
            double drawn = _client != null ? _client.Router.Clock.RenderTick : 0.0;
            _hits[_count++] = new ShotReportHit(
                hit.ActorId,
                hit.Part,
                pellet,
                ShotReportHit.PackMillimetres(hit.Point.x),
                ShotReportHit.PackMillimetres(hit.Point.y),
                ShotReportHit.PackMillimetres(hit.Point.z),
                ShotReportHit.PackDecimetres(travelledMetres),
                drawn > 0.0 ? (uint)Math.Round(drawn) : 0u);
        }

        private void LateUpdate()
        {
            if (_count > 0) Flush();
        }

        private void Flush()
        {
            int count = _count;
            _count = 0;
            if (_client == null || !_client.IsConnected) return;

            int size = ShotReportMessage.Write(_body, _fireTick, _weaponId, new ReadOnlySpan<ShotReportHit>(_hits, 0, count));
            if (size < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);
            if (!writer.WriteMessage(ClientMessageType.ShotReport, new ReadOnlySpan<byte>(_body, 0, size))) return;
            if (!writer.TryFinish(out int total)) return;

            _client.Send(ChannelId.ReliableOrdered, new ReadOnlySpan<byte>(_payload, 0, total), reliable: true);
            ReportsSent++;
            HitsSent += count;
        }
    }
}
