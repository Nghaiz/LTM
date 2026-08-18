using System.Runtime.CompilerServices;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Interest
{
    /// <summary>
    /// Which id space an <see cref="InterestSubject"/>'s id was drawn from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because <c>Evaluate</c> has a self-comparison in it.</b> The classifier
    /// short-circuits <c>viewer.Id == target.Id</c> to Near — you always see yourself. Actor ids
    /// and vehicle ids are separate <c>u16</c> spaces (V4-D1), so once the same method sees both,
    /// actor 7 looking at vehicle 7 matches that test and the vehicle is pinned to 20 Hz from
    /// anywhere on the map, at any distance, silently.
    /// </para>
    /// <para>
    /// V4-D3 already names this hazard for the rate table and pins it with a test. Carrying the
    /// space in the subject puts the same fact where the comparison is, so a future reader of
    /// <c>Evaluate</c> does not have to know about a decision recorded in another file.
    /// </para>
    /// </remarks>
    public enum InterestSpace : byte
    {
        Actor   = 0,
        Vehicle = 1,
    }

    /// <summary>
    /// The four things <see cref="InterestManager"/>'s classifier actually reads about an
    /// entity, whatever kind of entity it is. V4-D4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A struct, not an interface and not a generic.</b> <c>Evaluate</c> runs on the 30 Hz
    /// path once per (viewer, target) pair — at 16 viewers and 64 targets that is 1024 calls per
    /// snapshot before vehicles are counted. An interface would either box a struct entry or
    /// dispatch virtually on every one of those; a generic constrained to that interface would
    /// need the interface to exist anyway and would then instantiate the whole classifier twice
    /// for no behavioural difference. A 12-byte struct passed <c>in</c> copies nothing and
    /// inlines.
    /// </para>
    /// <para>
    /// <b>Exactly the fields the classifier reads, and nothing else.</b> Id (for the
    /// self-comparison), packed position (for the distance ladder), team (for the teammate
    /// floor) and yaw (for the view cone past the cull radius). Adding a field here that the
    /// classifier does not read would be a per-pair copy cost with no reader — and would invite
    /// the next person to classify on it.
    /// </para>
    /// <para>
    /// <b>Positions stay quantized.</b> Both factories read from a snapshot entry, which is what
    /// the server has at this point in the tick. Re-deriving float positions would mean two
    /// sources of truth for where something is, and the 6.25 cm quantum is five thousand times
    /// finer than the smallest band edge, so it cannot change a classification that was not
    /// already on a knife edge.
    /// </para>
    /// </remarks>
    public readonly struct InterestSubject
    {
        /// <summary>The entity's id, in <b>its own</b> id space.</summary>
        /// <remarks>
        /// Vehicle 7 and actor 7 are different entities with the same number, which is exactly
        /// why the rate tables are separate (V4-D3). Nothing here disambiguates them and nothing
        /// here should: this struct is handed to a classifier that only ever compares an id
        /// against a viewer from the same space.
        /// </remarks>
        public readonly ushort Id;

        /// <summary>Which id space <see cref="Id"/> was drawn from.</summary>
        public readonly InterestSpace Space;

        /// <summary>Quantized position (<see cref="Quantize.PackPos"/>).</summary>
        public readonly short PosX, PosY, PosZ;

        /// <summary>
        /// Team, or <see cref="TeamId.None"/> for an entity the teammate floor must never fire
        /// for.
        /// </summary>
        public readonly byte Team;

        /// <summary>Facing, for the view cone. Meaningless on a non-viewer.</summary>
        public readonly ushort Yaw;

        public InterestSubject(
            ushort id, InterestSpace space,
            short posX, short posY, short posZ, byte team, ushort yaw)
        {
            Id    = id;
            Space = space;
            PosX  = posX;
            PosY  = posY;
            PosZ  = posZ;
            Team  = team;
            Yaw   = yaw;
        }

        /// <summary>
        /// True when two subjects name the same entity — same space AND same id.
        /// </summary>
        /// <remarks>
        /// Never <c>Id == Id</c> alone. See <see cref="InterestSpace"/> for the failure that
        /// buys.
        /// </remarks>
        public bool IsSameEntityAs(in InterestSubject other)
            => Space == other.Space && Id == other.Id;

        /// <summary>Reads an actor's snapshot entry.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static InterestSubject From(in ActorSnapshotEntry entry)
            => new InterestSubject(
                entry.ActorId, InterestSpace.Actor,
                entry.PosX, entry.PosY, entry.PosZ, entry.Team, entry.Yaw);

        /// <summary>
        /// Reads a vehicle's snapshot entry. <b>Team is always
        /// <see cref="TeamId.None"/></b> (V4-D5).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A vehicle is never a viewer and never a teammate. <c>VehicleState.OwnerTeam</c>
        /// exists and is deliberately not read here: the teammate floor promotes a target to
        /// Mid <i>because a player is expected to care about where their side is</i>, and a jeep
        /// somebody's teammate drove ten minutes ago is not that. Writing
        /// <see cref="TeamId.None"/> means the floor at <c>InterestManager</c>'s team check
        /// cannot fire for a vehicle at all, rather than firing rarely and confusingly.
        /// </para>
        /// <para>
        /// Yaw is 0 for the same reason: the view cone is only reached with an actor viewer, and
        /// the vehicle path asserts that. A vehicle's real orientation is a full quaternion the
        /// entry carries as a packed <c>u32</c> — unpacking it here to fill a field nothing
        /// reads would be trigonometry per pair per snapshot for no classification it changes.
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static InterestSubject From(in VehicleSnapshotEntry entry)
            => new InterestSubject(
                entry.VehicleId, InterestSpace.Vehicle,
                entry.PosX, entry.PosY, entry.PosZ, TeamId.None, yaw: 0);
    }
}
