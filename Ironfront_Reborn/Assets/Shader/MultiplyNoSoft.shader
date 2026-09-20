// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

// RECOVERED SHADER - Ravenfield Beta 5
// Properties, tags, render state and blend mode below are EXACT: they were read back from the
// shipped ShaderLab block (Shader.m_Script) inside resources.assets.
// The CG body is RE-AUTHORED: the game ships only compiled d3d9/d3d11 bytecode for it.
// Behaviour: Unity's "Particles/Multiply", forced on top (Queue=Overlay, ZTest Always) with the
// soft-particle depth fade removed - hence "No Soft". _InvFade is kept for material compatibility.
Shader "Custom/Multiply No Soft" {
	Properties {
		_MainTex ("Particle Texture", 2D) = "white" {}
		_InvFade ("Soft Particles Factor", Range(0.01, 3.0)) = 1.0
	}

	Category {
		Tags { "Queue"="Overlay" "IgnoreProjector"="True" "RenderType"="Transparent" }
		Blend Zero SrcColor
		Cull Off
		Lighting Off
		ZWrite Off
		ZTest Always

		SubShader {
			Pass {
				CGPROGRAM
				#pragma vertex vert
				#pragma fragment frag
				#include "UnityCG.cginc"

				sampler2D _MainTex;
				float4 _MainTex_ST;

				struct appdata_t {
					float4 vertex   : POSITION;
					fixed4 color    : COLOR;
					float2 texcoord : TEXCOORD0;
				};

				struct v2f {
					float4 vertex   : SV_POSITION;
					fixed4 color    : COLOR;
					float2 texcoord : TEXCOORD0;
				};

				v2f vert (appdata_t v)
				{
					v2f o;
					o.vertex = UnityObjectToClipPos(v.vertex);
					o.color = v.color;
					o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
					return o;
				}

				fixed4 frag (v2f i) : SV_Target
				{
					half4 prev = i.color * tex2D(_MainTex, i.texcoord);
					// multiply blend: fade toward white by alpha so alpha=0 is a no-op
					return lerp(half4(1,1,1,1), prev, prev.a);
				}
				ENDCG
			}
		}
	}
}
