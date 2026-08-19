using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// Converts between the authority's team byte and the original game's spawn-point team int.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two representations disagree about neutral and they disagree loudly:
    /// <see cref="TeamId.None"/> is <c>255</c>, while <c>SpawnPoint.owner</c> spells neutral
    /// <c>-1</c>. A cast rather than a mapping turns every neutral flag on the map into a
    /// spawn point owned by team 255 — which no team matches, so nobody spawns — and the
    /// narrowing form <c>(int)(sbyte)</c> would instead hand it to team -1, which every
    /// <c>owner &lt; 0</c> eligibility test reads as "any team may spawn here".
    /// </para>
    /// <para>
    /// One function, in the engine-free half, so both the server slave that writes
    /// <c>SpawnPoint.owner</c> and any future client presenter apply the same rule, and so the
    /// rule itself is covered by <c>dotnet test</c> rather than by a headless run.
    /// </para>
    /// </remarks>
    public static class CapturePointOwnership
    {
        /// <summary>What <c>SpawnPoint.owner</c> spells for "no team holds this".</summary>
        public const int Neutral = -1;

        /// <summary>
        /// The <c>SpawnPoint.owner</c> value for an authoritative
        /// <see cref="CapturePointState.OwningTeam"/>.
        /// </summary>
        public static int ToSpawnPointOwner(byte owningTeam)
        {
            if (owningTeam == TeamId.Team0) return 0;
            if (owningTeam == TeamId.Team1) return 1;
            return Neutral;
        }

        /// <summary>
        /// The 0..1 "how far along is this capture" the flag pole is lerped against, from the
        /// signed -1..+1 ownership the authority keeps.
        /// </summary>
        /// <remarks>
        /// The sign carries <i>which</i> team, and <c>SetOwner</c> already carries that; the
        /// height only ever encoded <i>how much</i>. Taking the magnitude here rather than at
        /// the call site keeps the two halves of that split stated once.
        /// </remarks>
        public static float ToControl(float owner) => owner < 0f ? -owner : owner;
    }
}
