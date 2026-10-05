using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Rendering
{
    /// <summary>
    /// Draws a terrain's trees on the GPU: a compute pass culls each tree and picks the LOD its
    /// LODGroup would, and indirect instanced draws read what it kept. The CPU does no work per tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Forest Lake carries 12,412 trees, and the terrain handles each LODGroup tree it
    /// draws as a renderer of its own on the main thread, every frame: about 2,900 of them in view
    /// at once, in <c>Terrain.Trees.OnWillRender</c>, <c>LODTreeInstanceRenderer.UpdateWind</c> and
    /// <c>QueuePrepareIntegrateMainThreadObjects</c> -- some 4 ms of a 38 ms frame in a 100-bot
    /// match (development profile, 2026-10-02). Drawing them with
    /// <c>Graphics.RenderMeshInstanced</c> instead still cost the main thread 0.36 us a tree to
    /// pick and 0.26 us a tree to submit (release IL2CPP player, 2026-10-02), which is why the
    /// picking and the instance data live on the GPU here.
    /// </para>
    /// <para>
    /// <b>The terrain draws none of them while this runs.</b> Its tree distance does not cull a
    /// LODGroup tree (Unity 6 draws one out to its last LOD at any tree distance; measured), so the
    /// terrain's tree LOD bias goes to <see cref="CulledBias"/>, which culls every tree there and
    /// takes the terrain's own tree work to nothing. Its colliders are the terrain data's, and stay.
    /// </para>
    /// <para>
    /// <b>Drawn as the terrain drew them</b> (<c>TreeCulling.compute</c>). The LOD is LODGroup's
    /// (<see cref="TreeLod"/>) with the terrain's own bias and the camera's field of view, so
    /// binoculars and scopes pick finer LODs and see farther exactly as before. A tree within the
    /// shadow distance (stretched by <see cref="ShadowMargin"/> and <see cref="ShadowReachMetres"/>)
    /// casts its renderer's shadow and is drawn in or out of view, for the shadow it throws into
    /// view; past that it casts none, as before, and is culled against the view. The one difference
    /// is the dithered cross-fade the far LOD's shader has: an LOD change here is a cut.
    /// </para>
    /// <para>
    /// Taken over from the second frame: the minimap's one-off snapshot renders in a <c>Start</c>,
    /// with every tree the terrain draws. Without a camera, or with vegetation off
    /// (<c>Terrain.drawTreesAndFoliage</c>), the terrain gets its trees back; so does a machine
    /// without compute shaders, and a terrain whose trees cannot be drawn this way
    /// (<see cref="TreeCatalog"/>), and each says why once.
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

        /// <summary>Structured buffers the instanced tree shaders read per vertex.</summary>
        internal const int VertexBufferInputs = 3;

        private const int ThreadGroupSize = 64;
        private const int ArgsPerCommand = 5;

        private static readonly ProfilerMarker FrameMarker = new ProfilerMarker("InstancedTreeRenderer.Frame");
        private static readonly int TreesId = Shader.PropertyToID("_Trees");
        private static readonly int LodSquaredDistancesId = Shader.PropertyToID("_LodSquaredDistances");
        private static readonly int LodCountsId = Shader.PropertyToID("_LodCounts");
        private static readonly int BucketStartsId = Shader.PropertyToID("_BucketStarts");
        private static readonly int BucketCountsId = Shader.PropertyToID("_BucketCounts");
        private static readonly int VisibleId = Shader.PropertyToID("_Visible");
        private static readonly int CommandBucketsId = Shader.PropertyToID("_CommandBuckets");
        private static readonly int ArgsId = Shader.PropertyToID("_Args");
        private static readonly int TreeCountId = Shader.PropertyToID("_TreeCount");
        private static readonly int BucketCountId = Shader.PropertyToID("_BucketCount");
        private static readonly int CommandCountId = Shader.PropertyToID("_CommandCount");
        private static readonly int EyeId = Shader.PropertyToID("_Eye");
        private static readonly int ShadowRange2Id = Shader.PropertyToID("_ShadowRange2");
        private static readonly int PlanesId = Shader.PropertyToID("_Planes");
        private static readonly int LightDirectionId = Shader.PropertyToID("_LightDirection");
        private static readonly int ShadowReachPerRadiusId = Shader.PropertyToID("_ShadowReachPerRadius");
        private static readonly int ShadowRangeId = Shader.PropertyToID("_ShadowRange");

        /// <summary>
        /// What a kept tree is drawn in. A tree out of view but inside the shadow range is drawn
        /// in the shadow passes only: until P31 every tree within the range, all round the camera,
        /// went through the main pass too, and into every cascade whether its shadow could fall in
        /// view or not (TreeCulling.compute).
        /// </summary>
        internal enum Pass
        {
            /// <summary>In view, past the shadow range: drawn, casting nothing.</summary>
            Unshadowed = 0,

            /// <summary>In view and in the shadow range: drawn with its shadow.</summary>
            Shadowed = 1,

            /// <summary>Out of view, its shadow able to reach the view: the shadow alone.</summary>
            ShadowOnly = 2,
        }

        private const int Passes = 3;

        /// <summary>
        /// The lowest the shadow-casting light is taken to stand, as the sine of its elevation: a
        /// tree's shadow runs 2 radii / sin(elevation) along the light, so a light at the horizon
        /// would make every shadow endless. Under it the shadow range alone bounds them.
        /// </summary>
        internal const float LowestLightSine = 0.1f;

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuTree
        {
            public Vector3 Reference;
            public float SquaredScale;
            public float Radius;
            public uint Prototype;
        }

        private sealed class Command
        {
            public TreeCatalog.Part Part;
            public int Prototype;
            public int Lod;
            public Pass Pass;
            public int Bucket;
            public MaterialPropertyBlock Properties;
        }

        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _planeVectors = new Vector4[6];
        private Terrain _terrain;
        private TreeCatalog _catalog;
        private ComputeShader _culling;
        private int _clearKernel, _cullKernel, _writeArgsKernel;
        private GraphicsBuffer _trees, _objectToWorld, _worldToObject, _lodSquaredDistances, _lodCounts;
        private GraphicsBuffer _bucketStarts, _bucketCounts, _visible, _commandBuckets;
        private ComputeBuffer _args;
        private Command[] _commands;
        private float[] _lodTable;
        private float[][] _lodSquared;
        private bool[] _reachable;
        private int _bucketCount;
        private Bounds _bounds;
        private float _terrainBias;
        private bool _holding;
        private int _builtFrame = -1;
        private float _tallestRadius;

        internal bool IsBuilt => _catalog != null;

        internal bool IsHolding => _holding;

        /// <summary>The terrain's own tree LOD bias, given back on release.</summary>
        internal float TerrainBias => _terrainBias;

        /// <summary>Indirect draws issued last frame.</summary>
        internal int DrawCalls { get; private set; }

        /// <summary>Of <see cref="DrawCalls"/>, the draws issued for out-of-view shadows alone.</summary>
        internal int ShadowOnlyDraws { get; private set; }

        // The light the last cull used, for the cells' own shadow test (MarkReachable).
        private Vector3 _lightDirection;
        private float _shadowReachPerRadius;

        private void Start() => Build();

        internal void Build()
        {
            _terrain = GetComponent<Terrain>();
            string reason = null;
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) reason = "no compute shaders or instancing";
            // The tree shaders read three structured buffers in the vertex stage
            // (TreeInstancing.cginc). A device that reports compute but no vertex-stage buffers
            // would draw every tree as nothing, with the terrain's own trees already culled.
            else if (SystemInfo.maxComputeBufferInputsVertex < VertexBufferInputs) reason = $"{SystemInfo.maxComputeBufferInputsVertex} vertex-stage buffers, {VertexBufferInputs} needed";
            else if ((_culling = Resources.Load<ComputeShader>("TreeCulling")) == null) reason = "no TreeCulling compute shader";
            else _catalog = TreeCatalog.TryBuild(_terrain, out reason);

            if (_catalog == null)
            {
                Debug.Log($"[trees] '{name}': the terrain keeps drawing its trees ({reason}).");
                enabled = false;
                return;
            }

            _terrainBias = _terrain.treeLODBiasMultiplier;
            Upload();
            _builtFrame = Time.frameCount;
            Debug.Log($"[trees] '{name}': {_catalog.Count} trees are culled and drawn on the GPU, in up to {_commands.Length} draws.");
        }

        private void LateUpdate()
        {
            if (Time.frameCount <= _builtFrame) return;
            Frame(Camera.main);
        }

        private void OnDisable() => Release();

        private void OnDestroy()
        {
            _trees?.Dispose();
            _objectToWorld?.Dispose();
            _worldToObject?.Dispose();
            _lodSquaredDistances?.Dispose();
            _lodCounts?.Dispose();
            _bucketStarts?.Dispose();
            _bucketCounts?.Dispose();
            _visible?.Dispose();
            _commandBuckets?.Dispose();
            _args?.Release();
        }

        /// <summary>Culls and draws every tree <paramref name="viewer"/> would see, or hands them back.</summary>
        internal void Frame(Camera viewer)
        {
            using (FrameMarker.Auto())
            {
                DrawCalls = 0;
                ShadowOnlyDraws = 0;
                if (_catalog == null || viewer == null || !_terrain.drawTreesAndFoliage)
                {
                    Release();
                    return;
                }

                Hold();
                Vector3 eye = viewer.transform.position;
                float shadowRange = QualitySettings.shadows == ShadowQuality.Disable
                    ? 0f
                    : QualitySettings.shadowDistance * ShadowMargin + ShadowReachMetres;
                Cull(viewer, eye, shadowRange);
                Draw(eye, shadowRange);
            }
        }

        /// <summary>How many trees the last cull kept for one prototype, LOD and pass. Reads the GPU back.</summary>
        internal int Kept(int prototype, int lod, Pass pass)
        {
            var counts = new uint[_bucketCount];
            _bucketCounts.GetData(counts);
            return (int)counts[Bucket(prototype, lod, pass)];
        }

        /// <summary>How many trees the last cull kept casting a shadow (<paramref name="shadowed"/>) or not.</summary>
        internal int Kept(int prototype, int lod, bool shadowed) =>
            shadowed
                ? Kept(prototype, lod, Pass.Shadowed) + Kept(prototype, lod, Pass.ShadowOnly)
                : Kept(prototype, lod, Pass.Unshadowed);

        private static int Bucket(int prototype, int lod, Pass pass) =>
            (prototype * TreeCatalog.MaxLods + lod) * Passes + (int)pass;

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

        private void Cull(Camera viewer, Vector3 eye, float shadowRange)
        {
            float lodBias = QualitySettings.lodBias * _terrainBias;
            float tanHalfFov = Mathf.Tan(viewer.fieldOfView * 0.5f * Mathf.Deg2Rad);
            for (int p = 0; p < _catalog.Prototypes.Length; p++)
            {
                TreeCatalog.Prototype prototype = _catalog.Prototypes[p];
                TreeLod.SquaredDistances(prototype.Thresholds, prototype.Size, tanHalfFov, lodBias, _lodSquared[p]);
                System.Array.Copy(_lodSquared[p], 0, _lodTable, p * TreeCatalog.MaxLods, prototype.Thresholds.Length);
            }
            _lodSquaredDistances.SetData(_lodTable);

            GeometryUtility.CalculateFrustumPlanes(viewer, _planes);
            for (int i = 0; i < _planes.Length; i++)
                _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);

            _culling.SetVector(EyeId, eye);
            _culling.SetFloat(ShadowRange2Id, shadowRange * shadowRange);
            _culling.SetVectorArray(PlanesId, _planeVectors);
            SetShadowLight(shadowRange);
            _culling.Dispatch(_clearKernel, Groups(_bucketCount), 1, 1);
            _culling.Dispatch(_cullKernel, Groups(_catalog.Count), 1, 1);
            _culling.Dispatch(_writeArgsKernel, Groups(_commands.Length), 1, 1);
        }

        /// <summary>
        /// Hands the cull the light the shadows come from: <see cref="RenderSettings.sun"/>, which
        /// is the moon in Night Mode. With none, or none casting shadows, every tree in the shadow
        /// range is kept for its shadow, as before.
        /// </summary>
        private void SetShadowLight(float shadowRange)
        {
            Light sun = RenderSettings.sun;
            bool known = sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional
                         && sun.shadows != LightShadows.None && shadowRange > 0f;
            Vector3 direction = known ? sun.transform.forward : Vector3.down;
            float sine = Mathf.Max(-direction.y, LowestLightSine);
            _lightDirection = direction;
            _shadowReachPerRadius = known ? 2f / sine : 0f;
            _culling.SetVector(LightDirectionId, direction);
            _culling.SetFloat(ShadowReachPerRadiusId, _shadowReachPerRadius);
            _culling.SetFloat(ShadowRangeId, shadowRange);
        }

        private void Draw(Vector3 eye, float shadowRange)
        {
            MarkReachable(eye, shadowRange * shadowRange);
            for (int c = 0; c < _commands.Length; c++)
            {
                Command command = _commands[c];
                if (!_reachable[command.Bucket]) continue;

                TreeCatalog.Part part = command.Part;
                Graphics.DrawMeshInstancedIndirect(
                    part.Mesh, part.Submesh, part.Material, _bounds, _args, c * ArgsPerCommand * sizeof(uint),
                    command.Properties, CastingFor(command.Pass, part.ShadowCasting),
                    part.ReceiveShadows, part.Layer, null, part.LightProbes);
                DrawCalls++;
                if (command.Pass == Pass.ShadowOnly) ShadowOnlyDraws++;
            }
        }

        /// <summary>
        /// Marks the buckets a tree of some cell can fall into this frame, so a draw is issued only
        /// for those: an indirect draw of no instances still costs the render thread a draw call
        /// in every pass, and the 114 issued a frame took a 100-bot match to 1,600-2,000 batches
        /// against 600-750 before this renderer.
        /// </summary>
        /// <remarks>
        /// A cell's trees lie between its bounds' nearest and farthest points, and between its
        /// prototypes' smallest and largest scales, so they can only be on the LODs from the one
        /// the nearest point picks for the largest tree to the one the farthest picks for the
        /// smallest. Shadowed buckets need a cell reaching inside the shadow range; unshadowed ones
        /// a cell reaching past it, in view.
        /// </remarks>
        private void MarkReachable(Vector3 eye, float shadow2)
        {
            System.Array.Clear(_reachable, 0, _reachable.Length);
            foreach (TreeCatalog.Cell cell in _catalog.Cells)
            {
                if (cell.Prototypes == 0) continue;
                float near2 = cell.Bounds.SqrDistance(eye);
                float far2 = FarthestSquared(cell.Bounds, eye);
                bool inView = GeometryUtility.TestPlanesAABB(_planes, cell.Bounds);
                bool inRange = near2 <= shadow2;
                bool shadowed = inRange && inView;
                // Out of view, a cell can still hold trees whose shadows fall into it -- unless all
                // of it is in view, or its shadow, the cell swept along the light, misses the view.
                bool shadowOnly = inRange && !Contains(_planes, cell.Bounds) && ShadowCanReachView(cell.Bounds, Mathf.Sqrt(shadow2));
                bool unshadowed = far2 > shadow2 && inView;
                if (!shadowed && !shadowOnly && !unshadowed) continue;

                for (int p = 0; p < _catalog.Prototypes.Length; p++)
                {
                    if ((cell.Prototypes & (1u << p)) == 0) continue;
                    TreeCatalog.Prototype prototype = _catalog.Prototypes[p];
                    int finest = TreeLod.Select(near2, prototype.LargestSquaredScale, _lodSquared[p]);
                    if (finest < 0) continue;
                    int coarsest = TreeLod.Select(far2, prototype.SmallestSquaredScale, _lodSquared[p]);
                    if (coarsest < 0) coarsest = prototype.Lods.Length - 1;
                    for (int lod = finest; lod <= coarsest; lod++)
                    {
                        if (shadowed) _reachable[Bucket(p, lod, Pass.Shadowed)] = true;
                        if (shadowOnly) _reachable[Bucket(p, lod, Pass.ShadowOnly)] = true;
                        if (unshadowed) _reachable[Bucket(p, lod, Pass.Unshadowed)] = true;
                    }
                }
            }
        }

        /// <summary>Whether all of <paramref name="bounds"/> lies inside every plane.</summary>
        internal static bool Contains(Plane[] planes, Bounds bounds)
        {
            Vector3 min = bounds.min, max = bounds.max;
            foreach (Plane plane in planes)
            {
                Vector3 n = plane.normal;
                // The corner farthest behind the plane: inside it, so is the whole box.
                var corner = new Vector3(n.x >= 0f ? min.x : max.x, n.y >= 0f ? min.y : max.y, n.z >= 0f ? min.z : max.z);
                if (plane.GetDistanceToPoint(corner) < 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether the shadow of anything in <paramref name="bounds"/> can fall in view: the box
        /// swept along the light by the longest shadow a tree in it can throw meets the view. With
        /// no light known, it can.
        /// </summary>
        private bool ShadowCanReachView(Bounds bounds, float shadowRange)
        {
            if (_shadowReachPerRadius <= 0f) return true;
            float tallest = _tallestRadius * _shadowReachPerRadius;
            Vector3 sweep = _lightDirection * Mathf.Min(tallest, shadowRange);
            var swept = new Bounds(bounds.center, bounds.size);
            swept.Encapsulate(bounds.min + sweep);
            swept.Encapsulate(bounds.max + sweep);
            return GeometryUtility.TestPlanesAABB(_planes, swept);
        }

        /// <summary>How a part is drawn in <paramref name="pass"/>, given how its renderer casts.</summary>
        internal static ShadowCastingMode CastingFor(Pass pass, ShadowCastingMode authored)
        {
            switch (pass)
            {
                case Pass.Shadowed: return authored;
                case Pass.ShadowOnly: return authored == ShadowCastingMode.Off ? ShadowCastingMode.Off : ShadowCastingMode.ShadowsOnly;
                default: return ShadowCastingMode.Off;
            }
        }

        private static float FarthestSquared(Bounds bounds, Vector3 point)
        {
            Vector3 min = bounds.min, max = bounds.max;
            float x = Mathf.Max(Mathf.Abs(point.x - min.x), Mathf.Abs(point.x - max.x));
            float y = Mathf.Max(Mathf.Abs(point.y - min.y), Mathf.Abs(point.y - max.y));
            float z = Mathf.Max(Mathf.Abs(point.z - min.z), Mathf.Abs(point.z - max.z));
            return x * x + y * y + z * z;
        }

        private void Upload()
        {
            TreeCatalog catalog = _catalog;
            int count = catalog.Count;
            var trees = new GpuTree[count];
            var inverse = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                trees[i] = new GpuTree
                {
                    Reference = catalog.References[i],
                    SquaredScale = catalog.SquaredScales[i],
                    Radius = catalog.Radii[i],
                    Prototype = catalog.PrototypeOf[i],
                };
                inverse[i] = catalog.ObjectToWorld[i].inverse;
            }
            _trees = Structured(count, Marshal.SizeOf<GpuTree>());
            _trees.SetData(trees);
            _objectToWorld = Structured(count, 64);
            _objectToWorld.SetData(catalog.ObjectToWorld);
            _worldToObject = Structured(count, 64);
            _worldToObject.SetData(inverse);

            int prototypes = catalog.Prototypes.Length;
            _lodTable = new float[prototypes * TreeCatalog.MaxLods];
            _lodSquared = new float[prototypes][];
            for (int p = 0; p < prototypes; p++) _lodSquared[p] = new float[catalog.Prototypes[p].Lods.Length];
            _lodSquaredDistances = Structured(_lodTable.Length, sizeof(float));
            var lodCounts = new uint[prototypes];
            for (int p = 0; p < prototypes; p++) lodCounts[p] = (uint)catalog.Prototypes[p].Lods.Length;
            _lodCounts = Structured(prototypes, sizeof(uint));
            _lodCounts.SetData(lodCounts);

            // A bucket per (prototype, LOD, pass), each with room for every tree of its prototype.
            _bucketCount = prototypes * TreeCatalog.MaxLods * Passes;
            var starts = new uint[_bucketCount];
            uint capacity = 0;
            for (int p = 0; p < prototypes; p++)
                for (int lod = 0; lod < catalog.Prototypes[p].Lods.Length; lod++)
                    for (int s = 0; s < Passes; s++)
                    {
                        starts[Bucket(p, lod, (Pass)s)] = capacity;
                        capacity += (uint)catalog.Prototypes[p].Count;
                    }
            _reachable = new bool[_bucketCount];
            _bucketStarts = Structured(_bucketCount, sizeof(uint));
            _bucketStarts.SetData(starts);
            _bucketCounts = Structured(_bucketCount, sizeof(uint));
            _visible = Structured((int)capacity, sizeof(uint));

            var commands = new System.Collections.Generic.List<Command>();
            var args = new System.Collections.Generic.List<uint>();
            for (int p = 0; p < prototypes; p++)
                for (int lod = 0; lod < catalog.Prototypes[p].Lods.Length; lod++)
                    foreach (TreeCatalog.Part part in catalog.Prototypes[p].Lods[lod])
                        for (int s = 0; s < Passes; s++)
                        {
                            var pass = (Pass)s;
                            // A part that casts nothing has no shadow to draw out of view; in view,
                            // its Shadowed bucket draws it plainly.
                            if (pass == Pass.ShadowOnly && part.ShadowCasting == ShadowCastingMode.Off) continue;
                            int bucket = Bucket(p, lod, pass);
                            var properties = new MaterialPropertyBlock();
                            properties.SetBuffer("_TreeObjectToWorld", _objectToWorld);
                            properties.SetBuffer("_TreeWorldToObject", _worldToObject);
                            properties.SetBuffer("_TreeVisible", _visible);
                            properties.SetFloat("_TreeBucketStart", starts[bucket]);
                            commands.Add(new Command { Part = part, Prototype = p, Lod = lod, Pass = pass, Bucket = bucket, Properties = properties });
                            args.Add(part.Mesh.GetIndexCount(part.Submesh));
                            args.Add(0);
                            args.Add(part.Mesh.GetIndexStart(part.Submesh));
                            args.Add(part.Mesh.GetBaseVertex(part.Submesh));
                            args.Add(0);
                        }
            _commands = commands.ToArray();
            _args = new ComputeBuffer(args.Count, sizeof(uint), ComputeBufferType.IndirectArguments);
            _args.SetData(args);
            var commandBuckets = new uint[_commands.Length];
            for (int c = 0; c < _commands.Length; c++) commandBuckets[c] = (uint)_commands[c].Bucket;
            _commandBuckets = Structured(_commands.Length, sizeof(uint));
            _commandBuckets.SetData(commandBuckets);

            _clearKernel = _culling.FindKernel("Clear");
            _cullKernel = _culling.FindKernel("Cull");
            _writeArgsKernel = _culling.FindKernel("WriteArgs");
            _culling.SetBuffer(_clearKernel, BucketCountsId, _bucketCounts);
            _culling.SetBuffer(_cullKernel, TreesId, _trees);
            _culling.SetBuffer(_cullKernel, LodSquaredDistancesId, _lodSquaredDistances);
            _culling.SetBuffer(_cullKernel, LodCountsId, _lodCounts);
            _culling.SetBuffer(_cullKernel, BucketStartsId, _bucketStarts);
            _culling.SetBuffer(_cullKernel, BucketCountsId, _bucketCounts);
            _culling.SetBuffer(_cullKernel, VisibleId, _visible);
            _culling.SetBuffer(_writeArgsKernel, BucketCountsId, _bucketCounts);
            _culling.SetBuffer(_writeArgsKernel, CommandBucketsId, _commandBuckets);
            _culling.SetBuffer(_writeArgsKernel, ArgsId, _args);
            _culling.SetInt(TreeCountId, count);
            _culling.SetInt(BucketCountId, _bucketCount);
            _culling.SetInt(CommandCountId, _commands.Length);

            Vector3 origin = _terrain.GetPosition();
            Vector3 size = _terrain.terrainData.size;
            float tallest = 0f;
            foreach (TreeCatalog.Prototype prototype in catalog.Prototypes)
                tallest = Mathf.Max(tallest, prototype.Size * Mathf.Sqrt(prototype.LargestSquaredScale));
            _bounds = new Bounds(origin + size * 0.5f, size + new Vector3(tallest, tallest, tallest) * 2f);
            _tallestRadius = tallest * 0.5f;
        }

        private static GraphicsBuffer Structured(int count, int stride) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);

        private static int Groups(int count) => Mathf.Max(1, (count + ThreadGroupSize - 1) / ThreadGroupSize);
    }
}
