using System.Collections.Generic;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// Splits one side of the Tab board into pages of readable rows, instead of shrinking every row
    /// until a hundred bots fit (owner's list of 2026-10-09, item 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A page is a run of slots</b>: the PLAYERS heading, the players' rows, the BOTS heading and
    /// the bots' rows, each at a fixed height, as many as the column holds. A page that starts in
    /// the middle of a group opens with that group's heading again, so every page says what its rows
    /// are.
    /// </para>
    /// <para>
    /// <b>Both sides page together.</b> The layout is computed from the larger side's counts of each
    /// group, as the board already lines its two columns up, so page 2 on the left holds the same
    /// positions as page 2 on the right and the board still reads across as one table.
    /// </para>
    /// </remarks>
    public static class ScoreboardPaging
    {
        public enum SlotKind { PlayersHeading, Player, BotsHeading, Bot }

        public readonly struct Slot
        {
            public Slot(SlotKind kind, int index, float top, float height)
            {
                Kind = kind;
                Index = index;
                Top = top;
                Height = height;
            }

            public SlotKind Kind { get; }

            /// <summary>The row's position within its group, for a row slot; -1 for a heading.</summary>
            public int Index { get; }

            /// <summary>Distance from the column's top edge to the slot's, downwards.</summary>
            public float Top { get; }

            public float Height { get; }
        }

        /// <summary>
        /// The pages for <paramref name="players"/> players and <paramref name="bots"/> bots in a column
        /// <paramref name="available"/> tall. Always at least one page, empty when there is nobody.
        /// </summary>
        public static List<List<Slot>> Paginate(int players, int bots, float available,
            float headingPitch, float playerPitch, float botPitch)
        {
            var sequence = new List<(SlotKind Kind, int Index)>();
            if (players > 0)
            {
                sequence.Add((SlotKind.PlayersHeading, -1));
                for (int i = 0; i < players; i++) sequence.Add((SlotKind.Player, i));
            }
            if (bots > 0)
            {
                sequence.Add((SlotKind.BotsHeading, -1));
                for (int i = 0; i < bots; i++) sequence.Add((SlotKind.Bot, i));
            }

            var pages = new List<List<Slot>>();
            var page = new List<Slot>();
            float used = 0f;

            foreach ((SlotKind kind, int index) in sequence)
            {
                float pitch = Pitch(kind, headingPitch, playerPitch, botPitch);

                // A heading never ends a page: it moves to the next one with its first row.
                float need = IsHeading(kind) ? pitch + Pitch(RowOf(kind), headingPitch, playerPitch, botPitch) : pitch;
                if (page.Count > 0 && used + need > available)
                {
                    pages.Add(page);
                    page = new List<Slot>();
                    used = 0f;

                    // A page that opens mid-group repeats the group's heading.
                    if (!IsHeading(kind))
                    {
                        SlotKind heading = kind == SlotKind.Player ? SlotKind.PlayersHeading : SlotKind.BotsHeading;
                        page.Add(new Slot(heading, -1, used, headingPitch));
                        used += headingPitch;
                    }
                }

                page.Add(new Slot(kind, index, used, pitch));
                used += pitch;
            }

            pages.Add(page);
            return pages;
        }

        /// <summary>The page holding <paramref name="kind"/> row <paramref name="index"/>, or 0.</summary>
        public static int PageOf(List<List<Slot>> pages, SlotKind kind, int index)
        {
            for (int p = 0; p < pages.Count; p++)
                foreach (Slot slot in pages[p])
                    if (slot.Kind == kind && slot.Index == index) return p;
            return 0;
        }

        private static bool IsHeading(SlotKind kind) => kind == SlotKind.PlayersHeading || kind == SlotKind.BotsHeading;

        private static SlotKind RowOf(SlotKind heading) => heading == SlotKind.PlayersHeading ? SlotKind.Player : SlotKind.Bot;

        private static float Pitch(SlotKind kind, float heading, float player, float bot)
            => kind == SlotKind.Player ? player : kind == SlotKind.Bot ? bot : heading;
    }
}
