#nullable enable
using System;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The create-room form's bot count: what may be chosen, what it is called, and how the host's
    /// remaining capacity is put into words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A TOTAL for the whole match, 0 to <see cref="ProtocolConstants.MAX_BOTS"/>, in steps of
    /// two</b> (owner, 2026-09-30; protocol 13). It was bots PER TEAM, 0 to 16, typed into a field.
    /// Two, because the game server fields half on each side and an odd total would lose a bot.
    /// </para>
    /// <para>
    /// <b>The ceiling is the master's, not this build's.</b> The host carries two matches of 100
    /// bots or three of 50 (measured 2026-09-29/30), and the master sends what is left with every
    /// room list (<see cref="RoomCapacity"/>). A form with no answer yet offers the protocol's
    /// ceiling and says it does not know -- the master checks the create either way, so an
    /// unknown ceiling can only be refused, never exceeded.
    /// </para>
    /// <para>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it; <c>MenuBotSlider</c> and
    /// <c>MenuHostCapacityCard</c> draw what it says.
    /// </para>
    /// </remarks>
    public static class RoomBotChoice
    {
        /// <summary>The slider's step: the game server splits the total evenly between the sides.</summary>
        public const int Step = 2;

        /// <summary>Where the slider draws a labelled notch.</summary>
        public const int TickEvery = 10;

        /// <summary>The one-press counts under the slider. 32/50/64/100 are the owner's benchmarks.</summary>
        public static readonly int[] Presets = { 0, 16, 32, 50, 64, ProtocolConstants.MAX_BOTS };

        /// <summary>How big a match is, by its bots. Drives the colour and the name the form shows.</summary>
        public enum Tier
        {
            None,
            Skirmish,
            Battle,
            War,
            TotalWar,
        }

        /// <summary>
        /// The most bots a room created now may have: the master's answer, or the protocol's
        /// ceiling when there is none.
        /// </summary>
        public static int Ceiling(RoomCapacity? capacity)
        {
            if (capacity == null) return ProtocolConstants.MAX_BOTS;
            if (!capacity.CanCreateRoom) return 0;
            return Snap(capacity.MaxBotsForNewRoom, ProtocolConstants.MAX_BOTS);
        }

        /// <summary>Whether the host has room for another match at all. Unknown reads as yes.</summary>
        public static bool CanCreate(RoomCapacity? capacity) => capacity == null || capacity.CanCreateRoom;

        /// <summary>The nearest legal count at or below <paramref name="bots"/>: even, 0 to <paramref name="ceiling"/>.</summary>
        public static int Snap(int bots, int ceiling)
        {
            int limit = Math.Max(0, Math.Min(ceiling, ProtocolConstants.MAX_BOTS));
            int clamped = Math.Max(0, Math.Min(bots, limit));
            return clamped - (clamped % Step);
        }

        /// <summary>What a fresh form starts at: the room default, or the ceiling when that is lower.</summary>
        public static int Default(RoomCapacity? capacity)
            => Snap(ProtocolConstants.DEFAULT_ROOM_BOTS, Ceiling(capacity));

        /// <summary>Whether the form may send <paramref name="bots"/> against <paramref name="capacity"/>.</summary>
        public static bool IsAllowed(int bots, RoomCapacity? capacity)
            => CanCreate(capacity) && bots >= 0 && bots % Step == 0 && bots <= Ceiling(capacity);

        public static Tier TierOf(int bots)
        {
            if (bots <= 0) return Tier.None;
            if (bots <= 24) return Tier.Skirmish;
            if (bots <= 50) return Tier.Battle;
            if (bots <= 76) return Tier.War;
            return Tier.TotalWar;
        }

        public static string TierName(Tier tier)
        {
            switch (tier)
            {
                case Tier.Skirmish: return "SKIRMISH";
                case Tier.Battle: return "BATTLE";
                case Tier.War: return "WAR";
                case Tier.TotalWar: return "TOTAL WAR";
                default: return "PLAYERS ONLY";
            }
        }

        /// <summary>The tier's colour as RRGGBB, from the menu's own palette.</summary>
        public static string TierHex(Tier tier)
        {
            switch (tier)
            {
                case Tier.Skirmish: return "3BDB83";
                case Tier.Battle: return "35B6FF";
                case Tier.War: return "FF9A2E";
                case Tier.TotalWar: return "FF5265";
                default: return "8DA8BA";
            }
        }

        /// <summary>The big number's caption: "50 BOTS", or "NO BOTS".</summary>
        public static string Readout(int bots) => bots <= 0 ? "NO BOTS" : bots + " BOTS";

        /// <summary>How the total lands in the match.</summary>
        public static string PerSide(int bots)
            => bots <= 0 ? "Only players in this match" : $"{bots / 2} per side, {bots / 2} vs {bots / 2}";

        /// <summary>The map preview card's BOTS cell.</summary>
        public static string Preview(int bots) => bots <= 0 ? "NONE" : $"{bots} ({bots / 2}/SIDE)";

        /// <summary>The line under the slider: what the host can still take.</summary>
        public static string CeilingText(RoomCapacity? capacity)
        {
            if (capacity == null)
                return "Server capacity unknown. The master checks it when you create.";
            if (!capacity.CanCreateRoom)
                return "The servers are full. Wait for a match to end, then refresh.";

            int ceiling = Ceiling(capacity);
            if (ceiling >= ProtocolConstants.MAX_BOTS)
                return $"The servers can take all {ProtocolConstants.MAX_BOTS} bots for this room.";
            if (ceiling == 0)
                return "No bots left on the servers: this room can only have players.";
            return $"Only {ceiling} of {ProtocolConstants.MAX_BOTS} bots left on the servers right now.";
        }

        /// <summary>What is already running, for the capacity card.</summary>
        public static string InPlayText(RoomCapacity capacity)
        {
            string rooms = capacity.RoomsOpen == 1 ? "1 room" : capacity.RoomsOpen + " rooms";
            string bots = capacity.BotsInPlay == 1 ? "1 bot" : capacity.BotsInPlay + " bots";
            return $"{rooms} open, {bots} in play";
        }

        /// <summary>How loaded the host is already, 0 to 1.</summary>
        public static float LoadFraction(RoomCapacity capacity)
            => capacity.BudgetUnits <= 0 ? 1f : Clamp01((float)capacity.UnitsInUse / capacity.BudgetUnits);

        /// <summary>What a room with <paramref name="bots"/> would add to the host's load, 0 to 1.</summary>
        public static float ShareFraction(RoomCapacity capacity, int bots)
            => capacity.BudgetUnits <= 0 ? 0f : Clamp01((float)(capacity.MatchCostUnits + bots) / capacity.BudgetUnits);

        /// <summary>The load line: now, and with this room, or that no room fits.</summary>
        public static string LoadText(RoomCapacity capacity, int bots)
        {
            int now = Percent(LoadFraction(capacity));
            if (!capacity.CanCreateRoom) return $"Server load {now}%. No room for another match right now.";

            int with = Percent(Clamp01(LoadFraction(capacity) + ShareFraction(capacity, bots)));
            return $"Server load {now}% now, {with}% with this room";
        }

        /// <summary>
        /// Whether the chosen map's game server can take a new room, in words, or null when the
        /// master did not say.
        /// </summary>
        /// <param name="mapName">The map's display name, as the form shows it.</param>
        public static string? MapText(RoomCapacity? capacity, ushort mapId, string mapName)
        {
            if (capacity == null) return null;

            foreach (MapAvailability map in capacity.Maps)
            {
                if (map.MapId != mapId) continue;
                if (map.Free > 0)
                    return map.Free == 1 ? $"{mapName}: a server is ready." : $"{mapName}: {map.Free} servers ready.";
                return $"{mapName}: its server is busy with another match. Players can join once it ends.";
            }

            return $"{mapName}: no game server plays this map right now.";
        }

        /// <summary>Whether <see cref="MapText"/> is a warning rather than good news.</summary>
        public static bool MapIsBusy(RoomCapacity? capacity, ushort mapId)
        {
            if (capacity == null) return false;
            foreach (MapAvailability map in capacity.Maps)
                if (map.MapId == mapId) return map.Free <= 0;
            return true;
        }

        private static int Percent(float fraction) => (int)Math.Round(fraction * 100f);

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    }
}
