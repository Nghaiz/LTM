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
            public bool Shadowed;
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
        private float[] _lodScratch;
        private float[] _nearestSquared;
        private int _bucketCount;
        private Bounds _bounds;
        private float _terrainBias;
        private bool _holding;
        private int _builtFrame = -1;

        internal bool IsBuilt => _catalog != null;

        internal bool IsHolding => _holding;

        /// <summary>The terrain's own tree LOD bias, given back on release.</summary>
        internal float TerrainBias => _terrainBias;

        /// <summary>Indirect draws issued last frame.</summary>
        internal int DrawCalls { get; private set; }

        private void Start() => Build();

        internal void Build()
        {
            _terrain = GetComponent<Terrain>();
            string reason = null;
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) reason = "no compute shaders or instancing";
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

        /// <summary>How many trees the last cull kept for one prototype, LOD and shadow. Reads the GPU back.</summary>
        internal int Kept(int prototype, int lod, bool shadowed)
        {
            var counts = new uint[_bucketCount];
            _bucketCounts.GetData(counts);
            return (int)counts[Bucket(prototype, lod, shadowed)];
        }

        private static int Bucket(int prototype, int lod, bool shadowed) =>
            (prototype * TreeCatalog.MaxLods + lod) * 2 + (shadowed ? 1 : 0);

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
                TreeLod.SquaredDistances(prototype.Thresholds, prototype.Size, tanHalfFov, lodBias, _lodScratch);
                System.Array.Copy(_lodScratch, 0, _lodTable, p * TreeCatalog.MaxLods, prototype.Thresholds.Length);
            }
            _lodSquaredDistances.SetData(_lodTable);

            GeometryUtility.CalculateFrustumPlanes(viewer, _planes);
            for (int i = 0; i < _planes.Length; i++)
                _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);

            _culling.SetVector(EyeId, eye);
            _culling.SetFloat(ShadowRange2Id, shadowRange * shadowRange);
            _culling.SetVectorArray(PlanesId, _planeVectors);
            _culling.Dispatch(_clearKernel, Groups(_bucketCount), 1, 1);
            _culling.Dispatch(_cullKernel, Groups(_catalog.Count), 1, 1);
            _culling.Dispatch(_writeArgsKernel, Groups(_commands.Length), 1, 1);
        }

        private void Draw(Vector3 eye, float shadowRange)
        {
            // Only the draws a tree could reach: a prototype's finer LODs need a tree of it near the
            // camera, its shadows one inside the shadow range. The GPU decides the rest.
            for (int p = 0; p < _nearestSquared.Length; p++) _nearestSquared[p] = float.PositiveInfinity;
            foreach (TreeCatalog.Cell cell in _catalog.Cells)
            {
                if (cell.Prototypes == 0) continue;
                float distance2 = cell.Bounds.SqrDistance(eye);
                for (int p = 0; p < _nearestSquared.Length; p++)
                {
                    if ((cell.Prototypes & (1u << p)) != 0 && distance2 < _nearestSquared[p]) _nearestSquared[p] = distance2;
                }
            }

            float shadow2 = shadowRange * shadowRange;
            for (int c = 0; c < _commands.Length; c++)
            {
                Command command = _commands[c];
                float nearest2 = _nearestSquared[command.Prototype];
                if (command.Shadowed && nearest2 > shadow2) continue;
                float reach2 = _lodTable[command.Prototype * TreeCatalog.MaxLods + command.Lod]
                               * _catalog.Prototypes[command.Prototype].LargestSquaredScale;
                if (nearest2 > reach2) continue;

                TreeCatalog.Part part = command.Part;
                Graphics.DrawMeshInstancedIndirect(
                    part.Mesh, part.Submesh, part.Material, _bounds, _args, c * ArgsPerCommand * sizeof(uint),
                    command.Properties, command.Shadowed ? part.ShadowCasting : ShadowCastingMode.Off,
                    part.ReceiveShadows, part.Layer, null, part.LightProbes);
                DrawCalls++;
            }
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
            _lodScratch = new float[TreeCatalog.MaxLods];
            _nearestSquared = new float[prototypes];
            _lodSquaredDistances = Structured(_lodTable.Length, sizeof(float));
            var lodCounts = new uint[prototypes];
            for (int p = 0; p < prototypes; p++) lodCounts[p] = (uint)catalog.Prototypes[p].Lods.Length;
            _lodCounts = Structured(prototypes, sizeof(uint));
            _lodCounts.SetData(lodCounts);

            // A bucket per (prototype, LOD, shadowed), each with room for every tree of its prototype.
            _bucketCount = prototypes * TreeCatalog.MaxLods * 2;
            var starts = new uint[_bucketCount];
            uint capacity = 0;
            for (int p = 0; p < prototypes; p++)
                for (int lod = 0; lod < catalog.Prototypes[p].Lods.Length; lod++)
                    for (int s = 0; s < 2; s++)
                    {
                        starts[Bucket(p, lod, s == 1)] = capacity;
                        capacity += (uint)catalog.Prototypes[p].Count;
                    }
            _bucketStarts = Structured(_bucketCount, sizeof(uint));
            _bucketStarts.SetData(starts);
            _bucketCounts = Structured(_bucketCount, sizeof(uint));
            _visible = Structured((int)capacity, sizeof(uint));

            var commands = new System.Collections.Generic.List<Command>();
            var args = new System.Collections.Generic.List<uint>();
            for (int p = 0; p < prototypes; p++)
                for (int lod = 0; lod < catalog.Prototypes[p].Lods.Length; lod++)
                    foreach (TreeCatalog.Part part in catalog.Prototypes[p].Lods[lod])
                        for (int s = 0; s < 2; s++)
                        {
                            bool shadowed = s == 1;
                            if (shadowed && part.ShadowCasting == ShadowCastingMode.Off) continue;
                            int bucket = Bucket(p, lod, shadowed);
                            var properties = new MaterialPropertyBlock();
                            properties.SetBuffer("_TreeObjectToWorld", _objectToWorld);
                            properties.SetBuffer("_TreeWorldToObject", _worldToObject);
                            properties.SetBuffer("_TreeVisible", _visible);
                            properties.SetFloat("_TreeBucketStart", starts[bucket]);
                            commands.Add(new Command { Part = part, Prototype = p, Lod = lod, Shadowed = shadowed, Bucket = bucket, Properties = properties });
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
        }

        private static GraphicsBuffer Structured(int count, int stride) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);

        private static int Groups(int count) => Mathf.Max(1, (count + ThreadGroupSize - 1) / ThreadGroupSize);
    }
}
