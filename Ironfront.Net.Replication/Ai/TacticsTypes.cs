using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Ai
{
    /// <summary>What the team commander has a squad doing. Phase P28.</summary>
    public enum SquadRole : byte
    {
        /// <summary>No order from the commander: the squad falls back to the original behaviour.</summary>
        None = 0,

        /// <summary>Take the flag: go straight at it.</summary>
        Attack = 1,

        /// <summary>Hold a threatened flag of our own: dig in round it, facing the threat.</summary>
        Defend = 2,

        /// <summary>Take the flag from the side: a quiet approach through a waypoint off the main axis first.</summary>
        Flank = 3,

        /// <summary>
        /// Gather at the waypoint short of a defended flag and wait for the rest of the assault
        /// (phase P29): the squads go in together once enough of them are there.
        /// </summary>
        Assemble = 4,
    }

    /// <summary>How the side is playing, from the flags it holds and the score. Phase P28.</summary>
    public enum TeamPosture : byte
    {
        Balanced = 0,

        /// <summary>Behind on flags or on points: fewer bots at home, more at the front.</summary>
        Aggressive = 1,

        /// <summary>Ahead on both: hold what the side has, press with the rest.</summary>
        Defensive = 2,
    }

    /// <summary>One capture point or spawn, as the commander needs to see it.</summary>
    public struct FlagInfo
    {
        public Vec3 Position;

        /// <summary>-1 for neutral, else the side that holds it: <c>SpawnPoint.owner</c>'s convention.</summary>
        public int Owner;

        /// <summary>False for a side's base, which can never change hands.</summary>
        public bool Capturable;

        /// <summary>
        /// A side held it when the match began: its HQ, where its vehicles stand and where it can
        /// always respawn (phase P32). Fixed for the match, whoever holds it now.
        /// </summary>
        public bool IsBase;

        /// <summary>Where this flag's neighbours start in the shared adjacency list.</summary>
        public int AdjacencyStart;

        /// <summary>How many neighbours it has there.</summary>
        public int AdjacencyCount;

        /// <summary>Enemies of the planning side near this flag that the side is in contact with.</summary>
        public int EnemiesInContact;

        /// <summary>
        /// An enemy stands inside the flag's capture range: every player's map shows it contested
        /// (phase P29). One of the side's own flags in this state is taken back, not guarded -- the
        /// original squads' "take the nearest flag that is not safely ours".
        /// </summary>
        public bool Contested;
    }

    /// <summary>One squad of the planning side, as the commander needs to see it.</summary>
    public struct SquadInfo
    {
        /// <summary>Stable for the squad's life (<c>Squad.number</c>): ties are broken on it.</summary>
        public int Id;

        /// <summary>Where its leader stands.</summary>
        public Vec3 Position;

        /// <summary>Bots in it.</summary>
        public int Size;

        public bool InVehicle;

        /// <summary>Fighting right now: a target in sight or taking fire.</summary>
        public bool Engaged;

        /// <summary>The role its last order gave it.</summary>
        public SquadRole Role;

        /// <summary>The flag its last order named, -1 for none.</summary>
        public int Flag;
    }

    /// <summary>The commander's order for one squad.</summary>
    public struct SquadOrder
    {
        /// <summary>Which squad, as its index in the list the plan was made from.</summary>
        public int SquadIndex;

        public SquadRole Role;

        /// <summary>The flag to take or hold.</summary>
        public int Flag;

        /// <summary>
        /// Where to go first: the side approach of a flank, or the spot a defence digs in round.
        /// </summary>
        public Vec3 Waypoint;

        public bool HasWaypoint;

        /// <summary>Approach quietly: no sprinting until the waypoint is reached.</summary>
        public bool Sneak;

        /// <summary>The order differs from what the squad was already doing.</summary>
        public bool Changed;
    }
}
