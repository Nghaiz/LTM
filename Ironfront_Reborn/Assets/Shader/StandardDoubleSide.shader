// RECOVERED SHADER - Ravenfield Beta 5
// Properties, tags, pass list and render state below are EXACT (read back from the shipped
// ShaderLab block in resources.assets). The shipped shader has 5 generated passes -
// ForwardBase / ForwardAdd / PrePassBase / PrePassFinal / Deferred - which is exactly the set a
// Unity 5 surface shader with the Standard lighting model emits, so it is reconstructed as one.
// The CG body is RE-AUTHORED: only compiled d3d9/d3d11 bytecode shipped.
Shader "Custom/StandardDoubleSide" {
	Properties {
		_MainTex ("Texture", 2D) = "white" {}
		_ColorTint ("Tint", Color) = (1, 0.6, 0.6, 1)
	}

	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 200
		Cull Off          // the whole point of this shader: render both faces

		CGPROGRAM
		#pragma surface surf Standard
		#pragma target 3.0

		sampler2D _MainTex;
		fixed4 _ColorTint;

		struct Input {
			float2 uv_MainTex;
		};

		void surf (Input IN, inout SurfaceOutputStandard o) {
			fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _ColorTint;
			o.Albedo = c.rgb;
			o.Alpha  = c.a;
		}
		ENDCG
	}

	Fallback "Diffuse"
}
