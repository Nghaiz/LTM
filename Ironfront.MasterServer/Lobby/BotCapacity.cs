using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Lobby
{
    /// <summary>
    /// How many bots the game-server host can still take, across every room at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A room costs a fixed share plus one unit per bot.</b> The owner measured the Azure host
    /// (B2as_v2, one physical core shared by every game server) with the P29 AI on 2026-09-29/30:
    /// two matches of 100 bots, or three of 50, is as much as it carries. Both ceilings are the same
    /// total only if a match costs 50 bots' worth before its first bot -- its players, its vehicles,
    /// the simulation itself: <c>2 x (50 + 100) = 3 x (50 + 50) = 300</c>. Those are the defaults,
    /// and both are environment variables, because a bigger host moves them.
    /// </para>
    /// <para>
    /// <b>Every room counts, from the moment it is created.</b> A room with no game server yet is a
    /// match about to run, and a budget that ignored it would let two people each create the last
    /// 100 bots. It is released when the room closes.
    /// </para>
    /// <para>
    /// <b>Even numbers only.</b> A room's bots are split evenly between the sides
    /// (<c>GS_ROOM_ASSIGNED.botsPerTeam</c> is half the total), so an odd total would lose a bot; the
    /// create form moves in steps of two and this rounds its answer down to one.
    /// </para>
    /// </remarks>
    public sealed class BotCapacity
    {
        /// <summary>The measured host: 2 x 100 or 3 x 50 bots. See the class remarks.</summary>
        public const int DefaultBudgetUnits = 300;

        /// <summary>What a match costs before its bots. See the class remarks.</summary>
        public const int DefaultMatchCostUnits = 50;

        public BotCapacity(int budgetUnits = DefaultBudgetUnits, int matchCostUnits = DefaultMatchCostUnits)
        {
            if (budgetUnits <= 0) throw new ArgumentOutOfRangeException(nameof(budgetUnits));
            if (matchCostUnits < 0) throw new ArgumentOutOfRangeException(nameof(matchCostUnits));

            BudgetUnits = budgetUnits;
            MatchCostUnits = matchCostUnits;
        }

        /// <summary>What the host carries in all, in units.</summary>
        public int BudgetUnits { get; }

        /// <summary>What one room costs before its bots, in units.</summary>
        public int MatchCostUnits { get; }

        /// <summary>What <paramref name="room"/> takes from the budget.</summary>
        public int CostOf(Room room) => MatchCostUnits + room.BotCount;

        /// <summary>What the rooms that exist already take.</summary>
        public int UnitsInUse(IEnumerable<Room> rooms)
        {
            int used = 0;
            foreach (Room room in rooms) used += CostOf(room);
            return used;
        }

        /// <summary>
        /// The most bots a room created now may ask for: what is left after every existing room and
        /// the new room's own share, at most <see cref="ProtocolConstants.MAX_BOTS"/>, even, and never
        /// below zero. Zero also means the host has no room for another match at all.
        /// </summary>
        public int MaxBotsForNewRoom(IEnumerable<Room> rooms)
        {
            int left = BudgetUnits - UnitsInUse(rooms) - MatchCostUnits;
            int bots = Math.Max(0, Math.Min(ProtocolConstants.MAX_BOTS, left));
            return bots - (bots % 2);
        }

        /// <summary>Whether another room fits at all, whatever its bots.</summary>
        public bool HasRoomForAnotherMatch(IEnumerable<Room> rooms)
            => BudgetUnits - UnitsInUse(rooms) - MatchCostUnits >= 0;
    }
}
