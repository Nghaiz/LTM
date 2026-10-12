#nullable enable

using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// Numbers the practice match's soldiers for the killfeed's and the board's tables, which are
    /// indexed by the protocol's actor id: from 1, in the order the HUD meets them, each keeping
    /// its number for the whole match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offline game has no actor ids, only <c>GetInstanceID</c> keys. From 1 rather than 0
    /// because <c>PlayerScoreTable.LeaderOf</c> answers 0 for "nobody leads", as no server ever
    /// hands out actor 0 to a soldier.
    /// </para>
    /// <para>
    /// Up to the protocol's ceiling, 127 soldiers, which the practice screen's 100 bots and one
    /// player stay under. A key met after the ceiling gets no number; the caller leaves that
    /// soldier out rather than giving it somebody else's.
    /// </para>
    /// </remarks>
    internal sealed class PracticeActorIds
    {
        private readonly Dictionary<int, ushort> _ids = new Dictionary<int, ushort>();
        private ushort _next = 1;

        /// <summary>How many soldiers have a number.</summary>
        public int Count => _ids.Count;

        /// <summary>
        /// The number <paramref name="key"/> goes by, given one if it has none yet. False once
        /// every number is taken.
        /// </summary>
        public bool TryAssign(int key, out ushort id)
        {
            if (_ids.TryGetValue(key, out id)) return true;

            if (_next >= ProtocolConstants.MAX_ACTORS)
            {
                id = 0;
                return false;
            }

            id = _next++;
            _ids.Add(key, id);
            return true;
        }

        /// <summary>The number <paramref name="key"/> already has, if any.</summary>
        public bool TryGet(int key, out ushort id) => _ids.TryGetValue(key, out id);
    }
}
