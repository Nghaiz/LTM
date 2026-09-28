// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using Unity.Profiling;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>One window of per-frame managed allocation, as the profiler measured it.</summary>
    public readonly struct AllocationWindow
    {
        /// <summary>Frames sampled in this window. Zero means the window carries no answer.</summary>
        public readonly long Frames;

        /// <summary>Bytes the managed heap took across the whole window.</summary>
        public readonly long TotalBytes;

        /// <summary>The worst single frame in the window.</summary>
        public readonly long MaxBytesInAFrame;

        /// <summary>Whether the underlying counter was available at all.</summary>
        /// <remarks>
        /// <b>Reported rather than folded into a zero</b>, because a counter that never
        /// started and a frame that allocated nothing are the same number and opposite facts.
        /// A non-development player has no profiler counters; reading <c>0 B/frame</c> off one
        /// and calling check 10 PASS is precisely the green that proves nothing.
        /// </remarks>
        public readonly bool Valid;

        public AllocationWindow(long frames, long totalBytes, long maxBytes, bool valid)
        {
            Frames = frames;
            TotalBytes = totalBytes;
            MaxBytesInAFrame = maxBytes;
            Valid = valid;
        }

        /// <summary>Mean bytes per frame, or -1 when the window carries no answer.</summary>
        /// <remarks>
        /// <b>-1, not 0.</b> Check 10 asks whether a figure is zero; a window with no frames
        /// answering "0" would grade it PASS on the strength of not having run.
        /// </remarks>
        public double BytesPerFrame => Valid && Frames > 0 ? (double)TotalBytes / Frames : -1.0;
    }

    /// <summary>
    /// Samples managed allocation per frame, so check 10 has an instrument. Ledger <b>X-33</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing in the repository measured this.</b> X-33's own search — <c>Profiler</c>,
    /// <c>ProfilerRecorder</c>, <c>GetTotalAllocatedMemoryLong</c>, <c>GC.</c>, <c>allocat</c>
    /// — returned zero hits across <c>Assets/Scripts/**/*.cs</c>, so check 10 ("the client
    /// vehicle stage adds no per-frame allocation") had no lane that could grade it. It was
    /// assigned to lane A, which is engine-free by construction and never loads
    /// <c>ClientVehicleStage</c> at all; R5 moves it here and this is the instrument.
    /// </para>
    /// <para>
    /// <b>WHAT THIS COUNTER CAN AND CANNOT SAY, stated up front because the difference decides
    /// the verdict.</b> <c>GC Allocated In Frame</c> is a WHOLE-FRAME figure. It cannot
    /// attribute a byte to <c>ClientVehicleStage</c>, and no profiler counter can — attribution
    /// needs a sampled call tree, which is a capture rather than a counter. So check 10 is
    /// graded as a DIFFERENCE between checkpoint windows: the per-frame figure while this client
    /// is on foot against the figure while it is driving. A single number from a single window
    /// answers a question nobody asked.
    /// </para>
    /// <para>
    /// <b>The counter lags one frame, and the window is why that is harmless.</b>
    /// <c>ProfilerRecorder.LastValue</c> read during <c>Update</c> reports the frame that has
    /// already been finalised, so a single reading is always about the previous frame. Over a
    /// window of hundreds of frames the offset is one frame at each end and the mean is
    /// unaffected; over one frame it would be an outright misattribution, which is why nothing
    /// here exposes a single-frame reading.
    /// </para>
    /// <para>
    /// <b>It ships with its own falsification.</b> <see cref="ProbeVariable"/> makes the harness
    /// allocate a known quantity every frame on purpose. A recorder that reads the same number
    /// with the probe on and off is decoration, and acceptance criterion 5 requires it to be
    /// SEEN rising rather than assumed to. The probe is the cheapest way to see that, and it
    /// lives in the instrument rather than in a one-off script so the next person can re-run it.
    /// </para>
    /// </remarks>
    public sealed class LaneBAllocationSampler : IDisposable
    {
        /// <summary>
        /// The profiler counter this reads.
        /// </summary>
        /// <remarks>
        /// Managed allocation per frame, which is the quantity check 10 names. NOT
        /// <c>GC.GetTotalMemory</c>, which is a heap SIZE and falls when a collection runs — a
        /// per-frame allocation of zero and a collection that freed exactly as much both leave
        /// it flat.
        /// </remarks>
        public const string CounterName = "GC Allocated In Frame";

        /// <summary>Bytes to allocate deliberately per frame, for the falsification run.</summary>
        public const string ProbeVariable = "IRONFRONT_LANEB_ALLOC_PROBE";

        private ProfilerRecorder _recorder;
        private readonly int _probeBytes;

        // Held so the allocation is not optimised away and IS reachable for the frame it was
        // made in -- an unreferenced array is still allocated, but keeping it makes the intent
        // unarguable to a reader who suspects the compiler of eliding it.
        private byte[] _probeSink;

        private long _windowFrames, _windowTotal, _windowMax;
        private long _runFrames, _runTotal, _runMax;

        public LaneBAllocationSampler()
        {
            _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, CounterName);
            _probeBytes = ReadProbeBytes();

            if (_probeBytes > 0)
            {
                Debug.LogWarning(
                    $"[lane-b] allocation PROBE armed at {_probeBytes} B/frame via "
                    + $"{ProbeVariable}. This run is a falsification of the instrument, not a "
                    + "measurement of the game -- its allocation figures grade nothing.");
            }

            if (!_recorder.Valid)
            {
                Debug.LogWarning(
                    $"[lane-b] profiler counter '{CounterName}' is not available, so check 10 "
                    + "has no reading this run. The commonest cause is a non-development "
                    + "player; the record will say allocValid:false rather than 0 B/frame.");
            }
        }

        /// <summary>Whether the counter started. False means every window carries no answer.</summary>
        public bool Valid => _recorder.Valid;

        /// <summary>Bytes this sampler allocates on purpose per frame, or 0.</summary>
        public int ProbeBytesPerFrame => _probeBytes;

        /// <summary>Everything sampled since the sampler started, across all windows.</summary>
        public AllocationWindow Run => new AllocationWindow(_runFrames, _runTotal, _runMax, Valid);

        /// <summary>Takes one frame's reading. Called once per frame from the harness.</summary>
        /// <remarks>
        /// The probe allocates BEFORE the read on purpose, even though the read reports the
        /// previous frame: from the second frame onward every read sees a frame the probe was
        /// armed for, and ordering it the other way would make the very first reading the only
        /// one that differed.
        /// </remarks>
        public void Sample()
        {
            if (_probeBytes > 0) _probeSink = new byte[_probeBytes];

            if (!_recorder.Valid) return;

            long bytes = _recorder.LastValue;

            _windowFrames++;
            _windowTotal += bytes;
            if (bytes > _windowMax) _windowMax = bytes;

            _runFrames++;
            _runTotal += bytes;
            if (bytes > _runMax) _runMax = bytes;
        }

        /// <summary>
        /// Closes the current window and starts a new one. Called at each checkpoint.
        /// </summary>
        /// <remarks>
        /// Draining rather than reporting-and-continuing is what makes two checkpoints
        /// comparable: a cumulative figure would carry the on-foot frames into the driving
        /// window and dilute exactly the difference check 10 is graded on.
        /// </remarks>
        public AllocationWindow TakeWindow()
        {
            var window = new AllocationWindow(_windowFrames, _windowTotal, _windowMax, Valid);

            _windowFrames = 0;
            _windowTotal = 0;
            _windowMax = 0;

            return window;
        }

        public void Dispose()
        {
            if (_recorder.Valid) _recorder.Dispose();
            _probeSink = null;
        }

        /// <summary>
        /// Reads the probe size from the environment, or 0.
        /// </summary>
        /// <remarks>
        /// A malformed or negative value reads as OFF rather than throwing: this is a
        /// diagnostic, and a harness that refused to start because somebody typed
        /// <c>IRONFRONT_LANEB_ALLOC_PROBE=yes</c> would cost a run to teach a lesson a warning
        /// teaches for free.
        /// </remarks>
        private static int ReadProbeBytes()
        {
            string raw = Environment.GetEnvironmentVariable(ProbeVariable);
            if (string.IsNullOrEmpty(raw)) return 0;

            if (!int.TryParse(raw, out int bytes) || bytes <= 0)
            {
                Debug.LogWarning(
                    $"[lane-b] {ProbeVariable}='{raw}' is not a positive byte count; the "
                    + "allocation probe stays off.");
                return 0;
            }

            return bytes;
        }
    }
}
#endif
