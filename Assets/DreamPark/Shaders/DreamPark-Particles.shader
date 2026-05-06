Shader "DreamPark/Particles"
{
    // DreamPark-Particles — unified Unlit Particle shader for DreamPark MR.
    //
    // Designed to replace vendor particle shaders (CFXR_Particle_StandardHDR,
    // StandardParticles, Epic Toon FX custom, RunemarkStudio particles, etc.) with
    // a single shader that covers ~95% of the features actually used across
    // Cartoon FX Remaster, Epic Toon FX, Super Confetti FX, and RunemarkStudio
    // VFX packs imported into rpg-quest (807 materials surveyed).
    //
    // Feature coverage (each gated by a keyword for variant economy):
    //   • Blend modes: Alpha, Additive, Premultiplied, Multiply  (_BLENDMODE_*)
    //   • Alpha cutoff (cutout)                                  (_ALPHATEST_ON)
    //   • Vertex color modulation (Off / Multiply / Add)         (_VC_*)
    //   • Soft particles (depth fade against scene geometry)     (_SOFTPARTICLES_ON)
    //   • Single-channel alpha (R-channel→A, common in CFXR)     (_SINGLECHANNEL_ON)
    //   • HDR emission boost                                     (_HDR_BOOST_ON)
    //   • Configurable Cull (Off for billboards, Back for mesh)
    //   • Meta passthrough environment occlusion                 (HARD_OCCLUSION / SOFT_OCCLUSION)
    //     — particles are clipped/faded by REAL-WORLD geometry on Quest 3 via
    //       Meta's Depth API. Driven by the standard keyword that Meta's
    //       OcclusionToggle / EnvironmentDepthOcclusion components set globally;
    //       no per-material work needed. When neither keyword is enabled the
    //       Meta macros expand to no-ops (zero runtime cost).
    //   • Dissolve (noise + threshold + edge color)              (_DISSOLVE_ON)  [v2]
    //   • UV distortion (scrolling distortion map)               (_DISTORTION_ON) [v2]
    //
    // Out of scope (rare, MaterialConverter keeps these on vendor shaders):
    //   Flipbook blending (needs ParticleSystem custom vertex streams setup),
    //   dithered shadows (DreamPark particles don't cast shadows in MR — unlit),
    //   lit shading. ~<3% of materials surveyed; can be promoted later if needed.

    Properties
    {
        [Header(Surface)]
        [MainColor] _BaseColor("Tint Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}

        [Header(Blend Mode)]
        [KeywordEnum(Alpha, Additive, Premultiplied, Multiply)] _BlendMode("Blend Mode", Float) = 0

        [Header(Alpha Cutoff)]
        [Toggle(_ALPHATEST_ON)] _AlphaTest("Enable Alpha Cutoff", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Vertex Color)]
        [KeywordEnum(Off, Multiply, Add)] _VC("Vertex Color Mode", Float) = 1

        [Header(Soft Particles)]
        [Toggle(_SOFTPARTICLES_ON)] _SoftParticles("Enable Soft Particles", Float) = 0
        _SoftParticlesNear("Soft Fade Near", Float) = 0.0
        _SoftParticlesFar("Soft Fade Far", Float)  = 1.0

        [Header(Channel Mode)]
        [Toggle(_SINGLECHANNEL_ON)] _SingleChannel("Use R Channel as Alpha (CFXR-style)", Float) = 0

        [Header(Emission)]
        [Toggle(_HDR_BOOST_ON)] _HdrBoost("Enable HDR Boost", Float) = 0
        _HdrBoostMultiplier("HDR Boost Multiplier", Range(1, 16)) = 1.0
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0, 0)

        [Header(Dissolve)]
        [Toggle(_DISSOLVE_ON)] _Dissolve("Enable Dissolve", Float) = 0
        _DissolveMap("Dissolve Map (R channel = noise)", 2D) = "white" {}
        _DissolveAmount("Dissolve Amount", Range(0, 1)) = 0.0
        _DissolveEdgeWidth("Edge Width", Range(0, 0.5)) = 0.05
        [HDR] _DissolveEdgeColor("Edge Color", Color) = (1, 0.5, 0, 1)

        [Header(UV Distortion)]
        [Toggle(_DISTORTION_ON)] _Distortion("Enable UV Distortion", Float) = 0
        _DistortionMap("Distortion Map (RG = direction)", 2D) = "bump" {}
        _DistortionStrength("Distortion Strength", Range(0, 0.5)) = 0.1
        _DistortionScrollX("Distortion Scroll X (units/sec)", Float) = 0.0
        _DistortionScrollY("Distortion Scroll Y (units/sec)", Float) = 0.0

        [Header(Render State)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0     // 0=Off (billboards), 1=Front, 2=Back
        [Enum(Off, 0, On, 1)] _ZWrite("Z Write", Float) = 0                  // particles default OFF; opt-in for solid mesh particles
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("Z Test", Float) = 4   // LessEqual

        [Header(Meta Passthrough Occlusion)]
        // Forward bias applied to environment depth before comparison. Positive
        // values push the virtual fragment slightly toward the camera (less
        // likely to be occluded); useful for particles that should "punch
        // through" thin real-world surfaces. Match what Meta's other shaders use.
        _MetaDepthBias("Environment Depth Bias", Range(-0.02, 0.02)) = 0.0

        // Internal — driven by _BlendMode keyword via the C# converter or Inspector script.
        // We expose them as floats so MaterialConverter can set them deterministically.
        [HideInInspector] _SrcBlend("Src Blend", Float) = 5     // SrcAlpha
        [HideInInspector] _DstBlend("Dst Blend", Float) = 10    // OneMinusSrcAlpha
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }
        LOD 100

        Pass
        {
            Name "DreamPark Particles Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]
            ColorMask RGB

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            // Quest-friendly target. Mobile shader profile.
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            // Feature keywords — keep this list aligned with the C# converter's keyword map.
            #pragma multi_compile_local _BLENDMODE_ALPHA _BLENDMODE_ADDITIVE _BLENDMODE_PREMULTIPLIED _BLENDMODE_MULTIPLY
            #pragma multi_compile_local _VC_OFF _VC_MULTIPLY _VC_ADD
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _SOFTPARTICLES_ON
            #pragma shader_feature_local _SINGLECHANNEL_ON
            #pragma shader_feature_local _HDR_BOOST_ON
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma shader_feature_local _DISTORTION_ON

            // Meta passthrough environment occlusion — driven by the global keyword
            // that Meta's OcclusionToggle / EnvironmentDepthOcclusion components set
            // at runtime. When neither is enabled, all META_DEPTH_* macros expand to
            // no-ops (zero overhead).
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _EmissionColor;
                float  _Cutoff;
                float  _SoftParticlesNear;
                float  _SoftParticlesFar;
                float  _HdrBoostMultiplier;
                float  _MetaDepthBias;
                // Dissolve
                float4 _DissolveMap_ST;
                float4 _DissolveEdgeColor;
                float  _DissolveAmount;
                float  _DissolveEdgeWidth;
                // Distortion
                float4 _DistortionMap_ST;
                float  _DistortionStrength;
                float  _DistortionScrollX;
                float  _DistortionScrollY;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_DissolveMap);
            SAMPLER(sampler_DissolveMap);
            TEXTURE2D(_DistortionMap);
            SAMPLER(sampler_DistortionMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float4 color       : COLOR;
                float4 projPos     : TEXCOORD1;   // for soft particles depth lookup
                float  fogCoord    : TEXCOORD2;
                // Meta passthrough occlusion needs world position. Macro expands to
                // `float3 posWorld : TEXCOORD3;` when HARD_OCCLUSION/SOFT_OCCLUSION
                // is on, and to nothing otherwise.
                META_DEPTH_VERTEX_OUTPUT(3)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs vp = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = vp.positionCS;
                OUT.uv          = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color       = IN.color;
                OUT.projPos     = ComputeScreenPos(vp.positionCS);
                OUT.projPos.z   = -TransformWorldToView(vp.positionWS).z;   // linear eye depth at this fragment
                OUT.fogCoord    = ComputeFogFactor(vp.positionCS.z);
                // Populate world position for Meta occlusion (no-op when keyword off).
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(OUT, IN.positionOS);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // ── UV distortion ────────────────────────────────────────
                // Sample a distortion map's RG channels (mapped to -1..1) and
                // offset the base UV. Map UVs scroll over time so the warp
                // animates — typical use: heat haze, magical trails, smoke.
                float2 baseUv = IN.uv;
                #if defined(_DISTORTION_ON)
                    float2 dUv = baseUv * _DistortionMap_ST.xy + _DistortionMap_ST.zw;
                    dUv += _Time.y * float2(_DistortionScrollX, _DistortionScrollY);
                    half2 distort = SAMPLE_TEXTURE2D(_DistortionMap, sampler_DistortionMap, dUv).rg * 2.0h - 1.0h;
                    baseUv += distort * _DistortionStrength;
                #endif

                // ── Base sample ──────────────────────────────────────────
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, baseUv);

                #if defined(_SINGLECHANNEL_ON)
                    // CFXR convention: store alpha in R channel, RGB unused (saves texture memory).
                    // Treat R as the alpha and use the material's tint as the RGB.
                    tex = half4(1.0h, 1.0h, 1.0h, tex.r);
                #endif

                // ── Vertex color modulation ──────────────────────────────
                #if defined(_VC_MULTIPLY)
                    tex *= IN.color;
                #elif defined(_VC_ADD)
                    tex.rgb += IN.color.rgb * IN.color.a;
                    tex.a    = saturate(tex.a + IN.color.a);
                #endif
                // _VC_OFF: leave tex untouched.

                // ── Material tint ────────────────────────────────────────
                half4 c = tex * _BaseColor;

                // ── Dissolve (noise + threshold + edge color) ────────────
                // Read a noise value from the dissolve map's R channel. Pixels
                // below the threshold are clipped; pixels just above the threshold
                // get a glowing edge color (typical death/spawn FX). _DissolveAmount
                // is usually animated from 0→1 over the particle's lifetime via
                // the Material Property Block / particle custom data.
                #if defined(_DISSOLVE_ON)
                    float dissolveUv_x = baseUv.x * _DissolveMap_ST.x + _DissolveMap_ST.z;
                    float dissolveUv_y = baseUv.y * _DissolveMap_ST.y + _DissolveMap_ST.w;
                    half noise = SAMPLE_TEXTURE2D(_DissolveMap, sampler_DissolveMap,
                                                  float2(dissolveUv_x, dissolveUv_y)).r;
                    half thresh = _DissolveAmount;
                    if (noise < thresh) discard;
                    half edge = saturate((noise - thresh) / max(_DissolveEdgeWidth, 1e-4));
                    // Blend edge color into the result. At edge==0 → full edge color,
                    // at edge==1 → pure base color. _DissolveEdgeColor.a controls intensity.
                    half edgeMask = (1.0h - edge) * _DissolveEdgeColor.a;
                    c.rgb = lerp(c.rgb, _DissolveEdgeColor.rgb, edgeMask);
                #endif

                // ── Alpha cutoff ─────────────────────────────────────────
                #if defined(_ALPHATEST_ON)
                    clip(c.a - _Cutoff);
                #endif

                // ── Soft particles (depth fade) ──────────────────────────
                #if defined(_SOFTPARTICLES_ON)
                    float sceneEyeDepth = LinearEyeDepth(
                        SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture,
                                           UnityStereoTransformScreenSpaceTex(IN.projPos.xy / IN.projPos.w)).r,
                        _ZBufferParams);
                    float fragEyeDepth  = IN.projPos.z;
                    float fade = saturate((sceneEyeDepth - fragEyeDepth - _SoftParticlesNear) / max(_SoftParticlesFar, 1e-4));
                    c.a *= fade;
                #endif

                // ── HDR / emission boost ─────────────────────────────────
                #if defined(_HDR_BOOST_ON)
                    c.rgb *= _HdrBoostMultiplier;
                #endif
                c.rgb += _EmissionColor.rgb * _EmissionColor.a;

                // ── Premultiply alpha if this blend mode expects it ──────
                #if defined(_BLENDMODE_PREMULTIPLIED)
                    c.rgb *= c.a;
                #elif defined(_BLENDMODE_ADDITIVE)
                    // Additive uses Blend SrcAlpha One; multiply RGB by alpha so transparent
                    // pixels contribute zero light without needing the dst term.
                    c.rgb *= c.a;
                #elif defined(_BLENDMODE_MULTIPLY)
                    // Multiply blend (SrcColor * DstColor): bias toward white where alpha=0
                    // so transparent pixels don't darken what's behind them.
                    c.rgb = lerp(half3(1, 1, 1), c.rgb, c.a);
                #endif
                // _BLENDMODE_ALPHA: standard SrcAlpha/OneMinusSrcAlpha, RGB stays as-is.

                // ── Fog ──────────────────────────────────────────────────
                c.rgb = MixFog(c.rgb, IN.fogCoord);

                // ── Meta passthrough environment occlusion ──────────────
                // Modulates `c` by the per-pixel real-world occlusion value.
                // Hard occlusion → multiply by 0 or 1 (clip behind real geometry).
                // Soft occlusion → smooth fade at depth boundary.
                // No-op when neither HARD_OCCLUSION nor SOFT_OCCLUSION is enabled.
                META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY(IN, c, _MetaDepthBias);

                return c;
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Particles/Unlit"
    CustomEditor "UnityEditor.ShaderGraph.GenericShaderGraphMaterialGUI"
}
