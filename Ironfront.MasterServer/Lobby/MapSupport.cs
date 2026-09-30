using System;
using System.Collections.Generic;
using System.Linq;

namespace Ironfront.MasterServer.Lobby
{
    /// <summary>
    /// The maps one client can load: what it named at login, or the catalog of clients that named
    /// none (P30).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the master has to know.</b> A client handed a room on a map its catalog lacks does
    /// not refuse: <c>MapCatalog.SceneOrDefault</c> loads the default map instead, while the game
    /// server simulates the real one. So a v3.0.0 client that joined a Forest Lake room would stand
    /// in Dustbowl among players it cannot see and flags it cannot reach. The master is the one
    /// place that sees both the room and the client, so it keeps them apart: rooms are listed,
    /// joined, created and matchmade only on maps the session can load.
    /// </para>
    /// <para>
    /// <b>A client that names nothing is the v13 catalog.</b> <c>LOGIN_REQ.maps</c> is new, so
    /// every client already in players' hands sends no list; they were built with Dustbowl (1) and
    /// Island (2) and nothing else. That is a fact about those builds, not about the current
    /// catalog, which is why it is written out here rather than read from <c>MapCatalog</c>: a map
    /// added tomorrow must not be offered to them.
    /// </para>
    /// <para>
    /// <b>Map 0 is everybody's.</b> A room that names no map is hosted on the default map, which
    /// every client has; refusing it would refuse nothing dangerous and break the "any map" path.
    /// </para>
    /// </remarks>
    public sealed class MapSupport
    {
        /// <summary>The catalog of every client that names no maps: Dustbowl and Island.</summary>
        public static readonly MapSupport Legacy = new MapSupport(new ushort[] { 1, 2 }, isLegacy: true);

        private readonly HashSet<ushort> _ids;

        private MapSupport(IEnumerable<ushort> ids, bool isLegacy)
        {
            _ids = new HashSet<ushort>(ids);
            IsLegacy = isLegacy;
        }

        /// <summary>Whether this is the assumed catalog of a client that named no maps.</summary>
        public bool IsLegacy { get; }

        /// <summary>The map ids, ascending, for logs.</summary>
        public IReadOnlyList<ushort> Ids => _ids.OrderBy(id => id).ToArray();

        /// <summary>
        /// What a login's <c>maps</c> field says the client can load. Absent or empty is
        /// <see cref="Legacy"/>: an empty list would claim the client can load nothing, which no
        /// build can mean, and treating it as the old catalog is the reading that strands nobody.
        /// </summary>
        public static MapSupport FromLogin(IReadOnlyCollection<ushort>? maps)
            => maps is null || maps.Count == 0 ? Legacy : new MapSupport(maps, isLegacy: false);

        /// <summary>Whether a room on <paramref name="mapId"/> is one this client can play.</summary>
        public bool CanLoad(ushort mapId) => mapId == 0 || _ids.Contains(mapId);

        public override string ToString() => IsLegacy ? "legacy(1,2)" : string.Join(",", Ids);
    }
}
