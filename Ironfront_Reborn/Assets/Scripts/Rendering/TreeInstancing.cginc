#ifndef IRONFRONT_TREE_INSTANCING_INCLUDED
#define IRONFRONT_TREE_INSTANCING_INCLUDED

// Procedural instancing for InstancedTreeRenderer: each instance of a tree draw is the tree the
// culling pass (TreeCulling.compute) put at that slot of the draw's bucket, placed by the
// matrices uploaded once from the terrain's tree instances. Included only by the generated
// copies in Resources/InstancedTrees (TreeShaderVariants), never by an original shader.

#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
StructuredBuffer<float4x4> _TreeObjectToWorld;
StructuredBuffer<float4x4> _TreeWorldToObject;
StructuredBuffer<uint> _TreeVisible;
float _TreeBucketStart;
#endif

void TreeInstancingSetup()
{
#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
    uint tree = _TreeVisible[(uint)_TreeBucketStart + unity_InstanceID];
    unity_ObjectToWorld = _TreeObjectToWorld[tree];
    unity_WorldToObject = _TreeWorldToObject[tree];
#endif
}

#endif
