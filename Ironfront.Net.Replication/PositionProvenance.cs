namespace Ironfront.Net.Replication
{
    /// <summary>
    /// Records, per decoded entity, the server tick that entity's POSITION last arrived on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the decoders need this at all, and why the capture tick will not do.</b> A
    /// client's decoded world is not a photograph of tick T — it is the newest value this
    /// client received for each entity, and interest management gives different connections
    /// different update rates on purpose. Two clients at the same server tick therefore
    /// legitimately hold values from different moments. Comparing them without asking WHEN each
    /// value arrived scores that as a disagreement, which is defect <b>X-35</b>: lane A's
    /// agreement counter reported 31 disagreements over 32,520 comparisons on a run with
    /// nothing else wrong with it, all of them one client holding an older copy of a settling
    /// vehicle. Neither its zero nor its non-zero meant what it said.
    /// </para>
    /// <para>
    /// <b>Value-change is not a substitute for this.</b> Deriving "when did this last update"
    /// from "when did this value last change" is right almost always — the encoder only sets
    /// the Position bit when the quantized value moved — and wrong in exactly the case that
    /// matters: a full snapshot re-sends a value that happens to be unchanged for one client
    /// while the other client's differs, and a real divergence is filed as staleness. A false
    /// negative on divergence is the one error <b>X-40</b> cannot afford, since X-40 exists to
    /// size the real divergence rate.
    /// </para>
    /// <para>
    /// <b>Indexed by SLOT, not by entity id.</b> The decoders rebuild
    /// <c>Current</c> from scratch each tick and add entries in wire order, so a slot is stable
    /// for exactly as long as the snapshot it belongs to — which is the same lifetime as the
    /// entry it describes. Keying by id would need a bound on ids that neither pool promises.
    /// </para>
    /// <para>
    /// Sized once at construction and never resized: one <c>uint</c> per entry per baseline,
    /// which at 32 baselines and 64 actors is 8 KB for the actor decoder and 2 KB for the
    /// vehicle one.
    /// </para>
    /// </remarks>
    public sealed class PositionProvenance
    {
        private readonly uint[][] _history;
        private readonly uint[] _current;

        public PositionProvenance(int historyLength, int capacity)
        {
            _history = new uint[historyLength][];
            for (int i = 0; i < historyLength; i++) _history[i] = new uint[capacity];
            _current = new uint[capacity];
        }

        /// <summary>The tick the entry in <paramref name="slot"/> of the live snapshot came on.</summary>
        /// <remarks>
        /// 0 for a slot past the live entry count, and 0 before anything has been applied. A
        /// caller comparing two of these must treat 0 as "unknown" rather than as tick zero.
        /// </remarks>
        public uint CurrentAt(int slot)
            => (uint)slot < (uint)_current.Length ? _current[slot] : 0u;

        /// <summary>The tick recorded for <paramref name="slot"/> of a filed baseline.</summary>
        public uint BaselineAt(uint baselineTick, int slot)
        {
            if ((uint)slot >= (uint)_current.Length) return 0u;
            return _history[baselineTick % (uint)_history.Length][slot];
        }

        public void SetCurrent(int slot, uint serverTick)
        {
            if ((uint)slot < (uint)_current.Length) _current[slot] = serverTick;
        }

        /// <summary>
        /// Files the live ticks alongside the snapshot a later delta may name as its baseline.
        /// Called from the decoder's <c>Finish</c>, in step with its own history write.
        /// </summary>
        public void FileCurrent(uint serverTick, int count)
        {
            uint[] destination = _history[serverTick % (uint)_history.Length];
            for (int i = 0; i < destination.Length; i++)
                destination[i] = i < count ? _current[i] : 0u;
        }

        public void Clear()
        {
            for (int i = 0; i < _current.Length; i++) _current[i] = 0u;
            for (int h = 0; h < _history.Length; h++)
            {
                uint[] row = _history[h];
                for (int i = 0; i < row.Length; i++) row[i] = 0u;
            }
        }
    }
}
