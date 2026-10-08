using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Ironfront.Rendering
{
    /// <summary>
    /// Draws a terrain's details -- Forest Lake's grass, ferns, blueberries, flowers and rocks --
    /// the way <see cref="InstancedTreeRenderer"/> draws its trees: the instances on the GPU, a
    /// compute pass keeping the ones in view or able to shadow it, and one indirect draw per
    /// prototype and pass, where the terrain issued about 1,200 draw calls a frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why (P33).</b> On 10+ players' machines the client takes the whole CPU, and the CPU goes
    /// to submitting draws: main thread 0.94 cores, render thread 0.79, the driver's thread 0.64
    /// (v4.2.0, Forest Lake, 100 bots). A Frame Debugger walk of one frame found 1,232 of its 2,354
    /// events were terrain details, 511 instances a call with their matrices uploaded again for
    /// every pass: the main pass, the camera depth texture and each shadow cascade.
    /// </para>
    /// <para>
    /// <b>The same details, at the same distance.</b> Each instance is the terrain's own
    /// (<see cref="DetailPatchCache"/>). The terrain draws a 64-cell patch whole once its bounds come
    /// within the detail distance, however far its farthest instance lies (measured: instances
    /// 40 m past the distance shown in 54-72% of samples when their patch is near, 5% when it is
    /// not); the same rule picks the patches here. Inside them the GPU culls each instance against
    /// the view, which no one can see, and keeps for the shadow passes those within the shadow range
    /// that are in view or can throw a shadow into it (<c>DetailCulling.compute</c>). Distance and
    /// density follow the quality preset exactly as the terrain's do (<see cref="TerrainDetailHandOff"/>).
    /// </para>
    /// <para>
    /// <b>Fair to every machine.</b> The work leaves the CPU without loading the GPU more: every
    /// instance of a near patch was already drawn in the main pass and the depth texture, and now
    /// only those in view are; the compute pass reads 20 bytes a near instance. Patches are read
    /// ahead of the camera within <see cref="BakeBudgetMs"/> a frame, and only those near it are
    /// kept (a few MB of GPU memory), which suits integrated graphics sharing the system's RAM.
    /// </para>
    /// <para>
    /// <b>The terrain keeps them whenever this cannot match it:</b> without a camera, with
    /// vegetation off, on a machine without compute shaders, on a terrain with a prototype this
    /// cannot draw (<see cref="DetailCatalog"/>), and while the patches it would draw are still being
    /// read -- from the first frames of a map (so the minimap's one-off snapshot sees what it saw
    /// before) and after a density change. Each refusal says why once.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class InstancedDetailRenderer : MonoBehaviour
    {
        /// <summary>Patches within the detail distance plus this are read ahead of need.</summary>
        internal const float PrefetchMetres = 50f;

        /// <summary>Patches past the detail distance plus this are dropped.</summary>
        internal const float EvictMetres = 100f;

        /// <summary>CPU time a frame may spend reading patches ahead of need (at least one is read).</summary>
        internal const double BakeBudgetMs = 1.0;

        /// <summary>
        /// CPU time a frame may spend, in all, reading patches round <see cref="PrefetchPoint"/>: it
        /// is set while the player waits to deploy, when a frame has little else to do.
        /// </summary>
        internal const double PrefetchBudgetMs = 3.0;

        /// <summary>
        /// Where the camera is about to jump to, when that is known: the spawn point the player
        /// picked on the loadout screen, set by the player's controller while they wait to deploy,
        /// null otherwise.
        /// </summary>
        /// <remarks>
        /// A respawn moves the camera hundreds of metres, every patch the terrain would draw there
        /// is new, and they cannot wait (<see cref="Stream"/>): each deploy cost one frame of 200 to
        /// 840 ms on the owner's Ultra machine (phase P35, finding 1; 25 deploys and 25 such frames
        /// in the v4.5.0 playtest of 2026-10-07). The wait on the loadout screen lasts 2 to 17
        /// seconds, so those patches are read during it instead, nearest the spawn point first, and
        /// kept until the jump.
        /// </remarks>
        public static Vector3? PrefetchPoint { get; set; }

        /// <summary>Structured buffers the instanced detail shaders read per vertex.</summary>
        internal const int VertexBufferInputs = 1;

        private const int Group = 64;

        private static readonly ProfilerMarker FrameMarker = new ProfilerMarker("InstancedDetailRenderer.Frame");
        private static readonly int PoolId = Shader.PropertyToID("_Pool");
        private static readonly int PagesId = Shader.PropertyToID("_Pages");
        private static readonly int PrototypesId = Shader.PropertyToID("_Prototypes");
        private static readonly int BucketStartsId = Shader.PropertyToID("_BucketStarts");
        private static readonly int BucketCountsId = Shader.PropertyToID("_BucketCounts");
        private static readonly int InstancesId = Shader.PropertyToID("_Instances");
        private static readonly int CommandBucketsId = Shader.PropertyToID("_CommandBuckets");
        private static readonly int ArgsId = Shader.PropertyToID("_Args");
        private static readonly int PageCountId = Shader.PropertyToID("_PageCount");
        private static readonly int BucketCountId = Shader.PropertyToID("_BucketCount");
        private static readonly int CommandCountId = Shader.PropertyToID("_CommandCount");
        private static readonly int EyeId = Shader.PropertyToID("_Eye");
        private static readonly int ShadowRange2Id = Shader.PropertyToID("_ShadowRange2");
        private static readonly int PlanesId = Shader.PropertyToID("_Planes");
        private static readonly int LightDirectionId = Shader.PropertyToID("_LightDirection");
        private static readonly int ShadowReachPerMetreId = Shader.PropertyToID("_ShadowReachPerMetre");
        private static readonly int ShadowRangeId = Shader.PropertyToID("_ShadowRange");
        private static readonly int DetailInstancesId = Shader.PropertyToID("_DetailInstances");
        private static readonly int DetailBucketStartId = Shader.PropertyToID("_DetailBucketStart");

        /// <summary>What a page's patch can take part in this frame; <c>DetailCulling.compute</c>'s flags.</summary>
        [System.Flags]
        internal enum PageFlags : uint
        {
            InView = 1,
            Shadow = 2,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuPage
        {
            public uint PoolStart;
            public uint Count;
            public Vector2 ScaleMin;
            public Vector2 ScaleRange;
            public uint Prototype;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuPrototype
        {
            public float Radius;
            public float Height;
            public uint Casts;
            public uint Unused;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuInstance
        {
            public Vector3 Position;
            public float Rotation;
            public float ScaleXZ;
            public float ScaleY;
        }

        private struct Missing
        {
            public DetailPatchCache.Entry Entry;
            public float Distance;
        }

        private sealed class NearestFirst : IComparer<Missing>
        {
            internal static readonly NearestFirst Instance = new NearestFirst();
            public int Compare(Missing a, Missing b) => a.Distance.CompareTo(b.Distance);
        }

        private sealed class Command
        {
            public DetailCatalog.Part Part;
            public int Bucket;
            public bool Caster;
            public RenderParams Params;
        }

        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _planeVectors = new Vector4[6];
        private readonly List<Missing> _missing = new List<Missing>();
        private readonly List<Missing> _ahead = new List<Missing>();
        private readonly Stopwatch _watch = new Stopwatch();
        private Terrain _terrain;
        private DetailCatalog _catalog;
        private DetailPatchCache _cache;
        private TerrainDetailHandOff _handOff;
        private ComputeShader _culling;
        private int _clearKernel, _cullKernel, _writeArgsKernel;
        private GraphicsBuffer _pages, _prototypes, _bucketStarts, _bucketCounts, _instances, _commandBuckets, _args;
        private GpuPage[] _pageData = new GpuPage[256];
        private int _pageCount;
        private int _bucketCount;
        private uint[] _starts;
        private int[] _capacity;
        private Bounds[] _bucketBounds;
        private Command[] _commands;
        private float _shadowReach;
        private bool _shadows;
        private Vector3 _lightDirection;
        private float _reachPerMetre;
        private float _shadowRange;
        private int _builtFrame = -1;
        private bool _announced;

        internal bool IsBuilt => _catalog != null;

        internal bool IsHolding => _handOff != null && _handOff.IsHolding;

        internal TerrainDetailHandOff HandOff => _handOff;

        internal DetailPatchCache Cache => _cache;

        /// <summary>Indirect draw calls issued last frame.</summary>
        internal int DrawCalls { get; private set; }

        /// <summary>Of <see cref="DrawCalls"/>, the draws issued for the shadow passes alone.</summary>
        internal int ShadowOnlyDraws { get; private set; }

        /// <summary>Patches read from the terrain last frame.</summary>
        internal int Baked { get; private set; }

        /// <summary>Of <see cref="Baked"/>, the patches read round <see cref="PrefetchPoint"/>.</summary>
        internal int Prefetched { get; private set; }

        private void Start() => Build();

        internal void Build()
        {
            _terrain = GetComponent<Terrain>();
            string reason = null;
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) reason = "no compute shaders or instancing";
            // The detail shaders read a structured buffer in the vertex stage (DetailInstancing.cginc).
            else if (SystemInfo.maxComputeBufferInputsVertex < VertexBufferInputs) reason = $"{SystemInfo.maxComputeBufferInputsVertex} vertex-stage buffers, {VertexBufferInputs} needed";
            else if ((_culling = Resources.Load<ComputeShader>("DetailCulling")) == null) reason = "no DetailCulling compute shader";
            else _catalog = DetailCatalog.TryBuild(_terrain, out reason);

            if (_catalog == null)
            {
                Debug.Log($"[details] '{name}': the terrain keeps drawing its details ({reason}).");
                enabled = false;
                return;
            }

            _cache = new DetailPatchCache(_terrain, _catalog);
            _handOff = new TerrainDetailHandOff(_terrain);
            Upload();
            _builtFrame = Time.frameCount;
            Debug.Log($"[details] '{name}': {_catalog.Prototypes.Length} detail prototypes are drawn from near patches, in up to {_commands.Length} draws.");
        }

        private void LateUpdate()
        {
            if (Time.frameCount <= _builtFrame) return;
            Frame(Camera.main);
        }

        private void OnDisable() => _handOff?.Release();

        private void OnDestroy()
        {
            _handOff?.Release();
            _cache?.Dispose();
            _pages?.Dispose();
            _prototypes?.Dispose();
            _bucketStarts?.Dispose();
            _bucketCounts?.Dispose();
            _instances?.Dispose();
            _commandBuckets?.Dispose();
            _args?.Dispose();
        }

        /// <summary>Draws every detail <paramref name="viewer"/> would see, or hands them back to the terrain.</summary>
        internal void Frame(Camera viewer)
        {
            using (FrameMarker.Auto())
            {
                DrawCalls = 0;
                ShadowOnlyDraws = 0;
                Baked = 0;
                Prefetched = 0;
                if (_catalog == null || viewer == null || !_terrain.drawTreesAndFoliage)
                {
                    _handOff?.Release();
                    return;
                }

                _handOff.Adopt();
                float density = _handOff.Density;
                if (density != _cache.Density)
                {
                    // Another density is another set of details: the terrain draws them while they are read.
                    _handOff.Release();
                    _cache.Reset(density);
                }

                Vector3 eye = viewer.transform.position;
                float distance = _handOff.Distance;
                // At a detail distance of 0 the terrain draws none, even the patch the camera
                // stands in (measured), and neither does this.
                if (distance <= 0f || !Stream(eye, distance))
                {
                    _handOff.Release();
                    return;
                }

                _handOff.Hold();
                Cull(viewer, eye, distance);
                Draw();
                if (!_announced && DrawCalls > 0)
                {
                    _announced = true;
                    Debug.Log($"[details] '{name}': taken over from the terrain, {_cache.Resident.Count} patch prototypes read, {DrawCalls} draws.");
                }
            }
        }

        /// <summary>How many details the last cull kept for a prototype's visible or caster bucket. Reads the GPU back.</summary>
        internal int Kept(int prototype, bool caster)
        {
            if (_bucketCounts == null || _pageCount == 0) return 0;
            var counts = new uint[_bucketCount];
            _bucketCounts.GetData(counts);
            return (int)counts[prototype * 2 + (caster ? 1 : 0)];
        }

        /// <summary>
        /// Drops the patches far behind, reads the ones the terrain would draw now and those ahead,
        /// and says whether everything the terrain would draw now is read.
        /// </summary>
        /// <remarks>
        /// While this draws, a patch the terrain would draw right now cannot wait: it is read at
        /// once, as the terrain itself would have (a teleport or a respawn). Otherwise reading is
        /// spread, nearest first, over frames within <see cref="BakeBudgetMs"/>, and until it is
        /// done the terrain keeps drawing, so nothing is ever missing from view.
        /// </remarks>
        private bool Stream(Vector3 eye, float distance)
        {
            float reach = distance + PrefetchMetres;
            Vector3? ahead = PrefetchPoint;
            IReadOnlyList<DetailPatchCache.Entry> resident = _cache.Resident;
            for (int i = resident.Count - 1; i >= 0; i--)
            {
                DetailPatchCache.Entry entry = resident[i];
                if (SquareDistance(entry.PatchX, entry.PatchZ, eye) <= distance + EvictMetres) continue;
                // What was read for the jump is kept for it.
                if (ahead.HasValue && SquareDistance(entry.PatchX, entry.PatchZ, ahead.Value) <= reach) continue;
                _cache.Evict(entry);
            }

            _watch.Restart();
            bool ready = true;
            Collect(eye, reach, _missing);
            if (_missing.Count > 0)
            {
                _missing.Sort(NearestFirst.Instance);
                bool holding = _handOff.IsHolding;
                foreach (Missing missing in _missing)
                {
                    bool needed = missing.Distance <= distance;
                    bool urgent = holding && needed;
                    if (!urgent && Baked > 0 && _watch.Elapsed.TotalMilliseconds >= BakeBudgetMs)
                    {
                        if (needed) ready = false;
                        continue;
                    }
                    _cache.Bake(missing.Entry);
                    Baked++;
                }
            }

            if (ahead.HasValue && _watch.Elapsed.TotalMilliseconds < PrefetchBudgetMs)
            {
                Collect(ahead.Value, reach, _ahead);
                _ahead.Sort(NearestFirst.Instance);
                foreach (Missing missing in _ahead)
                {
                    if (_watch.Elapsed.TotalMilliseconds >= PrefetchBudgetMs) break;
                    if (missing.Entry.Baked) continue;
                    _cache.Bake(missing.Entry);
                    Baked++;
                    Prefetched++;
                }
            }
            return ready;
        }

        /// <summary>
        /// The patches not yet read within <paramref name="reach"/> of <paramref name="centre"/>,
        /// into <paramref name="into"/>.
        /// </summary>
        private void Collect(Vector3 centre, float reach, List<Missing> into)
        {
            into.Clear();
            Vector2 size = _cache.PatchSize;
            Vector2 start = _cache.SquareMin(0, 0);
            float margin = reach + _catalog.Reach;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - margin - start.x) / size.x));
            int x1 = Mathf.Min(_cache.Patches - 1, Mathf.FloorToInt((centre.x + margin - start.x) / size.x));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((centre.z - margin - start.y) / size.y));
            int z1 = Mathf.Min(_cache.Patches - 1, Mathf.FloorToInt((centre.z + margin - start.y) / size.y));
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float away = SquareDistance(x, z, centre);
                    if (away > reach) continue;
                    for (int p = 0; p < _catalog.Prototypes.Length; p++)
                    {
                        DetailPatchCache.Entry entry = _cache.Get(x, z, p);
                        if (!entry.Baked) into.Add(new Missing { Entry = entry, Distance = away });
                    }
                }
        }

        /// <summary>
        /// The least distance on the ground from <paramref name="eye"/> to the patch's square,
        /// widened by the farthest a detail reaches past it: never more than the terrain's own
        /// distance to the patch's bounds, so a patch it would draw is never skipped.
        /// </summary>
        private float SquareDistance(int patchX, int patchZ, Vector3 eye)
        {
            Vector2 min = _cache.SquareMin(patchX, patchZ) - new Vector2(_catalog.Reach, _catalog.Reach);
            Vector2 max = _cache.SquareMin(patchX, patchZ) + _cache.PatchSize + new Vector2(_catalog.Reach, _catalog.Reach);
            float dx = Mathf.Max(0f, Mathf.Max(min.x - eye.x, eye.x - max.x));
            float dz = Mathf.Max(0f, Mathf.Max(min.y - eye.z, eye.z - max.y));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void Cull(Camera viewer, Vector3 eye, float distance)
        {
            GeometryUtility.CalculateFrustumPlanes(viewer, _planes);
            for (int i = 0; i < _planes.Length; i++)
                _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
            SetShadowLight();

            System.Array.Clear(_capacity, 0, _capacity.Length);
            _pageCount = 0;
            float distance2 = distance * distance;
            float shadow2 = _shadowRange * _shadowRange;

            foreach (DetailPatchCache.Entry entry in _cache.Resident)
            {
                // The terrain draws a patch whole once its bounds come within the detail distance.
                if (entry.Count == 0 || entry.Bounds.SqrDistance(eye) > distance2) continue;

                DetailCatalog.Prototype prototype = _catalog.Prototypes[entry.Prototype];
                Bounds bounds = entry.Bounds;
                bounds.Expand(2f * prototype.Radius * prototype.MaxScale);
                bool inView = GeometryUtility.TestPlanesAABB(_planes, bounds);
                bool shadow = _shadows && prototype.Casts && bounds.SqrDistance(eye) <= shadow2
                              && (inView || ShadowCanReachView(bounds, prototype));
                if (!inView && !shadow) continue;

                uint flags = (inView ? (uint)PageFlags.InView : 0u) | (shadow ? (uint)PageFlags.Shadow : 0u);
                for (int i = 0; i < entry.Pages.Length; i++)
                {
                    int count = Mathf.Min(DetailPatchCache.PageSize, entry.Count - i * DetailPatchCache.PageSize);
                    if (_pageCount == _pageData.Length) System.Array.Resize(ref _pageData, _pageData.Length * 2);
                    _pageData[_pageCount++] = new GpuPage
                    {
                        PoolStart = (uint)(entry.Pages[i] * DetailPatchCache.PageSize),
                        Count = (uint)count,
                        ScaleMin = entry.ScaleMin,
                        ScaleRange = entry.ScaleRange,
                        Prototype = (uint)entry.Prototype,
                        Flags = flags,
                    };
                }
                if (inView) Grow(entry.Prototype * 2, entry.Count, bounds);
                if (shadow) Grow(entry.Prototype * 2 + 1, entry.Count, bounds);
            }

            if (_pageCount == 0) return;
            Dispatch(eye);
        }

        /// <summary>Adds <paramref name="count"/> details and their bounds to a bucket's room for this frame.</summary>
        private void Grow(int bucket, int count, Bounds bounds)
        {
            if (_capacity[bucket] == 0) _bucketBounds[bucket] = bounds; else _bucketBounds[bucket].Encapsulate(bounds);
            _capacity[bucket] += count;
        }

        private void Dispatch(Vector3 eye)
        {
            uint total = 0;
            for (int b = 0; b < _bucketCount; b++)
            {
                _starts[b] = total;
                total += (uint)_capacity[b];
            }
            EnsureInstances((int)total);

            if (_pages == null || _pages.count < _pageCount)
            {
                _pages?.Dispose();
                _pages = Structured(_pageData.Length, Marshal.SizeOf<GpuPage>());
            }
            _pages.SetData(_pageData, 0, 0, _pageCount);
            _bucketStarts.SetData(_starts);

            _culling.SetBuffer(_clearKernel, BucketCountsId, _bucketCounts);
            _culling.SetBuffer(_cullKernel, PoolId, _cache.Pool);
            _culling.SetBuffer(_cullKernel, PagesId, _pages);
            _culling.SetBuffer(_cullKernel, PrototypesId, _prototypes);
            _culling.SetBuffer(_cullKernel, BucketStartsId, _bucketStarts);
            _culling.SetBuffer(_cullKernel, BucketCountsId, _bucketCounts);
            _culling.SetBuffer(_cullKernel, InstancesId, _instances);
            _culling.SetBuffer(_writeArgsKernel, BucketCountsId, _bucketCounts);
            _culling.SetBuffer(_writeArgsKernel, CommandBucketsId, _commandBuckets);
            _culling.SetBuffer(_writeArgsKernel, ArgsId, _args);
            _culling.SetInt(PageCountId, _pageCount);
            _culling.SetInt(BucketCountId, _bucketCount);
            _culling.SetInt(CommandCountId, _commands.Length);
            _culling.SetVector(EyeId, eye);
            _culling.SetFloat(ShadowRange2Id, _shadowRange * _shadowRange);
            _culling.SetVectorArray(PlanesId, _planeVectors);
            _culling.SetVector(LightDirectionId, _lightDirection);
            _culling.SetFloat(ShadowReachPerMetreId, _reachPerMetre);
            _culling.SetFloat(ShadowRangeId, _shadowRange);

            _culling.Dispatch(_clearKernel, Groups(_bucketCount), 1, 1);
            _culling.Dispatch(_cullKernel, DetailPatchCache.PageSize / Group, _pageCount, 1);
            if (_commands.Length > 0) _culling.Dispatch(_writeArgsKernel, Groups(_commands.Length), 1, 1);
        }

        /// <summary>
        /// The light the shadows come from: <see cref="RenderSettings.sun"/>, the moon in Night Mode.
        /// With none, or none casting shadows, every caster in the shadow range is kept, as before.
        /// </summary>
        private void SetShadowLight()
        {
            _shadows = QualitySettings.shadows != ShadowQuality.Disable && QualitySettings.shadowDistance > 0f;
            _shadowRange = _shadows ? QualitySettings.shadowDistance * InstancedTreeRenderer.ShadowMargin + _shadowReach : 0f;
            Light sun = RenderSettings.sun;
            bool known = _shadows && sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional
                         && sun.shadows != LightShadows.None;
            _lightDirection = known ? sun.transform.forward : Vector3.down;
            float sine = Mathf.Max(-_lightDirection.y, InstancedTreeRenderer.LowestLightSine);
            _reachPerMetre = known ? 1f / sine : 0f;
        }

        /// <summary>Whether a shadow thrown by anything in <paramref name="bounds"/> can fall in view.</summary>
        private bool ShadowCanReachView(Bounds bounds, DetailCatalog.Prototype prototype)
        {
            if (_reachPerMetre <= 0f) return true;
            float tallest = prototype.Height * prototype.MaxScale;
            return InstancedTreeRenderer.SweepMeetsView(_planes, bounds, _lightDirection * Mathf.Min(tallest * _reachPerMetre, _shadowRange));
        }

        private void Draw()
        {
            if (_pageCount == 0) return;
            for (int c = 0; c < _commands.Length; c++)
            {
                Command command = _commands[c];
                if (_capacity[command.Bucket] == 0) continue;
                RenderParams parameters = command.Params;
                parameters.worldBounds = _bucketBounds[command.Bucket];
                parameters.matProps.SetFloat(DetailBucketStartId, _starts[command.Bucket]);
                Graphics.RenderMeshIndirect(parameters, command.Part.Mesh, _args, 1, c);
                DrawCalls++;
                if (command.Caster) ShadowOnlyDraws++;
            }
        }

        private void EnsureInstances(int count)
        {
            if (_instances != null && _instances.count >= count) return;
            _instances?.Dispose();
            _instances = Structured(Mathf.NextPowerOfTwo(Mathf.Max(count, 4096)), Marshal.SizeOf<GpuInstance>());
            foreach (Command command in _commands) command.Params.matProps.SetBuffer(DetailInstancesId, _instances);
        }

        private void Upload()
        {
            DetailCatalog.Prototype[] prototypes = _catalog.Prototypes;
            _bucketCount = prototypes.Length * 2;
            _starts = new uint[_bucketCount];
            _capacity = new int[_bucketCount];
            _bucketBounds = new Bounds[_bucketCount];

            var gpuPrototypes = new GpuPrototype[prototypes.Length];
            var commands = new List<Command>();
            var args = new List<GraphicsBuffer.IndirectDrawIndexedArgs>();
            for (int p = 0; p < prototypes.Length; p++)
            {
                DetailCatalog.Prototype prototype = prototypes[p];
                gpuPrototypes[p] = new GpuPrototype { Radius = prototype.Radius, Height = prototype.Height, Casts = prototype.Casts ? 1u : 0u };
                _shadowReach = Mathf.Max(_shadowReach, prototype.Casts ? prototype.Height * prototype.MaxScale / InstancedTreeRenderer.LowestLightSine : 0f);

                foreach (DetailCatalog.Part part in prototype.Parts)
                    for (int caster = 0; caster < 2; caster++)
                    {
                        // A prototype that casts nothing has no shadow pass to draw.
                        if (caster == 1 && !prototype.Casts) continue;
                        commands.Add(new Command
                        {
                            Part = part,
                            Bucket = p * 2 + caster,
                            Caster = caster == 1,
                            Params = new RenderParams(part.Material)
                            {
                                layer = prototype.GameObjectLayer,
                                renderingLayerMask = prototype.RenderingLayerMask,
                                // In view: drawn, its shadow drawn by the caster bucket. Casters:
                                // the shadow passes alone, in view or out of it.
                                shadowCastingMode = caster == 1 ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off,
                                receiveShadows = prototype.ReceiveShadows,
                                lightProbeUsage = prototype.LightProbes,
                                reflectionProbeUsage = prototype.ReflectionProbes,
                                matProps = new MaterialPropertyBlock(),
                            },
                        });
                        args.Add(new GraphicsBuffer.IndirectDrawIndexedArgs
                        {
                            indexCountPerInstance = part.Mesh.GetIndexCount(part.Submesh),
                            startIndex = part.Mesh.GetIndexStart(part.Submesh),
                            baseVertexIndex = part.Mesh.GetBaseVertex(part.Submesh),
                        });
                    }
            }

            _commands = commands.ToArray();
            _prototypes = Structured(prototypes.Length, Marshal.SizeOf<GpuPrototype>());
            _prototypes.SetData(gpuPrototypes);
            _bucketStarts = Structured(_bucketCount, sizeof(uint));
            _bucketCounts = Structured(_bucketCount, sizeof(uint));
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, Mathf.Max(1, _commands.Length), GraphicsBuffer.IndirectDrawIndexedArgs.size);
            if (_commands.Length > 0) _args.SetData(args);
            var commandBuckets = new uint[Mathf.Max(1, _commands.Length)];
            for (int c = 0; c < _commands.Length; c++) commandBuckets[c] = (uint)_commands[c].Bucket;
            _commandBuckets = Structured(commandBuckets.Length, sizeof(uint));
            _commandBuckets.SetData(commandBuckets);
            EnsureInstances(0);

            _clearKernel = _culling.FindKernel("Clear");
            _cullKernel = _culling.FindKernel("Cull");
            _writeArgsKernel = _culling.FindKernel("WriteArgs");
        }

        private static GraphicsBuffer Structured(int count, int stride) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);

        private static int Groups(int count) => Mathf.Max(1, (count + Group - 1) / Group);
    }
}
