// Generated from Assets/ForestLake/Shaders/M_Foliage.shader by Ironfront > Generate instanced tree shaders.
// Do not edit: change the original and generate again (TreeShaderVariants.Transform).
Shader "Hidden/Ironfront/InstancedTrees/M_Foliage"
{
	Properties
	{
		_Cutoff( "Mask Clip Value", Float ) = 0.5
		_Albedo("Albedo", 2D) = "white" {}
		_NormalMap("Normal Map", 2D) = "bump" {}
		_Brightness("Brightness", Range( 0 , 10)) = 1.5
		_Grass_Color("Grass_Color", Color) = (1,1,1,0)
		_Desaturation("Desaturation", Range( 0 , 1)) = 0
		_Roughness("Roughness", Range( 0 , 3)) = 0
		_T_Black_A("T_Black_A", 2D) = "white" {}
		[Toggle(_USEROUGHNESS_ON)] _UseRoughness("UseRoughness", Float) = 0
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Transparent"  "Queue" = "Geometry+0" }
		Cull Off
		CGPROGRAM
		#pragma target 3.0
		#include "Assets/Scripts/Rendering/TreeInstancing.cginc"
		#pragma instancing_options procedural:TreeInstancingSetup
		#pragma multi_compile_local _ _USEROUGHNESS_ON
		#pragma surface surf Standard keepalpha addshadow fullforwardshadows dithercrossfade 
		struct Input
		{
			float2 uv_texcoord;
		};

		uniform sampler2D _NormalMap;
		uniform float4 _NormalMap_ST;
		uniform sampler2D _Albedo;
		uniform float4 _Albedo_ST;
		uniform float4 _Grass_Color;
		uniform float _Brightness;
		uniform float _Desaturation;
		uniform sampler2D _T_Black_A;
		uniform float4 _T_Black_A_ST;
		uniform float _Roughness;
		uniform float _Cutoff = 0.5;

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float2 uv_NormalMap = i.uv_texcoord * _NormalMap_ST.xy + _NormalMap_ST.zw;
			o.Normal = UnpackNormal( tex2D( _NormalMap, uv_NormalMap ) );
			float2 uv_Albedo = i.uv_texcoord * _Albedo_ST.xy + _Albedo_ST.zw;
			float4 tex2DNode2 = tex2D( _Albedo, uv_Albedo );
			float3 desaturateInitialColor8 = ( ( tex2DNode2 * _Grass_Color ) * _Brightness ).rgb;
			float desaturateDot8 = dot( desaturateInitialColor8, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar8 = lerp( desaturateInitialColor8, desaturateDot8.xxx, _Desaturation );
			o.Albedo = desaturateVar8;
			float4 temp_cast_1 = (0.0).xxxx;
			float2 uv_T_Black_A = i.uv_texcoord * _T_Black_A_ST.xy + _T_Black_A_ST.zw;
			#ifdef _USEROUGHNESS_ON
				float4 staticSwitch30 = ( tex2D( _T_Black_A, uv_T_Black_A ) * _Roughness );
			#else
				float4 staticSwitch30 = temp_cast_1;
			#endif
			o.Smoothness = staticSwitch30.r;
			o.Alpha = 1;
			clip( tex2DNode2.a - _Cutoff );
		}

		ENDCG
	}
	Fallback "Diffuse"
}
//CHKSM=009667629708590D508211B5E80CE98A853122F0