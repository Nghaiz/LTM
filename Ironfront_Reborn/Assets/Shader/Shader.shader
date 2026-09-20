// RECOVERED ARTIFACT -- DO NOT RENAME BACK TO "Standard".
//
// This is AssetRipper's //DummyShaderTextExporter output for the original build's Standard
// shader: the 27-property block is faithful, the body is a stub that samples _MainTex into
// Albedo and nothing else. It is kept as ground truth from the recovered build (P22).
//
// It used to declare Shader "Standard", which SHADOWED Unity's built-in: Shader.Find("Standard")
// returned this stub, so any material assigned by name silently lost its normal, metallic,
// occlusion and emission maps. Forty materials were sitting on it before P26. Renaming the
// declaration removes the trap without deleting the artifact.
Shader "Recovered/StandardStub" {
Properties {
 _Color ("Color", Color) = (1.000000,1.000000,1.000000,1.000000)
 _MainTex ("Albedo", 2D) = "white" { }
 _Cutoff ("Alpha Cutoff", Range(0.000000,1.000000)) = 0.500000
 _Glossiness ("Smoothness", Range(0.000000,1.000000)) = 0.500000
 _GlossMapScale ("Smoothness Scale", Range(0.000000,1.000000)) = 1.000000
[Enum(Metallic Alpha,0,Albedo Alpha,1)]  _SmoothnessTextureChannel ("Smoothness texture channel", Float) = 0.000000
[Gamma]  _Metallic ("Metallic", Range(0.000000,1.000000)) = 0.000000
 _MetallicGlossMap ("Metallic", 2D) = "white" { }
[ToggleOff]  _SpecularHighlights ("Specular Highlights", Float) = 1.000000
[ToggleOff]  _GlossyReflections ("Glossy Reflections", Float) = 1.000000
 _BumpScale ("Scale", Float) = 1.000000
 _BumpMap ("Normal Map", 2D) = "bump" { }
 _Parallax ("Height Scale", Range(0.005000,0.080000)) = 0.020000
 _ParallaxMap ("Height Map", 2D) = "black" { }
 _OcclusionStrength ("Strength", Range(0.000000,1.000000)) = 1.000000
 _OcclusionMap ("Occlusion", 2D) = "white" { }
 _EmissionColor ("Color", Color) = (0.000000,0.000000,0.000000,1.000000)
 _EmissionMap ("Emission", 2D) = "white" { }
 _DetailMask ("Detail Mask", 2D) = "white" { }
 _DetailAlbedoMap ("Detail Albedo x2", 2D) = "grey" { }
 _DetailNormalMapScale ("Scale", Float) = 1.000000
 _DetailNormalMap ("Normal Map", 2D) = "bump" { }
[Enum(UV0,0,UV1,1)]  _UVSec ("UV Set for secondary textures", Float) = 0.000000
[HideInInspector]  _Mode ("__mode", Float) = 0.000000
[HideInInspector]  _SrcBlend ("__src", Float) = 1.000000
[HideInInspector]  _DstBlend ("__dst", Float) = 0.000000
[HideInInspector]  _ZWrite ("__zw", Float) = 1.000000
}
	//DummyShaderTextExporter
	
	SubShader{
		Tags { "RenderType" = "Opaque" }
		LOD 200
		CGPROGRAM
#pragma surface surf Standard fullforwardshadows
#pragma target 3.0
		sampler2D _MainTex;
		struct Input
		{
			float2 uv_MainTex;
		};
		void surf(Input IN, inout SurfaceOutputStandard o)
		{
			fixed4 c = tex2D(_MainTex, IN.uv_MainTex);
			o.Albedo = c.rgb;
		}
		ENDCG
	}
}