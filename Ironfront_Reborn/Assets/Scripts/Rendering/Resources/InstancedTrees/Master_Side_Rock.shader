// Generated from Assets/ForestLake/Shaders/Master_Side_Rock.shader by Ironfront > Generate instanced tree shaders.
// Do not edit: change the original and generate again (TreeShaderVariants.Transform).
Shader "Hidden/Ironfront/InstancedTrees/Master_Side_Rock"
{
	Properties
	{
		_CliffRough("CliffRough", Range( 0 , 3)) = 0
		_GrassRough("GrassRough", Range( 0 , 3)) = 0
		_GrassBrightness("GrassBrightness", Range( 0 , 3)) = 1
		_Color_Edge("Color_Edge", Range( 0 , 4)) = 1
		_Color_Cavities("Color_Cavities", Range( 0 , 3)) = 1
		_Desaturation("Desaturation", Range( 0 , 1)) = 0
		_Brightness("Brightness", Range( 0 , 3)) = 1
		_GrassScale("GrassScale", Range( 0 , 20)) = 1
		_AOPower("AOPower", Range( 0 , 1)) = 1
		_DetailNormalScale("DetailNormalScale", Range( 0 , 20)) = 2
		[NoScaleOffset]_GrassTexture("GrassTexture", 2D) = "white" {}
		_GrassColor("GrassColor", Color) = (1,1,1,0)
		[NoScaleOffset]_MaskMap("MaskMap", 2D) = "white" {}
		[NoScaleOffset]_CliffTexture("CliffTexture", 2D) = "white" {}
		_Grass_Normal_Power("Grass_Normal_Power", Range( 0 , 3)) = 1
		_Cliff_Normal_Power("Cliff_Normal_Power", Range( 0 , 2)) = 1
		[NoScaleOffset]_Cliff_Normal("Cliff_Normal", 2D) = "bump" {}
		[NoScaleOffset]_GrassNormal("GrassNormal", 2D) = "bump" {}
		_maskConstant("maskConstant", Range( 0 , 4)) = 1
		_NormalCapGrass("NormalCapGrass", Range( 0 , 3)) = 1
		_maskBrightness("maskBrightness", Range( 0 , 3)) = 1
		[NoScaleOffset]_MainNormal("MainNormal", 2D) = "bump" {}
		[Toggle(_USEROUGHNESSTEXTURE_ON)] _UseRoughnessTexture("UseRoughnessTexture", Float) = 0
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Opaque"  "Queue" = "Geometry+0" }
		Cull Back
		CGINCLUDE
		#include "UnityStandardUtils.cginc"
		#include "UnityPBSLighting.cginc"
		#include "Lighting.cginc"
		#pragma target 3.0
		#include "Assets/Scripts/Rendering/TreeInstancing.cginc"
		#pragma instancing_options procedural:TreeInstancingSetup
		#pragma multi_compile_instancing
		#pragma multi_compile_local _ _USEROUGHNESSTEXTURE_ON
		#ifdef UNITY_PASS_SHADOWCASTER
			#undef INTERNAL_DATA
			#undef WorldReflectionVector
			#undef WorldNormalVector
			#define INTERNAL_DATA half3 internalSurfaceTtoW0; half3 internalSurfaceTtoW1; half3 internalSurfaceTtoW2;
			#define WorldReflectionVector(data,normal) reflect (data.worldRefl, half3(dot(data.internalSurfaceTtoW0,normal), dot(data.internalSurfaceTtoW1,normal), dot(data.internalSurfaceTtoW2,normal)))
			#define WorldNormalVector(data,normal) half3(dot(data.internalSurfaceTtoW0,normal), dot(data.internalSurfaceTtoW1,normal), dot(data.internalSurfaceTtoW2,normal))
		#endif
		struct Input
		{
			float2 uv_texcoord;
			float3 worldNormal;
			INTERNAL_DATA
		};

		uniform sampler2D _MainNormal;
		uniform sampler2D _Cliff_Normal;
		uniform float _DetailNormalScale;
		uniform float _Cliff_Normal_Power;
		uniform sampler2D _GrassNormal;
		uniform float _GrassScale;
		uniform float _Grass_Normal_Power;
		uniform float _maskConstant;
		uniform float _maskBrightness;
		uniform float _NormalCapGrass;
		uniform float _Color_Cavities;
		uniform float _Color_Edge;
		uniform sampler2D _CliffTexture;
		uniform sampler2D _MaskMap;
		uniform float4 _GrassColor;
		uniform sampler2D _GrassTexture;
		uniform float _GrassBrightness;
		uniform float _Desaturation;
		uniform float _Brightness;
		uniform float _CliffRough;
		uniform float _GrassRough;
		uniform float _AOPower;

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float2 uv_MainNormal72 = i.uv_texcoord;
			float3 tex2DNode72 = UnpackNormal( tex2D( _MainNormal, uv_MainNormal72 ) );
			float2 temp_output_43_0 = ( i.uv_texcoord * _DetailNormalScale );
			float2 temp_output_17_0 = ( i.uv_texcoord * _GrassScale );
			float3 ase_worldNormal = WorldNormalVector( i, float3( 0, 0, 1 ) );
			float3 ase_worldTangent = WorldNormalVector( i, float3( 1, 0, 0 ) );
			float3 ase_worldBitangent = WorldNormalVector( i, float3( 0, 1, 0 ) );
			float3x3 ase_tangentToWorldFast = float3x3(ase_worldTangent.x,ase_worldBitangent.x,ase_worldNormal.x,ase_worldTangent.y,ase_worldBitangent.y,ase_worldNormal.y,ase_worldTangent.z,ase_worldBitangent.z,ase_worldNormal.z);
			float3 tangentToWorldDir83 = mul( ase_tangentToWorldFast, tex2DNode72 );
			float dotResult63 = dot( float3(0,1,0) , tangentToWorldDir83 );
			float saferPower65 = abs( dotResult63 );
			float clampResult69 = clamp( ( pow( saferPower65 , _maskConstant ) * _maskBrightness ) , 0.0 , _NormalCapGrass );
			float3 lerpResult59 = lerp( ( UnpackNormal( tex2D( _Cliff_Normal, temp_output_43_0 ) ) * ( float3(0,0,1) + ( float3(1,1,0) * _Cliff_Normal_Power ) ) ) , ( UnpackNormal( tex2D( _GrassNormal, temp_output_17_0 ) ) * ( float3(0,0,1) + ( float3(1,1,0) * _Grass_Normal_Power ) ) ) , clampResult69);
			o.Normal = BlendNormals( tex2DNode72 , lerpResult59 );
			float4 tex2DNode45 = tex2D( _CliffTexture, temp_output_43_0 );
			float2 uv_MaskMap35 = i.uv_texcoord;
			float4 tex2DNode35 = tex2D( _MaskMap, uv_MaskMap35 );
			float4 lerpResult29 = lerp( ( _Color_Edge * tex2DNode45 ) , tex2DNode45 , tex2DNode35.g);
			float4 lerpResult27 = lerp( ( _Color_Cavities * lerpResult29 ) , lerpResult29 , tex2DNode35.r);
			float4 tex2DNode19 = tex2D( _GrassTexture, temp_output_17_0 );
			float3 tangentToWorldDir82 = mul( ase_tangentToWorldFast, tex2DNode72 );
			float dotResult3 = dot( float3(0,1,0) , tangentToWorldDir82 );
			float saferPower8 = abs( dotResult3 );
			float clampResult13 = clamp( ( pow( saferPower8 , _maskConstant ) * _maskBrightness ) , 0.0 , 1.0 );
			float4 lerpResult26 = lerp( lerpResult27 , ( ( _GrassColor * tex2DNode19 ) * _GrassBrightness ) , clampResult13);
			float3 desaturateInitialColor47 = lerpResult26.rgb;
			float desaturateDot47 = dot( desaturateInitialColor47, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar47 = lerp( desaturateInitialColor47, desaturateDot47.xxx, _Desaturation );
			o.Albedo = ( desaturateVar47 * _Brightness );
			float lerpResult84 = lerp( _CliffRough , _GrassRough , clampResult13);
			float lerpResult88 = lerp( ( _CliffRough * tex2DNode45.a ) , ( _GrassRough * tex2DNode19.a ) , clampResult13);
			#ifdef _USEROUGHNESSTEXTURE_ON
				float staticSwitch85 = lerpResult88;
			#else
				float staticSwitch85 = lerpResult84;
			#endif
			o.Smoothness = staticSwitch85;
			float lerpResult80 = lerp( 1.0 , tex2DNode35.b , _AOPower);
			o.Occlusion = lerpResult80;
			o.Alpha = 1;
		}

		ENDCG
		CGPROGRAM
		#pragma surface surf Standard keepalpha fullforwardshadows 

		ENDCG
		Pass
		{
			Name "ShadowCaster"
			Tags{ "LightMode" = "ShadowCaster" }
			ZWrite On
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.0
			#pragma multi_compile_shadowcaster
			#pragma multi_compile UNITY_PASS_SHADOWCASTER
			#pragma skip_variants FOG_LINEAR FOG_EXP FOG_EXP2
			#include "HLSLSupport.cginc"
			#if ( SHADER_API_D3D11 || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3 || SHADER_API_METAL || SHADER_API_VULKAN )
				#define CAN_SKIP_VPOS
			#endif
			#include "UnityCG.cginc"
			#include "Lighting.cginc"
			#include "UnityPBSLighting.cginc"
			struct v2f
			{
				V2F_SHADOW_CASTER;
				float2 customPack1 : TEXCOORD1;
				float4 tSpace0 : TEXCOORD2;
				float4 tSpace1 : TEXCOORD3;
				float4 tSpace2 : TEXCOORD4;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};
			v2f vert( appdata_full v )
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID( v );
				UNITY_INITIALIZE_OUTPUT( v2f, o );
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO( o );
				UNITY_TRANSFER_INSTANCE_ID( v, o );
				Input customInputData;
				float3 worldPos = mul( unity_ObjectToWorld, v.vertex ).xyz;
				half3 worldNormal = UnityObjectToWorldNormal( v.normal );
				half3 worldTangent = UnityObjectToWorldDir( v.tangent.xyz );
				half tangentSign = v.tangent.w * unity_WorldTransformParams.w;
				half3 worldBinormal = cross( worldNormal, worldTangent ) * tangentSign;
				o.tSpace0 = float4( worldTangent.x, worldBinormal.x, worldNormal.x, worldPos.x );
				o.tSpace1 = float4( worldTangent.y, worldBinormal.y, worldNormal.y, worldPos.y );
				o.tSpace2 = float4( worldTangent.z, worldBinormal.z, worldNormal.z, worldPos.z );
				o.customPack1.xy = customInputData.uv_texcoord;
				o.customPack1.xy = v.texcoord;
				TRANSFER_SHADOW_CASTER_NORMALOFFSET( o )
				return o;
			}
			half4 frag( v2f IN
			#if !defined( CAN_SKIP_VPOS )
			, UNITY_VPOS_TYPE vpos : VPOS
			#endif
			) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID( IN );
				Input surfIN;
				UNITY_INITIALIZE_OUTPUT( Input, surfIN );
				surfIN.uv_texcoord = IN.customPack1.xy;
				float3 worldPos = float3( IN.tSpace0.w, IN.tSpace1.w, IN.tSpace2.w );
				half3 worldViewDir = normalize( UnityWorldSpaceViewDir( worldPos ) );
				surfIN.worldNormal = float3( IN.tSpace0.z, IN.tSpace1.z, IN.tSpace2.z );
				surfIN.internalSurfaceTtoW0 = IN.tSpace0.xyz;
				surfIN.internalSurfaceTtoW1 = IN.tSpace1.xyz;
				surfIN.internalSurfaceTtoW2 = IN.tSpace2.xyz;
				SurfaceOutputStandard o;
				UNITY_INITIALIZE_OUTPUT( SurfaceOutputStandard, o )
				surf( surfIN, o );
				#if defined( CAN_SKIP_VPOS )
				float2 vpos = IN.pos;
				#endif
				SHADOW_CASTER_FRAGMENT( IN )
			}
			ENDCG
		}
	}
	Fallback "Diffuse"
}
//CHKSM=381B20B9B4012CE5740BAF0C808C08A2A4639D6F