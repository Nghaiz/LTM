// Made with Amplify Shader Editor v1.9.1.5
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "M_Standart_Blend"
{
	Properties
	{
		[Header(Vertex Color_G)][Header(_____________________)]_VColor_G_Scale("VColor_G_Scale", Range( 0 , 10)) = 1.37
		_Global_G("Global_G", Range( 0 , 5)) = 1.1
		[Toggle(_LAYER_SWAP_ON)] _Layer_Swap("Layer_Swap", Float) = 0
		[Header(Global)][Header(_____________________)]_Brightness("Brightness", Range( 0 , 5)) = 1
		_Desaturation("Desaturation", Range( 0 , 1)) = 0
		[Toggle(_ADD_NORMAL_ON)] _Add_Normal("Add_Normal", Float) = 0
		[Toggle(_ADD_MASK_ROUGH_ON)] _Add_Mask_Rough("Add_Mask_Rough", Float) = 0
		[Toggle(_ADD_MASK_ON)] _Add_Mask("Add_Mask", Float) = 0
		_UV("UV", Range( 0 , 10)) = 1
		[Toggle(_NON_METALLIC_ON)] _Non_Metallic("Non_Metallic", Float) = 1
		_Mask("Mask", 2D) = "white" {}
		_MaskTexture("MaskTexture", 2D) = "white" {}
		[Header(Layer_A)][Header(_____________________)]_Color_A("Color_A", Color) = (1,1,1,0)
		[Toggle(_COLOR_CHANGE_A_ON)] _Color_Change_A("Color_Change_A", Float) = 0
		_Albedo_A("Albedo_A", 2D) = "white" {}
		_Brightness_A("Brightness_A", Range( 0 , 5)) = 1
		_Desaturation_A("Desaturation_A", Range( 0 , 1)) = 0
		_Roughness_A("Roughness_A", 2D) = "white" {}
		_Rough_A("Rough_A", Range( -5 , 5)) = 1
		_Normal_A("Normal_A", 2D) = "bump" {}
		_Metallic_A("Metallic_A", 2D) = "white" {}
		[Header(Layer_B)][Header(_____________________)]_Albedo_B("Albedo_B", 2D) = "white" {}
		[Toggle(_COLOR_CHANGE_B_ON)] _Color_Change_B("Color_Change_B", Float) = 0
		_Color_B("Color_B", Color) = (1,1,1,0)
		_Brightness_B("Brightness_B", Range( 0 , 5)) = 1
		_Desaturation_B("Desaturation_B", Range( 0 , 1)) = 0
		_Normal_B("Normal_B", 2D) = "bump" {}
		_NormalTexture("NormalTexture", 2D) = "bump" {}
		_Roughness_B("Roughness_B", 2D) = "white" {}
		_Rough_B("Rough_B", Range( -5 , 5)) = 1
		_Metallic_B("Metallic_B", 2D) = "white" {}
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Opaque"  "Queue" = "Geometry+0" }
		Cull Back
		CGPROGRAM
		#include "UnityStandardUtils.cginc"
		#pragma target 3.0
		#pragma shader_feature_local _ADD_NORMAL_ON
		#pragma shader_feature_local _LAYER_SWAP_ON
		#pragma shader_feature_local _ADD_MASK_ON
		#pragma shader_feature_local _COLOR_CHANGE_A_ON
		#pragma shader_feature_local _COLOR_CHANGE_B_ON
		#pragma shader_feature_local _NON_METALLIC_ON
		#pragma shader_feature_local _ADD_MASK_ROUGH_ON
		#pragma surface surf Standard keepalpha addshadow fullforwardshadows 
		struct Input
		{
			float2 uv_texcoord;
			float4 vertexColor : COLOR;
		};

		uniform sampler2D _Normal_A;
		uniform float _UV;
		uniform sampler2D _Normal_B;
		uniform sampler2D _Mask;
		uniform float _VColor_G_Scale;
		uniform float _Global_G;
		uniform sampler2D _NormalTexture;
		uniform float4 _NormalTexture_ST;
		uniform float _Brightness;
		uniform float _Brightness_A;
		uniform sampler2D _Albedo_A;
		uniform float4 _Color_A;
		uniform float _Desaturation_A;
		uniform float _Brightness_B;
		uniform sampler2D _Albedo_B;
		uniform float4 _Color_B;
		uniform float _Desaturation_B;
		uniform sampler2D _MaskTexture;
		uniform float4 _MaskTexture_ST;
		uniform float _Desaturation;
		uniform sampler2D _Metallic_A;
		uniform sampler2D _Metallic_B;
		uniform float _Rough_A;
		uniform sampler2D _Roughness_A;
		uniform float _Rough_B;
		uniform sampler2D _Roughness_B;

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float2 temp_output_4_0 = ( i.uv_texcoord * _UV );
			float temp_output_56_0 = ( i.vertexColor.g + ( i.vertexColor.g * tex2D( _Mask, ( i.uv_texcoord * _VColor_G_Scale ) ).r ) );
			float lerpResult73 = lerp( 1.0 , temp_output_56_0 , _Global_G);
			#ifdef _LAYER_SWAP_ON
				float staticSwitch66 = ( 1.0 - lerpResult73 );
			#else
				float staticSwitch66 = lerpResult73;
			#endif
			float clampResult75 = clamp( staticSwitch66 , 0.0 , 1.0 );
			float3 lerpResult36 = lerp( UnpackNormal( tex2D( _Normal_A, temp_output_4_0 ) ) , UnpackNormal( tex2D( _Normal_B, temp_output_4_0 ) ) , clampResult75);
			float2 uv_NormalTexture = i.uv_texcoord * _NormalTexture_ST.xy + _NormalTexture_ST.zw;
			#ifdef _ADD_NORMAL_ON
				float3 staticSwitch2 = BlendNormals( lerpResult36 , UnpackNormal( tex2D( _NormalTexture, uv_NormalTexture ) ) );
			#else
				float3 staticSwitch2 = lerpResult36;
			#endif
			o.Normal = staticSwitch2;
			float4 tex2DNode6 = tex2D( _Albedo_A, temp_output_4_0 );
			#ifdef _COLOR_CHANGE_A_ON
				float4 staticSwitch8 = ( _Color_A * tex2DNode6 );
			#else
				float4 staticSwitch8 = tex2DNode6;
			#endif
			float3 desaturateInitialColor13 = ( _Brightness_A * staticSwitch8 ).rgb;
			float desaturateDot13 = dot( desaturateInitialColor13, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar13 = lerp( desaturateInitialColor13, desaturateDot13.xxx, _Desaturation_A );
			float4 tex2DNode18 = tex2D( _Albedo_B, temp_output_4_0 );
			#ifdef _COLOR_CHANGE_B_ON
				float4 staticSwitch21 = ( _Color_B * tex2DNode18 );
			#else
				float4 staticSwitch21 = tex2DNode18;
			#endif
			float3 desaturateInitialColor23 = ( _Brightness_B * staticSwitch21 ).rgb;
			float desaturateDot23 = dot( desaturateInitialColor23, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar23 = lerp( desaturateInitialColor23, desaturateDot23.xxx, _Desaturation_B );
			float3 lerpResult26 = lerp( desaturateVar13 , desaturateVar23 , clampResult75);
			float2 uv_MaskTexture = i.uv_texcoord * _MaskTexture_ST.xy + _MaskTexture_ST.zw;
			float4 tex2DNode42 = tex2D( _MaskTexture, uv_MaskTexture );
			float3 lerpResult43 = lerp( lerpResult26 , desaturateVar13 , tex2DNode42.r);
			#ifdef _ADD_MASK_ON
				float3 staticSwitch15 = lerpResult43;
			#else
				float3 staticSwitch15 = lerpResult26;
			#endif
			float3 desaturateInitialColor68 = ( _Brightness * staticSwitch15 );
			float desaturateDot68 = dot( desaturateInitialColor68, float3( 0.299, 0.587, 0.114 ));
			float3 desaturateVar68 = lerp( desaturateInitialColor68, desaturateDot68.xxx, _Desaturation );
			o.Albedo = desaturateVar68;
			float lerpResult34 = lerp( tex2D( _Metallic_A, temp_output_4_0 ).r , tex2D( _Metallic_B, temp_output_4_0 ).r , clampResult75);
			#ifdef _NON_METALLIC_ON
				float staticSwitch39 = 0.0;
			#else
				float staticSwitch39 = lerpResult34;
			#endif
			o.Metallic = staticSwitch39;
			float temp_output_27_0 = ( _Rough_A * tex2D( _Roughness_A, temp_output_4_0 ).r );
			float lerpResult31 = lerp( temp_output_27_0 , ( _Rough_B * tex2D( _Roughness_B, temp_output_4_0 ).r ) , clampResult75);
			float lerpResult48 = lerp( lerpResult31 , temp_output_27_0 , tex2DNode42.r);
			#ifdef _ADD_MASK_ROUGH_ON
				float staticSwitch49 = lerpResult48;
			#else
				float staticSwitch49 = lerpResult31;
			#endif
			o.Smoothness = staticSwitch49;
			o.Alpha = 1;
		}

		ENDCG
	}
	Fallback "Diffuse"
}
/*ASEBEGIN
Version=19105
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;11;-156.5792,-134.8477;Inherit;False;2;2;0;FLOAT;0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;17;-700.0759,-78.54805;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SamplerNode;6;-989.3759,50.09082;Inherit;True;Property;_Albedo_A;Albedo_A;15;0;Create;True;0;0;0;False;0;False;-1;abc00000000007045897824049021830;abc00000000012431439308118642558;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;22;-326.9283,582.5164;Inherit;False;2;2;0;FLOAT;0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.DesaturateOpNode;23;44.16302,540.4305;Inherit;False;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;20;-858.2562,465.7533;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.RangedFloatNode;19;-636.3475,356.8226;Inherit;False;Property;_Brightness_B;Brightness_B;25;0;Create;True;0;0;0;False;0;False;1;1.04;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.StaticSwitch;21;-666.3724,527.3295;Inherit;False;Property;_Color_Change_B;Color_Change_B;23;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;COLOR;0,0,0,0;False;0;COLOR;0,0,0,0;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;4;COLOR;0,0,0,0;False;5;COLOR;0,0,0,0;False;6;COLOR;0,0,0,0;False;7;COLOR;0,0,0,0;False;8;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;27;-414.8889,1011.762;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;12;-805.415,985.229;Inherit;False;Property;_Rough_A;Rough_A;19;0;Create;True;0;0;0;False;0;False;1;1.23;-5;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;28;-372.6752,1410.317;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;30;-763.2013,1383.784;Inherit;False;Property;_Rough_B;Rough_B;30;0;Create;True;0;0;0;False;0;False;1;1.17;-5;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.LerpOp;31;92.6511,1159.129;Inherit;False;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SamplerNode;29;-776.932,1493.162;Inherit;True;Property;_Roughness_B;Roughness_B;29;0;Create;True;0;0;0;False;0;False;-1;abc00000000009428763281507424020;98166acf28dfa0a4d843d8490f1d36d8;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;32;-778.321,2028.16;Inherit;True;Property;_Metallic_B;Metallic_B;31;0;Create;True;0;0;0;False;0;False;-1;abc00000000009428763281507424020;abc00000000009428763281507424020;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;33;-772.7394,1829.31;Inherit;True;Property;_Metallic_A;Metallic_A;21;0;Create;True;0;0;0;False;0;False;-1;abc00000000009428763281507424020;abc00000000009428763281507424020;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;5;-756.6697,2375.85;Inherit;True;Property;_Normal_A;Normal_A;20;0;Create;True;0;0;0;False;0;False;-1;c8c245ed52054eb429927574439d1d8b;abc00000000005106269873718007130;True;0;True;bump;Auto;True;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.TextureCoordinatesNode;1;-1970.621,1073.565;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;4;-1690.129,1091.373;Inherit;False;2;2;0;FLOAT2;0,0;False;1;FLOAT;0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.LerpOp;34;59.79346,1805.543;Inherit;False;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.BlendNormalsNode;37;424.1231,2483.874;Inherit;False;0;3;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.SamplerNode;10;-835.3703,1106.776;Inherit;True;Property;_Roughness_A;Roughness_A;18;0;Create;True;0;0;0;False;0;False;-1;abc00000000009428763281507424020;89ab4556af0d3234889da19d17581f97;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.LerpOp;26;408.9414,234.1863;Inherit;False;3;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.RangedFloatNode;7;-497.0959,-155.03;Inherit;False;Property;_Brightness_A;Brightness_A;16;0;Create;True;0;0;0;False;0;False;1;0.81;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;9;-165.6768,-16.58696;Inherit;False;Property;_Desaturation_A;Desaturation_A;17;0;Create;True;0;0;0;False;0;False;0;0;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;46;1666.206,205.0372;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.StandardSurfaceOutputNode;0;2372.32,658.5513;Float;False;True;-1;2;;0;0;Standard;M_Standart_Blend;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;Back;0;False;;0;False;;False;0;False;;0;False;;False;0;Opaque;0.5;True;True;0;False;Opaque;;Geometry;All;12;all;True;True;True;True;0;False;;False;0;False;;255;False;;255;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;False;2;15;10;25;False;0.5;True;0;0;False;;0;False;;0;0;False;;0;False;;0;False;;0;False;;0;False;0;0,0,0,0;VertexOffset;True;False;Cylindrical;False;True;Relative;0;;-1;-1;-1;-1;0;False;0;0;False;;-1;0;False;;0;0;0;False;0.1;False;;0;False;;False;16;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT;0;False;9;FLOAT;0;False;10;FLOAT;0;False;13;FLOAT3;0,0,0;False;11;FLOAT3;0,0,0;False;12;FLOAT3;0,0,0;False;14;FLOAT4;0,0,0,0;False;15;FLOAT3;0,0,0;False;0
Node;AmplifyShaderEditor.SamplerNode;42;635.6526,583.0199;Inherit;True;Property;_MaskTexture;MaskTexture;12;0;Create;True;0;0;0;False;0;False;-1;abc00000000009428763281507424020;abc00000000009428763281507424020;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.LerpOp;43;986.3418,123.621;Inherit;False;3;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.LerpOp;48;1053.766,951.4332;Inherit;False;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;50;-4124.591,2073.351;Inherit;False;Property;_VColor_G_Scale;VColor_G_Scale;0;1;[Header];Create;True;2;Vertex Color_G;_____________________;0;0;False;0;False;1.37;0.5;0;10;0;1;FLOAT;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;51;-4079.688,1924.957;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;52;-3757.188,1969.257;Inherit;False;2;2;0;FLOAT2;0,0;False;1;FLOAT;0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.VertexColorNode;53;-3563.808,1532.151;Inherit;False;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;55;-3103.944,1784.787;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleAddOpNode;56;-2964.333,1710.109;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.LerpOp;36;45.36452,2459.19;Inherit;False;3;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.RangedFloatNode;40;10.57922,1618.528;Inherit;False;Constant;_Float0;Float 0;24;0;Create;True;0;0;0;False;0;False;0;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.StaticSwitch;15;1302.757,202.1759;Inherit;False;Property;_Add_Mask;Add_Mask;8;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;FLOAT3;0,0,0;False;0;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT3;0,0,0;False;4;FLOAT3;0,0,0;False;5;FLOAT3;0,0,0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.StaticSwitch;49;1302.223,1036.523;Inherit;False;Property;_Add_Mask_Rough;Add_Mask_Rough;7;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;FLOAT;0;False;0;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT;0;False;7;FLOAT;0;False;8;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.StaticSwitch;39;306.5792,1647.528;Inherit;False;Property;_Non_Metallic;Non_Metallic;10;0;Create;True;0;0;0;False;0;False;0;1;1;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;FLOAT;0;False;0;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT;0;False;7;FLOAT;0;False;8;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SamplerNode;54;-3535.39,1932.847;Inherit;True;Property;_Mask;Mask;11;0;Create;True;0;0;0;False;0;False;-1;abc00000000000149353655522420383;abc00000000000149353655522420383;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.ColorNode;25;-1151.388,389.0787;Inherit;False;Property;_Color_B;Color_B;24;0;Create;True;0;0;0;False;0;False;1,1,1,0;1,1,1,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.ColorNode;16;-1086.5,-203.8967;Inherit;False;Property;_Color_A;Color_A;13;1;[Header];Create;True;2;Layer_A;_____________________;0;0;False;0;False;1,1,1,0;1,1,1,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;18;-1146.204,599.8011;Inherit;True;Property;_Albedo_B;Albedo_B;22;1;[Header];Create;True;2;Layer_B;_____________________;0;0;False;0;False;-1;abc00000000005219264639637305418;f8232031c2875ce41b3cda7976f1e946;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.StaticSwitch;2;739.1216,2333.491;Inherit;False;Property;_Add_Normal;Add_Normal;6;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;FLOAT3;0,0,0;False;0;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT3;0,0,0;False;4;FLOAT3;0,0,0;False;5;FLOAT3;0,0,0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT3;0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.RangedFloatNode;44;1359.837,97.38066;Inherit;False;Property;_Brightness;Brightness;4;1;[Header];Create;True;2;Global;_____________________;0;0;False;0;False;1;0.94;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;3;-2007.028,1238.185;Inherit;False;Property;_UV;UV;9;1;[Header];Create;True;0;0;0;False;0;False;1;1;0;10;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;45;1554.234,362.0807;Inherit;False;Property;_Desaturation;Desaturation;5;0;Create;True;0;0;0;False;0;False;0;0;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.DesaturateOpNode;68;1891.179,215.0112;Inherit;False;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.RangedFloatNode;24;-307.6331,473.6328;Inherit;False;Property;_Desaturation_B;Desaturation_B;26;0;Create;True;0;0;0;False;0;False;0;0;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.StaticSwitch;8;-481.151,-3.451871;Inherit;False;Property;_Color_Change_A;Color_Change_A;14;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;COLOR;0,0,0,0;False;0;COLOR;0,0,0,0;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;4;COLOR;0,0,0,0;False;5;COLOR;0,0,0,0;False;6;COLOR;0,0,0,0;False;7;COLOR;0,0,0,0;False;8;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.DesaturateOpNode;13;163.2825,-101.19;Inherit;False;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.PowerNode;71;-2536.481,1701.847;Inherit;False;False;2;0;FLOAT;0;False;1;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.LerpOp;58;-2366.774,1833.36;Inherit;False;3;0;FLOAT;1;False;1;FLOAT;0;False;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.PowerNode;70;-2763.576,1673.62;Inherit;False;False;2;0;FLOAT;0;False;1;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;69;-3086.895,1945.621;Inherit;False;Property;_Details_G;Details_G;1;0;Create;True;0;0;0;False;0;False;1.1;1.78;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.OneMinusNode;67;-2092.018,1857.566;Inherit;False;1;0;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.LerpOp;73;-2461.915,2077.216;Inherit;False;3;0;FLOAT;1;False;1;FLOAT;0;False;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;57;-2768.13,1934.491;Inherit;False;Property;_Global_G;Global_G;2;0;Create;True;0;0;0;False;0;False;1.1;3.63;0;5;0;1;FLOAT;0
Node;AmplifyShaderEditor.StaticSwitch;66;-1964.323,1671.84;Inherit;False;Property;_Layer_Swap;Layer_Swap;3;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;FLOAT;0;False;0;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT;0;False;7;FLOAT;0;False;8;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.ClampOpNode;72;-2214.852,2049.292;Inherit;False;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.ClampOpNode;75;-1715.133,1670.335;Inherit;False;3;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;1;False;1;FLOAT;0
Node;AmplifyShaderEditor.SamplerNode;35;-751.0662,2620.226;Inherit;True;Property;_Normal_B;Normal_B;27;0;Create;True;0;0;0;False;0;False;-1;c8c245ed52054eb429927574439d1d8b;abc00000000007042257557727383858;True;0;True;bump;Auto;True;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;76;-263.0731,2787.169;Inherit;True;Property;_NormalTexture;NormalTexture;28;0;Create;True;0;0;0;False;0;False;-1;c8c245ed52054eb429927574439d1d8b;abc00000000007042257557727383858;True;0;True;bump;Auto;True;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
WireConnection;11;0;7;0
WireConnection;11;1;8;0
WireConnection;17;0;16;0
WireConnection;17;1;6;0
WireConnection;6;1;4;0
WireConnection;22;0;19;0
WireConnection;22;1;21;0
WireConnection;23;0;22;0
WireConnection;23;1;24;0
WireConnection;20;0;25;0
WireConnection;20;1;18;0
WireConnection;21;1;18;0
WireConnection;21;0;20;0
WireConnection;27;0;12;0
WireConnection;27;1;10;1
WireConnection;28;0;30;0
WireConnection;28;1;29;1
WireConnection;31;0;27;0
WireConnection;31;1;28;0
WireConnection;31;2;75;0
WireConnection;29;1;4;0
WireConnection;32;1;4;0
WireConnection;33;1;4;0
WireConnection;5;1;4;0
WireConnection;4;0;1;0
WireConnection;4;1;3;0
WireConnection;34;0;33;1
WireConnection;34;1;32;1
WireConnection;34;2;75;0
WireConnection;37;0;36;0
WireConnection;37;1;76;0
WireConnection;10;1;4;0
WireConnection;26;0;13;0
WireConnection;26;1;23;0
WireConnection;26;2;75;0
WireConnection;46;0;44;0
WireConnection;46;1;15;0
WireConnection;0;0;68;0
WireConnection;0;1;2;0
WireConnection;0;3;39;0
WireConnection;0;4;49;0
WireConnection;43;0;26;0
WireConnection;43;1;13;0
WireConnection;43;2;42;1
WireConnection;48;0;31;0
WireConnection;48;1;27;0
WireConnection;48;2;42;1
WireConnection;52;0;51;0
WireConnection;52;1;50;0
WireConnection;55;0;53;2
WireConnection;55;1;54;1
WireConnection;56;0;53;2
WireConnection;56;1;55;0
WireConnection;36;0;5;0
WireConnection;36;1;35;0
WireConnection;36;2;75;0
WireConnection;15;1;26;0
WireConnection;15;0;43;0
WireConnection;49;1;31;0
WireConnection;49;0;48;0
WireConnection;39;1;34;0
WireConnection;39;0;40;0
WireConnection;54;1;52;0
WireConnection;18;1;4;0
WireConnection;2;1;36;0
WireConnection;2;0;37;0
WireConnection;68;0;46;0
WireConnection;68;1;45;0
WireConnection;8;1;6;0
WireConnection;8;0;17;0
WireConnection;13;0;11;0
WireConnection;13;1;9;0
WireConnection;71;0;70;0
WireConnection;71;1;57;0
WireConnection;58;1;56;0
WireConnection;58;2;57;0
WireConnection;70;0;56;0
WireConnection;70;1;69;0
WireConnection;67;0;73;0
WireConnection;73;1;56;0
WireConnection;73;2;57;0
WireConnection;66;1;73;0
WireConnection;66;0;67;0
WireConnection;72;0;73;0
WireConnection;75;0;66;0
WireConnection;35;1;4;0
ASEEND*/
//CHKSM=70612A65312BCBE468162F7E6F45000E06903F1D