// DreamPark/SpriteOnTop (Oct 22 2026, ui-on-top): the sprite parts of in-world UI panels (the pause card's backing and
// bars, a sync card's mark) drawn on top of the world, because Aidan wants every UI panel over walls, occluders and
// 3D objects, with only the player's real hands covering them.
// Same output as URP's Sprite-Unlit-Default (texture x vertex colour x _Color x the SpriteRenderer colour, flip),
// plus a depth test you can set (_ZTest; MagicOnTop sets Always) and the panel hand occlusion
// (DreamParkHandOcclusion.hlsl, _DPHandOcclude). MagicOnTop makes these materials; nothing else uses this shader.
// Lives in Resources so Shader.Find resolves it in a build.
Shader "DreamPark/SpriteOnTop"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 8
        [Toggle] _DPHandOcclude ("Panel: real hands occlude", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
            "RenderPipeline"="UniversalPipeline"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [_ZTest]
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

        Pass
        {
            Name "SpriteOnTop"
            Tags { "LightMode"="SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"
            #include "Assets/DreamPark/Shaders/DreamParkHandOcclusion.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                float3 posWorld   : TEXCOORD1;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _ZTest;
                float _DPHandOcclude;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                SetUpSpriteInstanceProperties();
                float3 pos = UnityFlipSprite(IN.positionOS.xyz, unity_SpriteProps.xy);
                OUT.positionCS = TransformObjectToHClip(pos);
                OUT.uv = IN.uv;
                OUT.color = IN.color * _Color * unity_SpriteColor;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                OUT.posWorld = TransformObjectToWorld(pos);
                #endif
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * IN.color;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                if (_DPHandOcclude > 0.5) c.a *= DPHandVisibility(IN.posWorld, 0.0);
                #endif
                return c;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
