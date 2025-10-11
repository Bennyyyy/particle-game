Shader "Unlit/Particle2D"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Size ("Size", Float) = 0.02
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Pos; // xy pos, w size
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Size;

            struct appdata
            {
                float3 vertex:POSITION;
                float2 uv:TEXCOORD0;
                uint id:SV_InstanceID;
            };

            struct v2f
            {
                float4 pos:SV_POSITION;
                float2 uv:TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 p = _Pos[v.id];
                float s = max(p.w, _Size);
                float2 worldXY = p.xy + v.vertex.xy * s;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldXY, 0, 1));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDHLSL
        }
    }
}