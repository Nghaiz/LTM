using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Ironfront.Rendering
{
    /// <summary>
    /// Draws a terrain's trees with GPU instancing, each on the LOD its LODGroup would pick, in a
    /// few dozen instanced calls instead of one renderer per tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Forest Lake carries 12,412 trees, and the terrain handles each LODGroup tree it
    /// draws as a renderer of its own on the main thread, every frame: about 2,900 of them in view
    /// at once, in <c>Terrain.Trees.OnWillRender</c>, <c>LODTreeInstanceRenderer.UpdateWind</c> and
    /// <c>QueuePrepareIntegrateMainThreadObjects</c> -- some 4 ms of a 38 ms frame in a 100-bot
    /// match (development profile, 2026-10-02). Measured in the Editor from one spot over the
    /// forest, the trees were 9 of the camera's 11.7 ms.
    /// </para>
    /// <para>
    /// <b>The terrain draws none of them while this runs.</b> Its tree distance does not cull a
    /// LODGroup tree (Unity 6 draws one out to its last LOD at any tree distance; measured), so the
    /// terrain's tree LOD bias goes to <see cref="CulledBias"/>, which culls every tree there and
    /// takes the terrain's own tree work to nothing. Its colliders are the terrain data's, and stay.
    /// </para>
    /// <para>
    /// <b>Drawn as the terrain drew them.</b> The LOD is LODGroup's (<see cref="TreeLod"/>) with the
    /// terrain's own bias, so binoculars and scopes pick finer LODs exactly as before. A tree within
    /// the shadow distance (stretched by <see cref="ShadowMargin"/> and <see cref="ShadowReachMetres"/>)
    /// is drawn with its renderer's shadow casting, in batches by cell so each shadow cascade culls
    /// whole cells, and drawn whether or not it is in view, for the shadow it throws into view. Past
    /// that it casts none, as before, and is culled against the view. The one difference is the
    /// dithered cross-fade the far LOD's shader has: an LOD change here is a cut.
    /// </para>
    /// <para>
    /// Taken over from the second frame: the minimap's one-off snapshot renders in a <c>Start</c>,
    /// with every tree the terrain draws. Without a camera, or with vegetation off
    /// (<c>Terrain.drawTreesAndFoliage</c>), the terrain gets its trees back.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class InstancedTreeRenderer : MonoBehaviour
    {
        /// <summary>The terrain's tree LOD bias while this draws: every tree is culled there.</summary>
        internal const float CulledBias = 0.0001f;

        /// <summary>The shadow distance is stretched by this to cover shadows cast from just past it.</summary>
        internal const float ShadowMargin = 1.2f;

        /// <summary>Added to the stretched shadow distance, for the tallest tree's shadow.</summary>
        internal const float ShadowReachMetres = 20f;

        private static readonly ProfilerMarker FrameMarker = new ProfilerMarker("InstancedTreeRenderer.Frame");

        private sealed class Batch
        {
            public TreeCatalog.Part Part;
            public bool Shadowed;
            public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
            public Bounds Bounds;
        }

        private readonly Plane[] _planes = new Plane[6];
        private readonly List<Batch> _used = new List<Batch>();
        private readonly Dictionary<long, Batch[]> _shadowedBatches = new Dictionary<long, Batch[]>();
        private Batch[][][] _unshadowedBatches;
        private Terrain _terrain;
        private TreeCatalog _catalog;
        private float _terrainBias;
        private bool _holding;
        private int _builtFrame = -1;

        /// <summary>Trees drawn here last frame.</summary>
        internal int Drawn { get; private set; }

        /// <summary>Of <see cref="Drawn"/>, the ones drawn with their shadows.</summary>
        internal int DrawnShadowed { get; private set; }

        /// <summary>Trees drawn here last frame at each LOD.</summary>
        internal int[] DrawnAtLod { get; private set; } = new int[0];

        internal bool IsBuilt => _catalog != null;

        internal bool IsHolding => _holding;

        /// <summary>The terrain's own tree LOD bias, given back on release.</summary>
        internal float TerrainBias => _terrainBias;

        private void Start() => Build();

        internal void Build()
        {
            _terrain = GetComponent<Terrain>();
            _catalog = TreeCatalog.TryBuild(_terrain, out string reason);
            if (_catalog == null)
            {
                Debug.Log($"[trees] '{name}': the terrain keeps drawing its trees ({reason}).");
                enabled = false;
                return;
            }

            _terrainBias = _terrain.treeLODBiasMultiplier;
            _unshadowedBatches = new Batch[_catalog.Prototypes.Length][][];
            for (int p = 0; p < _catalog.Prototypes.Length; p++)
            {
                TreeCatalog.Part[][] lods = _catalog.Prototypes[p].Lods;
                _unshadowedBatches[p] = new Batch[lods.Length][];
                for (int lod = 0; lod < lods.Length; lod++) _unshadowedBatches[p][lod] = NewBatches(lods[lod], shadowed: false);
            }
            DrawnAtLod = new int[_catalog.MaxLods];
            _builtFrame = Time.frameCount;
            Debug.Log($"[trees] '{name}': {_catalog.Count} trees in {_catalog.Cells.Length} cells are drawn instanced.");
        }

        private void LateUpdate()
        {
            if (Time.frameCount <= _builtFrame) return;
            Frame(Camera.main);
        }

        private void OnDisable() => Release();

        /// <summary>Draws every tree <paramref name="viewer"/> would see, or hands them back.</summary>
        internal void Frame(Camera viewer)
        {
            using (FrameMarker.Auto())
            {
                ClearBatches();
                if (_catalog == null || viewer == null || !_terrain.drawTreesAndFoliage)
                {
                    Release();
                    return;
                }

                Hold();
                Gather(viewer);
                Draw();
            }
        }

        private void Hold()
        {
            if (_holding && _terrain.treeLODBiasMultiplier == CulledBias) return;
            // Something set the bias since: that is the terrain's own now.
            if (_holding) _terrainBias = _terrain.treeLODBiasMultiplier;
            _terrain.treeLODBiasMultiplier = CulledBias;
            _holding = true;
        }

        private void Release()
        {
            if (!_holding) return;
            _terrain.treeLODBiasMultiplier = _terrainBias;
            _holding = false;
        }

        private void Gather(Camera viewer)
        {
            float lodBias = QualitySettings.lodBias * _terrainBias;
            float tanHalfFov = Mathf.Tan(viewer.fieldOfView * 0.5f * Mathf.Deg2Rad);
            foreach (TreeCatalog.Prototype prototype in _catalog.Prototypes)
                TreeLod.SquaredDistances(prototype.Thresholds, prototype.Size, tanHalfFov, lodBias, prototype.SquaredDistances);

            float shadowRange = QualitySettings.shadows == ShadowQuality.Disable
                ? 0f
                : QualitySettings.shadowDistance * ShadowMargin + ShadowReachMetres;
            float shadow2 = shadowRange * shadowRange;
            GeometryUtility.CalculateFrustumPlanes(viewer, _planes);
            Vector3 eye = viewer.transform.position;

            TreeCatalog.Cell[] cells = _catalog.Cells;
            for (int c = 0; c < cells.Length; c++)
            {
                TreeCatalog.Cell cell = cells[c];
                if (cell.Count == 0) continue;
                bool shadowCell = cell.Bounds.SqrDistance(eye) <= shadow2;
                bool viewCell = GeometryUtility.TestPlanesAABB(_planes, cell.Bounds);
                if (!shadowCell && !viewCell) continue;

                for (int i = cell.Start, end = cell.Start + cell.Count; i < end; i++)
                {
                    Vector3 reference = _catalog.References[i];
                    float distance2 = (reference - eye).sqrMagnitude;
                    int prototypeIndex = _catalog.PrototypeOf[i];
                    int lod = TreeLod.Select(distance2, _catalog.SquaredScales[i], _catalog.Prototypes[prototypeIndex].SquaredDistances);
                    if (lod < 0) continue;

                    bool shadowed = distance2 <= shadow2;
                    if (!shadowed && (!viewCell || !InView(reference, _catalog.Sizes[i] * 0.5f))) continue;

                    Batch[] batches = shadowed ? ShadowedBatches(c, prototypeIndex, lod) : _unshadowedBatches[prototypeIndex][lod];
                    Add(batches, in _catalog.Matrices[i], reference, _catalog.Sizes[i]);
                    Drawn++;
                    if (shadowed) DrawnShadowed++;
                    DrawnAtLod[lod]++;
                }
            }
        }

        private Batch[] ShadowedBatches(int cell, int prototype, int lod)
        {
            long key = ((long)cell * _catalog.Prototypes.Length + prototype) * _catalog.MaxLods + lod;
            if (!_shadowedBatches.TryGetValue(key, out Batch[] batches))
            {
                batches = NewBatches(_catalog.Prototypes[prototype].Lods[lod], shadowed: true);
                _shadowedBatches.Add(key, batches);
            }
            return batches;
        }

        private static Batch[] NewBatches(TreeCatalog.Part[] parts, bool shadowed)
        {
            var batches = new Batch[parts.Length];
            for (int i = 0; i < parts.Length; i++) batches[i] = new Batch { Part = parts[i], Shadowed = shadowed };
            return batches;
        }

        private void Add(Batch[] batches, in Matrix4x4 placement, Vector3 reference, float size)
        {
            var bounds = new Bounds(reference, new Vector3(size, size, size));
            foreach (Batch batch in batches)
            {
                if (batch.Matrices.Count == 0)
                {
                    batch.Bounds = bounds;
                    _used.Add(batch);
                }
                else
                {
                    batch.Bounds.Encapsulate(bounds);
                }
                batch.Matrices.Add(batch.Part.IdentityLocal ? placement : placement * batch.Part.Local);
            }
        }

        private void Draw()
        {
            foreach (Batch batch in _used)
            {
                RenderParams parameters = batch.Shadowed ? batch.Part.Shadowed : batch.Part.Unshadowed;
                parameters.worldBounds = batch.Bounds;
                Graphics.RenderMeshInstanced(parameters, batch.Part.Mesh, batch.Part.Submesh, batch.Matrices);
            }
        }

        private void ClearBatches()
        {
            foreach (Batch batch in _used) batch.Matrices.Clear();
            _used.Clear();
            Drawn = 0;
            DrawnShadowed = 0;
            System.Array.Clear(DrawnAtLod, 0, DrawnAtLod.Length);
        }

        private bool InView(Vector3 centre, float radius)
        {
            for (int i = 0; i < _planes.Length; i++)
            {
                if (_planes[i].GetDistanceToPoint(centre) < -radius) return false;
            }
            return true;
        }
    }
}
