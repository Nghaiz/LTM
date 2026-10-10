using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Receives <c>C_SHOT_REPORT</c> (14.0.6): the rounds of one trigger pull that the shooter's own
    /// game saw strike a body. The receiver judges every one (<c>ReportedHitJudge</c>) before any
    /// damage is done; the span is the router's scratch and is valid only during the call.
    /// </summary>
    public interface IShotReportHandler
    {
        void OnShotReport(ClientSession session, uint fireTick, byte weaponId, ReadOnlySpan<ShotReportHit> hits);
    }
}
