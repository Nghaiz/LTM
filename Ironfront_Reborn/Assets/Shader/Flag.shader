// RECOVERED SHADER - Ravenfield Beta 5
// Properties, tags, the single FORWARDBASE pass, the Dependency line and Fallback below are EXACT
// (read back from the shipped ShaderLab block in sharedassets2.assets).
// That signature - RenderType=TreeLeaf, SHADOWSUPPORT, one forward pass only, and
// Dependency "OptimizedShader" = "Hidden/Nature/Tree Creator Leaves Fast Optimized" - identifies it
// as Unity 5.4's built-in "Nature/Tree Creator Leaves" with Cull Off added (so the flag is visible
// from both sides) and the _ShadowTex/_BumpMap/_TranslucencyMap inputs dropped.
// The CG body is RE-AUTHORED against Unity's own tree library; only compiled bytecode shipped.
Shader "Custom/Flag" {
	Properties {
		_Color ("Main Color", Color) = (1, 1, 1, 1)
		_TranslucencyColor ("Translucency Color", Color) = (0.73, 0.85, 0.41, 1)
		_Cutoff ("Alpha cutoff", Range(0, 1)) = 0.3
		_TranslucencyViewDependency ("View dependency", Range(0, 1)) = 0.7
		_ShadowStrength ("Shadow Strength", Range(0, 1)) = 1.0
		_MainTex ("Base (RGB) Alpha (A)", 2D) = "white" {}

		// set from script by Unity's tree system; hidden in the inspector
		[HideInInspector] _TreeInstanceColor ("TreeInstanceColor", Vector) = (1, 1, 1, 1)
		[HideInInspector] _TreeInstanceScale ("TreeInstanceScale", Vector) = (1, 1, 1, 1)
		[HideInInspector] _SquashAmount ("Squash", Float) = 1
	}

	SubShader {
		Tags { "IgnoreProjector"="True" "RenderType"="TreeLeaf" }
		LOD 200
		Cull Off          // double-sided: a flag must render from both faces

		CGPROGRAM
		// noforwardadd -> a single FORWARDBASE pass, matching the shipped shader
		#pragma surface surf TreeLeaf alphatest:_Cutoff vertex:TreeVertLeaf addshadow nolightmap noforwardadd
		#include "UnityBuiltin3xTreeLibrary.cginc"

		sampler2D _MainTex;

		struct Input {
			float2 uv_MainTex;
			fixed4 color : COLOR;
		};

		void surf (Input IN, inout LeafSurfaceOutput o) {
			fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
			o.Albedo = c.rgb * IN.color.a;
			o.Translucency = IN.color.rgb;
			o.Gloss = 0.0;
			o.Alpha = c.a;
		}
		ENDCG
	}

	Dependency "OptimizedShader" = "Hidden/Nature/Tree Creator Leaves Fast Optimized"
	Fallback "Diffuse"
}
