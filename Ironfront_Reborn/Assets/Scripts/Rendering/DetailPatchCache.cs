using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Ironfront.Rendering
{
    /// <summary>
    /// The details of the terrain's patches near the camera, as the terrain itself places them,
    /// packed into pages of a GPU buffer for the culling pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The terrain's own scatter, not a copy of it.</b> Each (patch, prototype) is read once from
    /// <c>TerrainData.ComputeDetailInstanceTransforms</c>, which Unity documents as "the exact same
    /// transform data the Unity engine uses for detail rendering": same positions, sizes and turns,
    /// same subset at a lower density, deterministic (measured on Forest Lake, 2026-10-05). So the
    /// GPU draws the very grass the terrain drew, not a lookalike.
    /// </para>
    /// <para>
    /// <b>Near patches only.</b> Forest Lake holds 3.97 M details; baking them all would cost
    /// ~1.3 s of CPU and ~48 MB. Only the patches near the camera are kept (a few MB), baked as the
    /// camera approaches and dropped once it is far (<see cref="InstancedDetailRenderer"/> decides
    /// which). Reading one costs the CPU what the terrain paid to read it for itself: ~10 ms for
    /// the densest grass patch (31,641 instances) in the Editor.
    /// </para>
    /// <para>
    /// <b>Packing.</b> 20 bytes an instance: its position as the terrain's own floats, its turn in
    /// 16 bits (0.003 degrees), both scales in 16 bits across the patch's own range. Positions in
    /// 16 bits an axis (under 1 mm) moved the edges of blades half a metre from the camera by a
    /// pixel or two in an A/B render, so they are kept whole. <see cref="Unpack"/> is the CPU twin
    /// of <c>DetailCulling.compute</c>'s decode.
    /// </para>
    /// </remarks>
    internal sealed class DetailPatchCache : IDisposable
    {
        /// <summary>Instances a page holds; <c>DetailCulling.compute</c>'s <c>PAGE_SIZE</c>.</summary>
        internal const int PageSize = 1024;

        private const int InitialPages = 256;
        private const float FullTurn = Mathf.PI * 2f;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Packed
        {
            public Vector3 Position;
            public uint TurnWidth;
            public uint Height;
        }

        internal sealed class Entry
        {
            public int PatchX, PatchZ, Prototype;
            public bool Baked;
            public int Count;

            /// <summary>Unity's bounds of the patch's instances of the prototype, in world space.</summary>
            public Bounds Bounds;

            public Vector2 ScaleMin, ScaleRange;
            public int[] Pages;
            public Packed[] Instances;

            /// <summary>Its place among the entries kept in memory off the GPU, or null.</summary>
            public LinkedListNode<Entry> Kept;
        }

        /// <summary>
        /// Details kept in memory, at most, after their patches leave the GPU: about 30 MB.
        /// </summary>
        /// <remarks>
        /// <b>Why keep them.</b> Reading a patch is the terrain's own
        /// <see cref="TerrainData.ComputeDetailInstanceTransforms"/>, which runs on the main thread
        /// only and costs 2 to 3 microseconds a detail in the player: a dense patch is 20 to 60 ms,
        /// and a respawn reads 90 to 100 of them, 300 to 440 ms (phase P35, measured 2026-10-09 on
        /// the owner's machine). The terrain gives the same details every time it is asked
        /// (<c>DetailPatchCacheTests</c>), so a patch read once and left behind is kept, oldest
        /// forgotten first, and reading it again costs only its upload.
        /// </remarks>
        internal const int DefaultKeptDetails = 1_500_000;

        private readonly TerrainData _data;
        private readonly Vector3 _origin;
        private readonly DetailCatalog _catalog;
        private readonly Entry[] _entries;
        private readonly List<Entry> _resident = new List<Entry>();
        private readonly LinkedList<Entry> _kept = new LinkedList<Entry>();
        private readonly Stack<int> _freePages = new Stack<int>();
        private GraphicsBuffer _pool;
        private readonly System.Diagnostics.Stopwatch _part = new System.Diagnostics.Stopwatch();

        /// <summary>Milliseconds spent in the terrain's own read, packing, and uploading, since <see cref="ResetCosts"/>.</summary>
        internal double ComputeMs, PackMs, UploadMs;

        /// <summary>Times the pool has grown since <see cref="ResetCosts"/>.</summary>
        internal int Grows;

        /// <summary>Entries made resident from memory, not the terrain, since <see cref="ResetCosts"/>.</summary>
        internal int Reused;

        internal void ResetCosts()
        {
            ComputeMs = PackMs = UploadMs = 0;
            Grows = 0;
            Reused = 0;
        }

        /// <summary>Times the terrain was asked for a patch's details, since this cache was made.</summary>
        internal int TerrainReads { get; private set; }

        /// <summary>Details kept in memory off the GPU now.</summary>
        internal int KeptDetails { get; private set; }

        /// <summary>The most details kept in memory off the GPU (<see cref="DefaultKeptDetails"/>).</summary>
        internal int KeptBudget { get; set; } = DefaultKeptDetails;
        private int _poolPages;

        internal DetailPatchCache(Terrain terrain, DetailCatalog catalog)
        {
            _data = terrain.terrainData;
            _origin = terrain.GetPosition();
            _catalog = catalog;
            Patches = _data.detailPatchCount;
            Vector3 size = _data.size;
            PatchSize = new Vector2(size.x / Patches, size.z / Patches);
            _entries = new Entry[Patches * Patches * catalog.Prototypes.Length];
            Density = -1f;
        }

        /// <summary>Patches along each side of the terrain.</summary>
        internal int Patches { get; }

        /// <summary>A patch's side along X and Z, in metres.</summary>
        internal Vector2 PatchSize { get; }

        /// <summary>The density the resident patches were read at; -1 before any.</summary>
        internal float Density { get; private set; }

        internal IReadOnlyList<Entry> Resident => _resident;

        internal GraphicsBuffer Pool => _pool;

        internal int PoolPages => _poolPages;

        /// <summary>Where the patch's square starts, in world space.</summary>
        internal Vector2 SquareMin(int patchX, int patchZ) =>
            new Vector2(_origin.x + patchX * PatchSize.x, _origin.z + patchZ * PatchSize.y);

        internal Entry Get(int patchX, int patchZ, int prototype)
        {
            int index = (patchZ * Patches + patchX) * _catalog.Prototypes.Length + prototype;
            return _entries[index] ??= new Entry { PatchX = patchX, PatchZ = patchZ, Prototype = prototype };
        }

        /// <summary>Drops every patch and starts over at <paramref name="density"/>.</summary>
        internal void Reset(float density)
        {
            for (int i = _resident.Count - 1; i >= 0; i--) Drop(_resident[i]);
            _resident.Clear();
            // Another density is another set of details.
            while (_kept.First != null) Forget(_kept.First.Value);
            Density = density;
        }

        /// <summary>Reads the entry's details from the terrain and makes them resident.</summary>
        internal void Bake(Entry entry)
        {
            if (entry.Kept != null)
            {
                Unkeep(entry);
                entry.Baked = true;
                _resident.Add(entry);
                Reused++;
                if (entry.Count > 0) Place(entry);
                return;
            }

            DetailCatalog.Prototype prototype = _catalog.Prototypes[entry.Prototype];
            float density = prototype.ScalesWithDensity ? Density : 1f;
            _part.Restart();
            DetailInstanceTransform[] instances = _data.ComputeDetailInstanceTransforms(
                entry.PatchX, entry.PatchZ, prototype.Layer, density, out Bounds local);
            ComputeMs += _part.Elapsed.TotalMilliseconds;
            TerrainReads++;

            entry.Baked = true;
            entry.Count = instances.Length;
            entry.Bounds = new Bounds(local.center + _origin, local.size);
            _resident.Add(entry);
            if (instances.Length == 0) return;

            _part.Restart();
            entry.Instances = Pack(instances, _origin, out entry.ScaleMin, out entry.ScaleRange);
            PackMs += _part.Elapsed.TotalMilliseconds;
            Place(entry);
        }

        /// <summary>Gives the entry pages in the pool and uploads its details into them.</summary>
        private void Place(Entry entry)
        {
            // Assigned only once allocated: a pool that grows meanwhile re-uploads every entry
            // that has pages, and this one has none of its own yet.
            var pages = new int[(entry.Count + PageSize - 1) / PageSize];
            _part.Restart();
            Allocate(pages);
            entry.Pages = pages;
            Upload(entry);
            UploadMs += _part.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Gives back the entry's pages and keeps its details in memory, within
        /// <see cref="KeptBudget"/>, so reading it again does not ask the terrain.
        /// </summary>
        internal void Evict(Entry entry)
        {
            if (entry.Pages != null)
                foreach (int page in entry.Pages) _freePages.Push(page);
            entry.Pages = null;
            entry.Baked = false;
            _resident.Remove(entry);

            entry.Kept = _kept.AddLast(entry);
            KeptDetails += entry.Count;
            while (KeptDetails > KeptBudget && _kept.First != null) Forget(_kept.First.Value);
        }

        private void Unkeep(Entry entry)
        {
            _kept.Remove(entry.Kept);
            entry.Kept = null;
            KeptDetails -= entry.Count;
        }

        /// <summary>Forgets a kept entry's details: it is read from the terrain if needed again.</summary>
        private void Forget(Entry entry)
        {
            Unkeep(entry);
            entry.Count = 0;
            entry.Instances = null;
        }

        public void Dispose()
        {
            _pool?.Dispose();
            _pool = null;
        }

        internal static Packed[] Pack(DetailInstanceTransform[] instances, Vector3 terrainOrigin,
            out Vector2 scaleMin, out Vector2 scaleRange)
        {
            var smin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 smax = -smin;
            foreach (DetailInstanceTransform d in instances)
            {
                smin = Vector2.Min(smin, new Vector2(d.scaleXZ, d.scaleY));
                smax = Vector2.Max(smax, new Vector2(d.scaleXZ, d.scaleY));
            }
            scaleMin = smin;
            scaleRange = smax - smin;

            var packed = new Packed[instances.Length];
            for (int i = 0; i < instances.Length; i++)
            {
                DetailInstanceTransform d = instances[i];
                float turn = d.rotationY / FullTurn;
                uint t = (uint)Mathf.RoundToInt((turn - Mathf.Floor(turn)) * 65536f) & 0xFFFF;
                uint w = Quantize(d.scaleXZ - smin.x, scaleRange.x), h = Quantize(d.scaleY - smin.y, scaleRange.y);
                packed[i] = new Packed
                {
                    Position = new Vector3(d.posX, d.posY, d.posZ) + terrainOrigin,
                    TurnWidth = t | (w << 16),
                    Height = h,
                };
            }
            return packed;
        }

        /// <summary>What <c>DetailCulling.compute</c> decodes from <paramref name="packed"/>.</summary>
        internal static void Unpack(Packed packed, Vector2 scaleMin, Vector2 scaleRange,
            out Vector3 position, out float rotation, out float scaleXZ, out float scaleY)
        {
            position = packed.Position;
            rotation = (packed.TurnWidth & 0xFFFF) / 65536f * FullTurn;
            scaleXZ = scaleMin.x + scaleRange.x * (packed.TurnWidth >> 16) / 65535f;
            scaleY = scaleMin.y + scaleRange.y * (packed.Height & 0xFFFF) / 65535f;
        }

        private static uint Quantize(float value, float range) =>
            range <= 0f ? 0u : (uint)Mathf.Clamp(Mathf.RoundToInt(value / range * 65535f), 0, 65535);

        private void Drop(Entry entry)
        {
            if (entry.Pages != null)
                foreach (int page in entry.Pages) _freePages.Push(page);
            entry.Baked = false;
            entry.Count = 0;
            entry.Pages = null;
            entry.Instances = null;
        }

        private void Allocate(int[] pages)
        {
            if (_freePages.Count < pages.Length) Grow(pages.Length - _freePages.Count);
            for (int i = 0; i < pages.Length; i++) pages[i] = _freePages.Pop();
        }

        /// <summary>
        /// A pool with at least <paramref name="more"/> pages beyond the current one, doubling, with
        /// every resident page uploaded again at its own place from its CPU copy.
        /// </summary>
        private void Grow(int more)
        {
            Grows++;
            int pages = Mathf.Max(InitialPages, _poolPages * 2);
            while (pages - _poolPages < more) pages *= 2;

            _pool?.Dispose();
            _pool = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pages * PageSize, Marshal.SizeOf<Packed>());
            for (int page = pages - 1; page >= _poolPages; page--) _freePages.Push(page);
            _poolPages = pages;
            foreach (Entry resident in _resident)
                if (resident.Pages != null)
                    Upload(resident);
        }

        private void Upload(Entry entry)
        {
            for (int i = 0; i < entry.Pages.Length; i++)
            {
                int start = i * PageSize;
                int count = Mathf.Min(PageSize, entry.Count - start);
                _pool.SetData(entry.Instances, start, entry.Pages[i] * PageSize, count);
            }
        }
    }
}
