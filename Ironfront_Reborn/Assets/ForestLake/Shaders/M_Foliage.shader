// Made with Amplify Shader Editor v1.9.1.5
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "M_Foliage"
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
		#pragma shader_feature_local _USEROUGHNESS_ON
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
/*ASEBEGIN
Version=19105
Node;AmplifyShaderEditor.ColorNode;7;-819.5756,-125.5093;Inherit;False;Property;_Grass_Color;Grass_Color;4;0;Create;True;0;0;0;False;0;False;1,1,1,0;1,1,1,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;5;-585.6808,-399.9884;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SamplerNode;1;-830.8749,267.425;Inherit;True;Property;_NormalMap;Normal Map;2;0;Create;True;0;0;0;False;0;False;-1;793d558ed2ef5d247878f2e5d3e2ca7d;abc00000000009174826200331873443;True;0;True;bump;Auto;True;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;4;-911.9753,136.925;Inherit;False;Property;_Brightness;Brightness;3;0;Create;True;0;0;0;False;0;False;1.5;1.38;0;10;0;1;FLOAT;0
Node;AmplifyShaderEditor.DitheringNode;16;-783.4272,-636.6221;Inherit;False;2;False;4;0;FLOAT;0;False;1;SAMPLER2D;;False;2;FLOAT4;0,0,0,0;False;3;SAMPLERSTATE;;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;25;-1273.585,-917.2874;Inherit;False;Constant;_Float0;Float 0;8;0;Create;True;0;0;0;False;0;False;1;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.TexturePropertyNode;22;-1120.735,-596.7262;Inherit;True;Property;_Texture0;Texture 0;6;0;Create;True;0;0;0;False;0;False;None;a6bb3265c102a244387807ae59fac3f3;False;white;Auto;Texture2D;-1;0;2;SAMPLER2D;0;SAMPLERSTATE;1
Node;AmplifyShaderEditor.StaticSwitch;24;-1102.781,-803.0611;Inherit;False;Property;_LOD_FADE_CROSSFADE;LOD_FADE_CROSSFADE;7;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;FLOAT;0;False;0;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT;0;False;7;FLOAT;0;False;8;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.LODFadeNode;14;-1430.808,-792.804;Inherit;False;False;0;5;FLOAT4;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;9;-451.552,124.0511;Inherit;False;Property;_Desaturation;Desaturation;5;0;Create;True;0;0;0;False;0;False;0;0;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.SamplerNode;2;-1095.856,-335.3963;Inherit;True;Property;_Albedo;Albedo;1;0;Create;True;0;0;0;False;0;False;-1;33462f1a7f808884193bb283f39c511c;abc00000000009035436622715290406;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;27;-24.59971,399.2276;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SamplerNode;29;-374.2996,261.4275;Inherit;True;Property;_T_Black_A;T_Black_A;9;0;Create;True;0;0;0;False;0;False;-1;dfef6813ce0bbf041b2dc83ae97af2a5;dfef6813ce0bbf041b2dc83ae97af2a5;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;28;-395.0998,509.7274;Inherit;False;Property;_Roughness;Roughness;8;0;Create;True;0;0;0;False;0;False;0;1;0;3;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;31;-46.79163,209.9218;Inherit;False;Constant;_Float1;Float 1;11;0;Create;True;0;0;0;False;0;False;0;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.DesaturateOpNode;8;304.9525,-311.8056;Inherit;False;2;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;3;-254.9751,-281.2751;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.StandardSurfaceOutputNode;42;526.5751,-294.0283;Float;False;True;-1;2;;0;0;Standard;M_Foliage;False;False;False;False;False;False;False;False;False;False;False;False;True;False;False;False;False;False;False;False;False;Off;0;False;;0;False;;False;0;False;;0;False;;False;0;Custom;0.5;True;True;0;True;Transparent;;Geometry;All;12;all;True;True;True;True;0;False;;False;0;False;;255;False;;255;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;0;False;;False;2;15;10;25;False;0.5;True;0;0;False;;0;False;;0;0;False;;0;False;;0;False;;0;False;;0;False;0;0,0,0,0;VertexOffset;True;False;Cylindrical;False;True;Relative;0;;0;-1;-1;-1;0;False;0;0;False;;-1;0;False;;0;0;0;False;0.1;False;;0;False;;False;16;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT;0;False;9;FLOAT;0;False;10;FLOAT;0;False;13;FLOAT3;0,0,0;False;11;FLOAT3;0,0,0;False;12;FLOAT3;0,0,0;False;14;FLOAT4;0,0,0,0;False;15;FLOAT3;0,0,0;False;0
Node;AmplifyShaderEditor.StaticSwitch;30;195.1085,127.8213;Inherit;False;Property;_UseRoughness;UseRoughness;10;0;Create;True;0;0;0;False;0;False;0;0;0;True;;Toggle;2;Key0;Key1;Create;True;True;All;9;1;COLOR;0,0,0,0;False;0;COLOR;0,0,0,0;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;4;COLOR;0,0,0,0;False;5;COLOR;0,0,0,0;False;6;COLOR;0,0,0,0;False;7;COLOR;0,0,0,0;False;8;COLOR;0,0,0,0;False;1;COLOR;0
WireConnection;5;0;2;0
WireConnection;5;1;7;0
WireConnection;16;0;24;0
WireConnection;16;1;22;0
WireConnection;24;1;25;0
WireConnection;24;0;14;1
WireConnection;27;0;29;0
WireConnection;27;1;28;0
WireConnection;8;0;3;0
WireConnection;8;1;9;0
WireConnection;3;0;5;0
WireConnection;3;1;4;0
WireConnection;42;0;8;0
WireConnection;42;1;1;0
WireConnection;42;4;30;0
WireConnection;42;10;2;4
WireConnection;30;1;31;0
WireConnection;30;0;27;0
ASEEND*/
//CHKSM=009667629708590D508211B5E80CE98A853122F0