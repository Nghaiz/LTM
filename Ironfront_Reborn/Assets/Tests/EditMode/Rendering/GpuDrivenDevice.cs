using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// The set-up check of every test that drives a GPU-driven renderer on a real terrain.
    /// </summary>
    /// <remarks>
    /// A batchmode run with <c>-nographics</c> gets Unity's Null graphics device: no vertex-stage
    /// buffers, so the renderers refuse the terrain by design and hand it back. Those tests then
    /// cannot say anything, and are ignored with the reason rather than failed (22 of them read
    /// red that way on 2026-10-05, while all 531 passed on the GPU). A renderer refusing on a
    /// device that can run it still fails.
    /// </remarks>
    internal static class GpuDrivenDevice
    {
        internal static void RequireBuilt(bool built, int vertexBuffers, string terrain)
        {
            if (built) return;
            if (!SystemInfo.supportsComputeShaders || SystemInfo.maxComputeBufferInputsVertex < vertexBuffers)
                Assert.Ignore($"needs compute shaders and {vertexBuffers} vertex-stage buffer(s); the "
                              + $"{SystemInfo.graphicsDeviceType} device has {SystemInfo.maxComputeBufferInputsVertex} "
                              + "(run the tests without -nographics)");
            Assert.Fail($"Setup: the {terrain} terrain was refused");
        }
    }
}
