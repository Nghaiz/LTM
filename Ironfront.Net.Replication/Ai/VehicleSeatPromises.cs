using System;
using System.Collections.Generic;

namespace Ironfront.Net.Replication.Ai
{
    /// <summary>An empty vehicle parked at one of a side's flags, as a respawning bot sees it.</summary>
    public struct IdleVehicle
    {
        /// <summary>Stable for the vehicle's life: what a promise is kept against.</summary>
        public int Key;

        /// <summary>The side's flag it stands at, as an index the caller understands.</summary>
        public int Flag;

        /// <summary>Seats nobody sits in or has claimed.</summary>
        public int FreeSeats;
    }

    /// <summary>
    /// Sends respawning bots to the flags where their side has empty vehicles, one seat at a time,
    /// so a parked vehicle is crewed instead of left standing (phase P32).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A bot respawned at a random front-line flag seven times in ten, so the squads
    /// that formed never stood near the vehicles parked at a quiet HQ: on Forest Lake the HQ
    /// jeeps, tanks and helicopters sat empty for most of a match and the HQs were never fought
    /// over. Respawning is how a bot "goes back for a vehicle"; this decides where it goes.
    /// </para>
    /// <para>
    /// <b>A promise, not a claim.</b> A batch of bodies is placed first and its squads formed after,
    /// so for the length of a batch the seats a bot was sent for are claimed by nobody. Without a
    /// record of the seats already promised, every bot in that batch would read the same empty
    /// vehicle. A promise lapses after <see cref="PromiseSeconds"/>, by which time the batch has
    /// formed its crew and the crew's claims or seats say the rest; holding it longer counts the
    /// same bot twice, once as a promise and once as a claim.
    /// </para>
    /// <para>
    /// <b>One vehicle filled before the next is started</b>, so the bots sent for one jeep arrive
    /// together and form one squad for it rather than half a squad for each of two.
    /// </para>
    /// </remarks>
    public sealed class VehicleSeatPromises
    {
        /// <summary>How long a promised seat stays promised, in seconds.</summary>
        public const float PromiseSeconds = 3f;

        private readonly Dictionary<int, Promise> _promises = new Dictionary<int, Promise>();
        private readonly List<int> _expired = new List<int>();

        private struct Promise
        {
            public int Seats;
            public float Until;
        }

        /// <summary>Seats promised on <paramref name="key"/> and not yet lapsed at <paramref name="now"/>.</summary>
        public int Promised(int key, float now)
            => _promises.TryGetValue(key, out Promise promise) && promise.Until > now ? promise.Seats : 0;

        /// <summary>
        /// Picks the vehicle the next respawning bot should go to and promises it one seat. Returns
        /// its index in <paramref name="candidates"/>, or -1 when every seat is taken or promised.
        /// </summary>
        /// <remarks>
        /// A vehicle already being filled comes first; then the one with the most seats left; ties
        /// go to the lower key, so the same input gives the same answer.
        /// </remarks>
        public int Choose(ReadOnlySpan<IdleVehicle> candidates, float now)
        {
            Lapse(now);

            int best = -1;
            bool bestFilling = false;
            int bestLeft = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                int promised = Promised(candidates[i].Key, now);
                int left = candidates[i].FreeSeats - promised;
                if (left <= 0) continue;

                bool filling = promised > 0;
                bool better = best < 0
                    || (filling && !bestFilling)
                    || (filling == bestFilling && left > bestLeft)
                    || (filling == bestFilling && left == bestLeft && candidates[i].Key < candidates[best].Key);
                if (!better) continue;

                best = i;
                bestFilling = filling;
                bestLeft = left;
            }

            if (best < 0) return -1;

            int key = candidates[best].Key;
            _promises[key] = new Promise { Seats = Promised(key, now) + 1, Until = now + PromiseSeconds };
            return best;
        }

        /// <summary>Forgets every promise: a new round.</summary>
        public void Clear() => _promises.Clear();

        private void Lapse(float now)
        {
            _expired.Clear();
            foreach (KeyValuePair<int, Promise> entry in _promises)
            {
                if (entry.Value.Until <= now) _expired.Add(entry.Key);
            }
            for (int i = 0; i < _expired.Count; i++) _promises.Remove(_expired[i]);
        }
    }
}
