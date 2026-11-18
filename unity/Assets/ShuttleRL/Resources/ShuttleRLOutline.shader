// Selection outline used by RackHud (works with URP and the built-in pipeline: one unlit pass, no LightMode tag).
// Drawn twice per mesh with two materials:
//   mask    (_Width 0, ColorMask 0, stencil Always/Replace) : marks the object's own pixels
//   outline (_Width > 0, stencil NotEqual)                  : mesh grown outward, drawn only outside the mask
// ZTest Always so the outline stays visible behind rack frames. The mask uses a lower render queue.
Shader "Hidden/ShuttleRL/Outline"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.72, 0.1, 1)
        _Width ("Width (fraction of camera distance)", Float) = 0.0035
        _Center ("Object-space bounds center", Vector) = (0, 0, 0, 0)
        _Extents ("Object-space bounds extents", Vector) = (1, 1, 1, 0)
        _StencilComp ("Stencil Comp", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 2
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask [_ColorMask]
            Stencil { Ref 77 Comp [_StencilComp] Pass [_StencilPass] }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;
            float _Width;
            float4 _Center;
            float4 _Extents;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert (appdata v)
            {
                v2f o;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                if (_Width > 0)
                {
                    // grow each face of the part's bounding box by the same amount: a closed hull even on
                    // hard-edged (flat-normal) meshes and long thin parts
                    float3 n = (v.vertex.xyz - _Center.xyz) / max(_Extents.xyz, 1e-4);
                    float3 ax = normalize(mul((float3x3)unity_ObjectToWorld, float3(1, 0, 0)));
                    float3 ay = normalize(mul((float3x3)unity_ObjectToWorld, float3(0, 1, 0)));
                    float3 az = normalize(mul((float3x3)unity_ObjectToWorld, float3(0, 0, 1)));
                    wp += (ax * n.x + ay * n.y + az * n.z) * _Width * distance(_WorldSpaceCameraPos, wp);
                }
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return _Color; }
            ENDCG
        }
    }
}
