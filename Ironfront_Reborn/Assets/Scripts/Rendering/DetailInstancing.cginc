#ifndef IRONFRONT_DETAIL_INSTANCING_INCLUDED
#define IRONFRONT_DETAIL_INSTANCING_INCLUDED

// Procedural instancing for InstancedDetailRenderer: each instance of a detail draw is the detail
// the culling pass (DetailCulling.compute) put at that slot of the draw's bucket, placed as the
// terrain places it -- moved to its position, turned about Y by its rotation, scaled by its width
// across X and Z and its height along Y. Included only by the generated copies in
// Resources/InstancedDetails (ProceduralShaderCopy.Details), never by an original shader.

// The terrain lights its details with the main directional light and the ambient alone: a point
// light that is important to an object (forced per pixel, as Night Mode's are, or one of the
// brightest Auto lights, as a rocket's is) leaves its grass as dark as before (Editor A/B,
// 2026-10-05). The copies are compiled noforwardadd (ProceduralShaderCopy), which hands such
// lights to the base pass as vertex lights instead, so the base pass leaves them out too. The one
// light the terrain did apply and these do not is a point light the frame ranks per vertex anyway:
// a rocket's on Low, or past the pixel light count.
#undef VERTEXLIGHT_ON

#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
// DetailCulling.compute's Instance and InstancedDetailRenderer.GpuInstance: 24 bytes.
struct IronfrontDetailInstance
{
    float3 position;
    float rotation;
    float scaleXZ;
    float scaleY;
};

StructuredBuffer<IronfrontDetailInstance> _DetailInstances;
float _DetailBucketStart;
#endif

void DetailInstancingSetup()
{
#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
    IronfrontDetailInstance detail = _DetailInstances[(uint)_DetailBucketStart + unity_InstanceID];
    float s, c;
    sincos(detail.rotation, s, c);
    float w = detail.scaleXZ;
    float h = detail.scaleY;
    float3 p = detail.position;

    // Matrix4x4.TRS(p, Quaternion.Euler(0, rotation, 0), (w, h, w)), row by row.
    unity_ObjectToWorld = float4x4(
        c * w, 0, s * w, p.x,
        0, h, 0, p.y,
        -s * w, 0, c * w, p.z,
        0, 0, 0, 1);

    // Its inverse: the scale undone after the turn and the move.
    float iw = 1.0 / w;
    float ih = 1.0 / h;
    float3 r0 = float3(c * iw, 0, -s * iw);
    float3 r1 = float3(0, ih, 0);
    float3 r2 = float3(s * iw, 0, c * iw);
    unity_WorldToObject = float4x4(
        r0.x, r0.y, r0.z, -dot(r0, p),
        r1.x, r1.y, r1.z, -dot(r1, p),
        r2.x, r2.y, r2.z, -dot(r2, p),
        0, 0, 0, 1);
#endif
}

#endif
