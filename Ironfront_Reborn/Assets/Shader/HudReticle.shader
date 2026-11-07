// A mounted gun's reticle: unlit, one flat colour, drawn over everything in front of it.
//
// The RHIB's bow gun and the helicopter's door gun hang their crosshair 200 m down the barrel
// ("Gunner HUD", the Plane.asset cross). It was drawn with "HUD Green", whose shader is the
// decompiler's stub for "UI/Additive" (Assets/Shader/UIAdditive.shader): an opaque, lit,
// depth-tested surface. So the cross vanished behind anything nearer than 200 m -- a beach, a
// wall, the hill the gunner was shooting at -- and the gun had no aim mark exactly when it was
// in use (owner report 2026-11-07, "the boat's machine gun cannot be used").
//
// ZTest Always and the Overlay queue put it on top of the scene the gun camera draws; it writes
// no depth, so nothing drawn after it is hidden behind it.
Shader "Ironfront/HUD Reticle"
{
    Properties
    {
        _Color ("Colour", Color) = (0.62, 1, 0.62, 0.95)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Overlay"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata_t
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
}
