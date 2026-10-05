// Generated from Assets/ForestLake/Shaders/M_Foliage_Wind.shader by Ironfront > Generate instanced detail shaders.
// Do not edit: change the original and generate again (ProceduralShaderCopy.Transform).
Shader "Hidden/Ironfront/InstancedDetails/M_Foliage_Wind"
{
	Properties
	{
		_Cutoff( "Mask Clip Value", Float ) = 0.4
		_Noise_Amount("Noise_Amount", Range( 0 , 2)) = -0.15
		_Wind_Amount("Wind_Amount", Range( 0 , 0.5)) = 0.1
		_Wind_Speed("Wind_Speed", Range( 0 , 2)) = 1
		_NoiseTexture("NoiseTexture", 2D) = "white" {}
		_BaseMap("Base Map", 2D) = "white" {}
		_Roughness("Roughness", 2D) = "white" {}
		_NormalMap("Normal Map", 2D) = "bump" {}
		_Brightness("Brightness", Range( 0 , 3)) = 1
		_Rough("Rough", Range( 0 , 2)) = 1
		_Subsurface("Subsurface", Range( 0 , 3)) = 1
		[Toggle(_BEND_ONLY_ON)] _Bend_Only("Bend_Only", Float) = 0
		_Desaturate("Desaturate", Range( 0 , 1)) = 0
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Transparent"  "Queue" = "Geometry+0" "IsEmissive" = "true"  }
		Cull Off
		CGPROGRAM
		#include "UnityShaderVariables.cginc"
		#pragma target 3.0
		#include "Assets/Scripts/Rendering/DetailInstancing.cginc"
		#pragma instancing_options procedural:DetailInstancingSetup
		#pragma multi_compile_local _ _BEND_ONLY_ON
		#define ASE_VERSION 19701
		#pragma surface surf Standard keepalpha addshadow fullforwardshadows vertex:vertexDataFunc noforwardadd
		struct Input
		{
			float3 worldPos;
			float2 uv_texcoord;
		};

		uniform float _Wind_Speed;
		uniform float _Wind_Amount;
		uniform sampler2D _NoiseTexture;
		uniform float _Noise_Amount;
		uniform sampler2D _NormalMap;
		uniform float4 _NormalMap_ST;
		uniform sampler2D _BaseMap;
		uniform float4 _BaseMap_ST;
		uniform float _Brightness;
		uniform float _Desaturate;
		uniform float _Subsurface;
		uniform float _Rough;
		uniform sampler2D _Roughness;
		uniform float4 _Roughness_ST;
		uniform float _Cutoff = 0.4;

		void vertexDataFunc( inout appdata_full v, out Input o )
		{
			UNITY_INITIALIZE_OUTPUT( Input, o );
			float3 temp_output_15_0 = float3( 0,0,0 );
			float4 transform80 = mul(unity_ObjectToWorld,float4( 0,0,0,1 ));
			float3 ase_worldPos = mul( unity_ObjectToWorld, v.vertex );
			float temp_output_12_0 = ( ( _Wind_Speed * _CosTime.w ) * ( distance( transform80.y , ase_worldPos.y ) * _Wind_Amount ) );
			float4 appendResult13 = (float4(temp_output_12_0 , 0.0 , temp_output_12_0 , 0.0));
			float4 temp_output_14_0 = ( float4( temp_output_15_0 , 0.0 ) + appendResult13 );
			float2 temp_cast_2 = (_Noise_Amount).xx;
			float4 appendResult33 = (float4(ase_worldPos.x , ase_worldPos.y , 0.0 , 0.0));
			float2 panner36 = ( 0.1 * _Time.y * temp_cast_2 + ( appendResult33 * -0.15 ).xy);
			float4 _Direction = float4(0,0,0,0);
			float3 appendResult37 = (float3(_Direction.x , _Direction.y , _Direction.z));
			float3 worldToObjDir42 = mul( unity_WorldToObject, float4( appendResult37, 0 ) ).xyz;
			float4 lerpResult54 = lerp( float4( temp_output_15_0 , 0.0 ) , temp_output_14_0 , ( ( ( (tex2Dlod( _NoiseTexture, float4( panner36, 0, 0.0) )*2.0 + -1.0) * 0.39 ) + float4( ( worldToObjDir42 * 0.0 ) , 0.0 ) ) * float4( float3(1,1,0.2) , 0.0 ) ));
			#ifdef _BEND_ONLY_ON
				float4 staticSwitch74 = temp_output_14_0;
			#else
				float4 staticSwitch74 = lerpResult54;
			#endif
			v.vertex.xyz += staticSwitch74.xyz;
			v.vertex.w = 1;
		}

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float2 uv_NormalMap = i.uv_texcoord * _NormalMap_ST.xy + _NormalMap_ST.zw;
			o.Normal = UnpackNormal( tex2D( _NormalMap, uv_NormalMap ) );
			float2 uv_BaseMap = i.uv_texcoord * _BaseMap_ST.xy + _BaseMap_ST.zw;
			float4 tex2DNode16 = tex2D( _BaseMap, uv_BaseMap );
			float3 desaturateInitialColor81 = ( tex2DNode16 * _Brightness ).rgb;
			float desaturateDot81 = dot( desaturateInitialColor81, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar81 = lerp( desaturateInitialColor81, desaturateDot81.xxx, _Desaturate );
			o.Albedo = desaturateVar81;
			o.Emission = ( tex2DNode16 * _Subsurface ).rgb;
			float2 uv_Roughness = i.uv_texcoord * _Roughness_ST.xy + _Roughness_ST.zw;
			o.Smoothness = ( _Rough * tex2D( _Roughness, uv_Roughness ) ).r;
			o.Alpha = 1;
			clip( tex2DNode16.a - _Cutoff );
		}

		ENDCG
	}
	Fallback "Diffuse"
}
//CHKSM=F0209FA5B17BF9EC6E39835E8914CDDFDF081563