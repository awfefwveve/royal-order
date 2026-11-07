// 구운 애니메이션(VAT) 몬스터용 표준 셰이더 (Built-in RP, GPU 인스턴싱).
// 정점마다 uv2.x = VAT 열 번호, 마리마다 _VatFrame = 프레임 (MaterialPropertyBlock 배열).
// 위치·법선 텍스처: 가로 = 정점 수, 세로 = 프레임 수 (RGBAHalf, 점 샘플링).
Shader "KnifeWar/VatStandard"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.35
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _VatPos ("VAT Position", 2D) = "black" {}
        _VatNrm ("VAT Normal", 2D) = "black" {}
        _VatFrame ("Frame", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma multi_compile_instancing
        #pragma instancing_options assumeuniformscaling
        #pragma target 3.5

        sampler2D _MainTex;
        sampler2D _BumpMap;
        sampler2D _VatPos;
        sampler2D _VatNrm;
        float4 _VatPos_TexelSize;
        half _Glossiness;
        half _Metallic;
        fixed4 _Color;

        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(float, _VatFrame)
        UNITY_INSTANCING_BUFFER_END(Props)

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 tangent : TANGENT;
            float4 texcoord : TEXCOORD0;
            float4 texcoord1 : TEXCOORD1;
            float4 texcoord2 : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Input
        {
            float2 uv_MainTex;
        };

        void vert(inout appdata v)
        {
            UNITY_SETUP_INSTANCE_ID(v);
            float frame = UNITY_ACCESS_INSTANCED_PROP(Props, _VatFrame);
            float u = (v.texcoord2.x + 0.5) * _VatPos_TexelSize.x;
            float w = (floor(frame) + 0.5) * _VatPos_TexelSize.y;
            v.vertex.xyz = tex2Dlod(_VatPos, float4(u, w, 0, 0)).xyz;
            v.normal = normalize(tex2Dlod(_VatNrm, float4(u, w, 0, 0)).xyz);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
