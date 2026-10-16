Shader "DreamPark/Portal"
{
    // DreamPark/Portal — the PORTAL MATERIAL (Oct 2026, golf-portal). Put it on any mesh with a PortalSurface component:
    // wall windows, floors, both together. It shows the PortalScene rendered by PortalRenderer for the eye that is drawing,
    // sampled at this fragment's SCREEN position, so every surface of one scene shows one continuous view (no seam where a
    // wall meets a floor) and it reads as a window into a real 3D place in each eye (stereo: _PortalTexL / _PortalTexR
    // picked by unity_StereoEyeIndex; single-pass instanced and multiview).
    //
    // The surface itself is ordinary opaque content (queue Geometry-4, before the SDK's depth-only floor occluders): it
    // writes depth at the surface and is Meta-occluded by real geometry nearer than it (hands, legs, furniture), with
    // _MetaDepthBias so the real wall / floor it lies on never eats it (pair a floor portal with a DepthMask mesh too).
    // Where the scene drew nothing (PortalScene.emptyIsPassthrough, the default) the surface discards the pixel: the real world
    // shows, never a flat colour (a teleport iris shrinking the scene away, a scene not streamed in yet).
    // Without a portal texture for the current camera (Scene view, catalog previews, no PortalRenderer) it shows
    // _FallbackColor × _FallbackTex (mesh UVs).

    Properties
    {
        [Header(Fallback (no portal texture for this camera))]
        _FallbackColor("Fallback Color", Color) = (0.45, 0.70, 0.35, 1)
        _FallbackTex("Fallback Texture (mesh UV)", 2D) = "white" {}

        [Header(Meta occlusion)]
        _MetaDepthBias("Meta Depth Bias (fraction nearer)", Range(0, 0.5)) = 0.06

        [Header(Surface)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2

        // set per camera by PortalRenderer (MaterialPropertyBlock) — never authored
        [HideInInspector] _PortalTexL("Portal L", 2D) = "black" {}
        [HideInInspector] _PortalTexR("Portal R", 2D) = "black" {}
        [HideInInspector] _PortalRectL("Rect L (uv min, 1/uv size)", Vector) = (0, 0, 1, 1)
        [HideInInspector] _PortalRectR("Rect R (uv min, 1/uv size)", Vector) = (0, 0, 1, 1)
        [HideInInspector] _PortalOn("Portal On", Float) = 0
        [HideInInspector] _PortalEyeOverride("Eye Override (-1 = stereo eye)", Float) = -1
        [HideInInspector] _PortalEmptyClip("Empty pixels show the real world", Float) = 0
        [HideInInspector] _PortalHide("Show the real world (scene has nothing to draw)", Float) = 0
        [HideInInspector] _PortalFade("Fade (1 = there, 0 = dissolved to the real world)", Float) = 1
    }

    SubShader
    {
        // Geometry-4 = 1996: BEFORE the SDK's depth-only floors (LevelFloor / GapFiller FloorOccluder, 1998), which lie exactly where a
        // floor portal lies (y 0) and would otherwise z-fight it away in patches; they are ColorMask 0, so drawn after us they erase nothing
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-4" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _FallbackColor;
            float4 _FallbackTex_ST;
            float _MetaDepthBias;
            float4 _PortalRectL;
            float4 _PortalRectR;
            float _PortalOn;
            float _PortalEyeOverride;
            float _PortalEmptyClip;
            float _PortalHide;
            float _PortalFade;
        CBUFFER_END

        // PortalSurface.fade: a per-pixel dissolve (interleaved gradient noise on the pixel position, stable from frame to frame)
        void PortalFadeClip(float2 pixel)
        {
            if (_PortalFade < 0.999)
            {
                float n = frac(52.9829189 * frac(dot(floor(pixel), float2(0.06711056, 0.00583715))));
                clip(_PortalFade - n - 0.0001);
            }
        }
        ENDHLSL

        Pass
        {
            Name "Portal"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            TEXTURE2D(_FallbackTex); SAMPLER(sampler_FallbackTex);
            TEXTURE2D(_PortalTexL); SAMPLER(sampler_PortalTexL);
            TEXTURE2D(_PortalTexR); SAMPLER(sampler_PortalTexR);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float2 uv : TEXCOORD1;
                META_DEPTH_VERTEX_OUTPUT(2)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionCS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _FallbackTex);
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(OUT, IN.positionOS.xyz);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 c;
                // PortalScene.emptyIsPassthrough: where the scene drew nothing (alpha 0 in its texture), or it has nothing to draw at all,
                // the surface is not there: the real world shows (never a flat colour)
                clip(0.5 - _PortalHide);
                PortalFadeClip(IN.positionCS.xy);
                if (_PortalOn > 0.5)
                {
                    float2 suv = IN.screenPos.xy / IN.screenPos.w;
                    #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                        float eye = (float)unity_StereoEyeIndex;
                    #else
                        float eye = 0.0;
                    #endif
                    if (_PortalEyeOverride >= 0.0) eye = _PortalEyeOverride;
                    if (eye < 0.5)
                        c = SAMPLE_TEXTURE2D(_PortalTexL, sampler_PortalTexL, (suv - _PortalRectL.xy) * _PortalRectL.zw);
                    else
                        c = SAMPLE_TEXTURE2D(_PortalTexR, sampler_PortalTexR, (suv - _PortalRectR.xy) * _PortalRectR.zw);
                    if (_PortalEmptyClip > 0.5) clip(c.a - 0.5);
                    c.a = 1.0;
                }
                else
                {
                    c = SAMPLE_TEXTURE2D(_FallbackTex, sampler_FallbackTex, IN.uv) * _FallbackColor;
                    c.a = 1.0;
                }

                // Meta environment occlusion of the SURFACE (no-op when neither keyword is on)
                float occ = META_DEPTH_GET_OCCLUSION_VALUE(IN, _MetaDepthBias);
                clip(occ - 0.5);
                return c;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            TEXTURE2D(_PortalTexL); SAMPLER(sampler_PortalTexL);
            TEXTURE2D(_PortalTexR); SAMPLER(sampler_PortalTexR);

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float4 screenPos : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionCS);
                return OUT;
            }
            // the same "nothing there" test as the colour pass, so a depth prepass never leaves depth where the real world shows
            half Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                clip(0.5 - _PortalHide);
                PortalFadeClip(IN.positionCS.xy);
                if (_PortalOn > 0.5 && _PortalEmptyClip > 0.5)
                {
                    float2 suv = IN.screenPos.xy / IN.screenPos.w;
                    #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                        float eye = (float)unity_StereoEyeIndex;
                    #else
                        float eye = 0.0;
                    #endif
                    if (_PortalEyeOverride >= 0.0) eye = _PortalEyeOverride;
                    half a = eye < 0.5
                        ? SAMPLE_TEXTURE2D(_PortalTexL, sampler_PortalTexL, (suv - _PortalRectL.xy) * _PortalRectL.zw).a
                        : SAMPLE_TEXTURE2D(_PortalTexR, sampler_PortalTexR, (suv - _PortalRectR.xy) * _PortalRectR.zw).a;
                    clip(a - 0.5);
                }
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
