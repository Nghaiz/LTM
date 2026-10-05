// Written by hand, not generated: the built-in "Standard" shader has no source in the project to
// copy (ProceduralShaderCopy), but its passes are thin wrappers over Unity's own CGIncludes, which
// ship with the Editor. This is its LOD 300 forward base and shadow caster passes, the same
// includes and the same keywords, with procedural instancing for InstancedDetailRenderer.
//
// Like the generated detail copies it lights details as the terrain does, with the main
// directional light and the ambient alone: no additive pass, and no vertex lights in the base
// pass (DetailInstancing.cginc). Forest Lake's rocks (MI_Rock_C, MI_Rock_D) use _NORMALMAP and
// nothing else; DetailCatalog draws on this copy only materials whose keywords it declares.
Shader "Hidden/Ironfront/InstancedDetails/Standard"
{
	Properties
	{
		_Color("Color", Color) = (1,1,1,1)
		_MainTex("Albedo", 2D) = "white" {}
		_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
		_Glossiness("Smoothness", Range(0.0, 1.0)) = 0.5
		_GlossMapScale("Smoothness Scale", Range(0.0, 1.0)) = 1.0
		[Enum(Metallic Alpha,0,Albedo Alpha,1)] _SmoothnessTextureChannel ("Smoothness texture channel", Float) = 0
		[Gamma] _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
		_MetallicGlossMap("Metallic", 2D) = "white" {}
		[ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
		[ToggleOff] _GlossyReflections("Glossy Reflections", Float) = 1.0
		_BumpScale("Scale", Float) = 1.0
		[Normal] _BumpMap("Normal Map", 2D) = "bump" {}
		_Parallax ("Height Scale", Range (0.005, 0.08)) = 0.02
		_ParallaxMap ("Height Map", 2D) = "black" {}
		_OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
		_OcclusionMap("Occlusion", 2D) = "white" {}
		_EmissionColor("Color", Color) = (0,0,0)
		_EmissionMap("Emission", 2D) = "white" {}
		_DetailMask("Detail Mask", 2D) = "white" {}
		_DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
		_DetailNormalMapScale("Scale", Float) = 1.0
		[Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}
		[Enum(UV0,0,UV1,1)] _UVSec ("UV Set for secondary textures", Float) = 0
		[HideInInspector] _Mode ("__mode", Float) = 0.0
		[HideInInspector] _SrcBlend ("__src", Float) = 1.0
		[HideInInspector] _DstBlend ("__dst", Float) = 0.0
		[HideInInspector] _ZWrite ("__zw", Float) = 1.0
	}

	CGINCLUDE
		#define UNITY_SETUP_BRDF_INPUT MetallicSetup
	ENDCG

	SubShader
	{
		Tags { "RenderType"="Opaque" "PerformanceChecks"="False" }
		LOD 300

		Pass
		{
			Name "FORWARD"
			// The main directional light and the ambient only, as the terrain lights its details:
			// no other light reaches the vertex lights or the ambient of this pass.
			Tags { "LightMode" = "ForwardBase" "PassFlags" = "OnlyDirectional" }
			Blend [_SrcBlend] [_DstBlend]
			ZWrite [_ZWrite]

			CGPROGRAM
			#pragma target 3.0
			// UnityInstancing.cginc (through UnityCG) decides whether procedural instancing is on:
			// the setup below must see that, and be declared before the vertex shader calls it.
			#include "UnityCG.cginc"
			#include "Assets/Scripts/Rendering/DetailInstancing.cginc"
			#pragma instancing_options procedural:DetailInstancingSetup
			#pragma multi_compile_local _ _NORMALMAP
			#pragma multi_compile_fwdbase
			#pragma multi_compile_fog
			#pragma multi_compile_instancing
			#pragma vertex vertBase
			#pragma fragment fragBase
			#include "UnityStandardCoreForward.cginc"
			ENDCG
		}

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On ZTest LEqual

			CGPROGRAM
			#pragma target 3.0
			// UnityInstancing.cginc (through UnityCG) decides whether procedural instancing is on:
			// the setup below must see that, and be declared before the vertex shader calls it.
			#include "UnityCG.cginc"
			#include "Assets/Scripts/Rendering/DetailInstancing.cginc"
			#pragma instancing_options procedural:DetailInstancingSetup
			#pragma multi_compile_shadowcaster
			#pragma multi_compile_instancing
			#pragma vertex vertShadowCaster
			#pragma fragment fragShadowCaster
			#include "UnityStandardShadow.cginc"
			ENDCG
		}
	}
}
