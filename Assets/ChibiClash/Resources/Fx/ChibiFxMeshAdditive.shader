// Chibi Clash additive unlit shader for procedural FX meshes (ChibiTrailRibbon blade / glow ribbons).
// colour = texture x vertex colour x _Color; _Color carries the HDR multiplier (ChibiFxTextures.Peak = 3) so white-hot
// cores reach 3.0 and bloom through ChibiPostFx (threshold 0.82). No sprite per-draw data (works on MeshRenderers).
// No LightMode tag = SRPDefaultUnlit, which the URP 2D renderer draws. Lives in Resources so builds include it.
Shader "ChibiClash/FxMeshAdditive"
{
    Properties
    {
        _MainTex ("Gradient", 2D) = "white" {}
        [HDR] _Color ("Tint (HDR)", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Blend SrcAlpha One, One One
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS);
                o.uv = TRANSFORM_TEX(input.uv, _MainTex);
                o.color = input.color * _Color;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color;
            }
            ENDHLSL
        }
    }
}
