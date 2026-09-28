// Made with Amplify Shader Editor v1.9.1.5
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "WaterShader"
{
	Properties
	{
		_WaterNormal("Water Normal", 2D) = "bump" {}
		_Distance("Distance", Float) = 0
		_Normal2("Normal2", 2D) = "bump" {}
		_NormalScale("Normal Scale", Float) = 0
		_DeepColor("DeepColor", Color) = (0,0,0,0)
		_SurfaceColor("SurfaceColor", Color) = (0,0,0,0)
		_Waves_UV1("Waves_UV1", Range( 0 , 100)) = 0.5
		_Waves_UV2("Waves_UV2", Range( 0 , 100)) = 0.5
		_Smoothness("Smoothness", Range( 0 , 1)) = 0
		_Metallic("Metallic", Range( 0 , 1)) = 0
		_Opacity("Opacity", Float) = 1
		_ShallowTransitionStart("Shallow Transition Start", Float) = 0
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Transparent"  "Queue" = "Transparent+0" "IgnoreProjector" = "True" }
		Cull Back
		CGINCLUDE
		#include "UnityStandardUtils.cginc"
		#include "UnityShaderVariables.cginc"
		#include "UnityCG.cginc"
		#include "UnityPBSLighting.cginc"
		#include "Lighting.cginc"
		#pragma target 5.0
		struct Input
		{
			float2 uv_texcoord;
			float4 screenPosition8_g20;
		};

		uniform sampler2D _Normal2;
		uniform float _Waves_UV1;
		uniform float _NormalScale;
		uniform sampler2D _WaterNormal;
		uniform float _Waves_UV2;
		uniform float4 _DeepColor;
		uniform float4 _SurfaceColor;
		UNITY_DECLARE_DEPTH_TEXTURE( _CameraDepthTexture );
		uniform float4 _CameraDepthTexture_TexelSize;
		uniform float _Distance;
		uniform float _Metallic;
		uniform float _Smoothness;
		uniform float _ShallowTransitionStart;
		uniform float _Opacity;

		void vertexDataFunc( inout appdata_full v, out Input o )
		{
			UNITY_INITIALIZE_OUTPUT( Input, o );
			float4 ase_vertex4Pos = v.vertex;
			float3 vertexPos8_g20 = ase_vertex4Pos.xyz;
			float4 ase_screenPos8_g20 = ComputeScreenPos( UnityObjectToClipPos( vertexPos8_g20 ) );
			o.screenPosition8_g20 = ase_screenPos8_g20;
		}

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float2 panner169 = ( 1.0 * _Time.y * float2( -0.03,0 ) + ( i.uv_texcoord * _Waves_UV1 ));
			float2 panner170 = ( 1.0 * _Time.y * float2( 0.04,0.04 ) + ( i.uv_texcoord * _Waves_UV2 ));
			float3 lerpResult174 = lerp( BlendNormals( UnpackScaleNormal( tex2D( _Normal2, panner169 ), _NormalScale ) , UnpackScaleNormal( tex2D( _WaterNormal, panner170 ), _NormalScale ) ) , float3(0,0,1) , float3( 0,0,0 ));
			o.Normal = lerpResult174;
			float4 ase_screenPos8_g20 = i.screenPosition8_g20;
			float4 ase_screenPosNorm8 = ase_screenPos8_g20 / ase_screenPos8_g20.w;
			ase_screenPosNorm8.z = ( UNITY_NEAR_CLIP_VALUE >= 0 ) ? ase_screenPosNorm8.z : ase_screenPosNorm8.z * 0.5 + 0.5;
			float screenDepth8_g20 = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE( _CameraDepthTexture, ase_screenPosNorm8.xy ));
			float distanceDepth8_g20 = abs( ( screenDepth8_g20 - LinearEyeDepth( ase_screenPosNorm8.z ) ) / ( _Distance ) );
			float temp_output_163_0 = ( 1.0 - saturate( distanceDepth8_g20 ) );
			float4 lerpResult29 = lerp( _DeepColor , _SurfaceColor , temp_output_163_0);
			o.Albedo = lerpResult29.rgb;
			o.Metallic = _Metallic;
			o.Smoothness = _Smoothness;
			float clampResult160 = clamp( ( lerpResult29.a * (1.0 + (temp_output_163_0 - _ShallowTransitionStart) * (0.0 - 1.0) / (1.0 - _ShallowTransitionStart)) * _Opacity ) , 0.0 , 1.0 );
			o.Alpha = clampResult160;
		}

		ENDCG
		CGPROGRAM
		#pragma surface surf Standard alpha:fade keepalpha fullforwardshadows vertex:vertexDataFunc 

		ENDCG
		Pass
		{
			Name "ShadowCaster"
			Tags{ "LightMode" = "ShadowCaster" }
			ZWrite On
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
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
			sampler3D _DitherMaskLOD;
			struct v2f
			{
				V2F_SHADOW_CASTER;
				float2 customPack1 : TEXCOORD1;
				float4 customPack2 : TEXCOORD2;
				float3 worldPos : TEXCOORD3;
				float4 tSpace0 : TEXCOORD4;
				float4 tSpace1 : TEXCOORD5;
				float4 tSpace2 : TEXCOORD6;
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
				vertexDataFunc( v, customInputData );
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
				o.customPack2.xyzw = customInputData.screenPosition8_g20;
				o.worldPos = worldPos;
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
				surfIN.screenPosition8_g20 = IN.customPack2.xyzw;
				float3 worldPos = IN.worldPos;
				half3 worldViewDir = normalize( UnityWorldSpaceViewDir( worldPos ) );
				SurfaceOutputStandard o;
				UNITY_INITIALIZE_OUTPUT( SurfaceOutputStandard, o )
				surf( surfIN, o );
				#if defined( CAN_SKIP_VPOS )
				float2 vpos = IN.pos;
				#endif
				half alphaRef = tex3D( _DitherMaskLOD, float3( vpos.xy * 0.25, o.Alpha * 0.9375 ) ).a;
				clip( alphaRef - 0.01 );
				SHADOW_CASTER_FRAGMENT( IN )
			}
			ENDCG
		}
	}
	Fallback "Diffuse"
}
/*ASEBEGIN
Version=19105
Node;AmplifyShaderEditor.ColorNode;27;-129.6184,-356.4741;Inherit;False;Property;_DeepColor;DeepColor;4;0;Create;True;0;0;0;False;0;False;0,0,0,0;0.9386792,1,0.9466282,0.3843137;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.LerpOp;29;190.7074,-40.68962;Inherit;False;3;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;2;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.BreakToComponentsNode;71;288.9081,134.8487;Inherit;False;COLOR;1;0;COLOR;0,0,0,0;False;16;FLOAT;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4;FLOAT;5;FLOAT;6;FLOAT;7;FLOAT;8;FLOAT;9;FLOAT;10;FLOAT;11;FLOAT;12;FLOAT;13;FLOAT;14;FLOAT;15
Node;AmplifyShaderEditor.ColorNode;28;-115.9067,115.8263;Inherit;False;Property;_SurfaceColor;SurfaceColor;5;0;Create;True;0;0;0;False;0;False;0,0,0,0;0.5471698,0.5471698,0.5471698,0.5803922;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;95;1259.528,1360.89;Inherit;False;Property;_Smoothness;Smoothness;8;0;Create;True;0;0;0;False;0;False;0;0.92;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;96;1148.389,1260.356;Inherit;False;Property;_Metallic;Metallic;9;0;Create;True;0;0;0;False;0;False;0;1;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;159;1211.218,1435.783;Inherit;False;3;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.ClampOpNode;160;1393.865,1610.365;Inherit;False;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;156;929.8347,1552.43;Inherit;False;Property;_Opacity;Opacity;10;0;Create;True;0;0;0;False;0;False;1;1;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.TFHCRemapNode;158;-47.94653,459.3961;Inherit;False;5;0;FLOAT;0;False;1;FLOAT;0.8;False;2;FLOAT;1;False;3;FLOAT;1;False;4;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.FunctionNode;163;-2666.764,172.3157;Inherit;True;DepthSamplingFunction;1;;20;85e819aa3aaa1204e87f098bf20b3f20;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;164;-492.9974,479.8288;Inherit;False;Property;_ShallowTransitionStart;Shallow Transition Start;11;0;Create;True;0;0;0;False;0;False;0;0.57;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;165;-5718.212,154.9839;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;166;-5515.496,317.1039;Inherit;False;2;2;0;FLOAT2;0,0;False;1;FLOAT;0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;167;-5316.496,580.1039;Inherit;False;2;2;0;FLOAT2;0,0;False;1;FLOAT;0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.RangedFloatNode;168;-4981.737,471.7148;Float;False;Property;_NormalScale;Normal Scale;3;0;Create;True;0;0;0;False;0;False;0;0.75;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.PannerNode;169;-4976.637,231.6168;Inherit;False;3;0;FLOAT2;0,0;False;2;FLOAT2;-0.03,0;False;1;FLOAT;1;False;1;FLOAT2;0
Node;AmplifyShaderEditor.PannerNode;170;-4974.337,344.1158;Inherit;False;3;0;FLOAT2;0,0;False;2;FLOAT2;0.04,0.04;False;1;FLOAT;1;False;1;FLOAT2;0
Node;AmplifyShaderEditor.SamplerNode;171;-4632.638,239.9157;Inherit;True;Property;_Normal2;Normal2;3;0;Create;True;0;0;0;False;0;False;-1;abc00000000009576515129225354041;abc00000000009576515129225354041;True;0;True;bump;Auto;True;Instance;-1;Auto;Texture2D;8;0;SAMPLER2D;0,0;False;1;FLOAT2;0,0;False;2;FLOAT;1;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;172;-4619.737,449.8158;Inherit;True;Property;_WaterNormal;Water Normal;0;0;Create;True;0;0;0;False;0;False;-1;abc00000000004831255687537903438;abc00000000009576515129225354041;True;0;True;bump;Auto;True;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;0,0;False;1;FLOAT2;0,0;False;2;FLOAT;1;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.BlendNormalsNode;173;-4192.734,384.4157;Inherit;False;0;3;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.LerpOp;174;-3620.169,420.1538;Inherit;False;3;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.RangedFloatNode;175;-5725.496,479.1039;Inherit;False;Property;_Waves_UV1;Waves_UV1;6;0;Create;True;0;0;0;False;0;False;0.5;30;0;100;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;176;-5650.496,650.1039;Inherit;False;Property;_Waves_UV2;Waves_UV2;7;0;Create;True;0;0;0;False;0;False;0.5;30;0;100;0;1;FLOAT;0
Node;AmplifyShaderEditor.Vector3Node;177;-4079.966,511.0888;Inherit;False;Constant;_Vector1;Vector 0;1;0;Create;True;0;0;0;False;0;False;0,0,1;0,0,0;0;4;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3
Node;AmplifyShaderEditor.StandardSurfaceOutputNode;0;2322.663,1550.401;Float;False;True;-1;7;;0;0;Standard;WaterShader;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;False;False;False;False;False;False;Back;0;False;;0;False;;False;0;False;;0;False;;False;0;Transparent;0.5;True;True;0;False;Transparent;;Transparent;All;12;all;True;True;True;True;0;False;;False;0;False;;255;False;;255;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;False;2;15;10;25;False;0.5;True;2;5;False;;10;False;;0;0;False;;0;False;;0;False;;0;False;;0;False;0;0,0,0,0;VertexOffset;True;False;Cylindrical;False;True;Relative;0;;-1;-1;-1;-1;0;False;0;0;False;;-1;0;False;;0;0;0;False;0.1;False;;0;False;;False;16;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT;0;False;9;FLOAT;0;False;10;FLOAT;0;False;13;FLOAT3;0,0,0;False;11;FLOAT3;0,0,0;False;12;FLOAT3;0,0,0;False;14;FLOAT4;0,0,0,0;False;15;FLOAT3;0,0,0;False;0
WireConnection;29;0;27;0
WireConnection;29;1;28;0
WireConnection;29;2;163;0
WireConnection;71;0;29;0
WireConnection;159;0;71;3
WireConnection;159;1;158;0
WireConnection;159;2;156;0
WireConnection;160;0;159;0
WireConnection;158;0;163;0
WireConnection;158;1;164;0
WireConnection;166;0;165;0
WireConnection;166;1;175;0
WireConnection;167;0;165;0
WireConnection;167;1;176;0
WireConnection;169;0;166;0
WireConnection;170;0;167;0
WireConnection;171;1;169;0
WireConnection;171;5;168;0
WireConnection;172;1;170;0
WireConnection;172;5;168;0
WireConnection;173;0;171;0
WireConnection;173;1;172;0
WireConnection;174;0;173;0
WireConnection;174;1;177;0
WireConnection;0;0;29;0
WireConnection;0;1;174;0
WireConnection;0;3;96;0
WireConnection;0;4;95;0
WireConnection;0;9;160;0
ASEEND*/
//CHKSM=465BB8A6EE7CA675960B0618B9A56F28D7257FE9