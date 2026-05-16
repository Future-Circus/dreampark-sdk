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

        [Header(Second Color)]
        // Two-tone particle tint. The "second color tex" is a noise/
        // gradient mask sampled at the base UV — wherever the mask is
        // white the second color shows through; black keeps the base
        // color. CFXR uses this heavily for two-tone smoke, fire with
        // a hot inner core, and magical color-shifting fog. Default off.
        [Toggle(_SECONDCOLOR_ON)] _SecondColorEnable("Enable Second Color", Float) = 0
        [HDR] _SecondColor("Second Color", Color) = (1, 1, 1, 1)
        _SecondColorTex("Second Color Mask (R)", 2D) = "black" {}
        _SecondColorSmooth("Transition Softness", Range(0, 1)) = 0.5

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

        [Header(Pseudo Lighting)]
        // Unlit pipeline cheat: when a normal map is bound, dot it against
        // a fixed light direction (in tangent space, which for billboard
        // particles ≈ screen space) and modulate the diffuse contribution.
        // Gives surface relief on rocks, droplets, magical orbs, embers —
        // anything where the vendor authored a normal map for lit shading.
        // Doesn't subscribe to URP's real lighting pipeline (no light loop,
        // no shadows). One texture sample + ~8 ALU ops when on, free when
        // off. Default off so plain billboard sprites stay cheap.
        [Toggle(_PSEUDO_LIT_ON)] _PseudoLit("Enable Pseudo Lighting", Float) = 0
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0, 2)) = 1.0
        // Fake light direction in TANGENT SPACE. (0, 1, 1) gives a nice
        // top-front lit look on billboards — like sun coming over your
        // shoulder. Artists can rotate per-material if they want a
        // different feel. Z component points toward the camera.
        _FakeLightDir("Fake Light Direction", Vector) = (0, 1, 1, 0)
        // How much the lighting modulates the final diffuse. 0 = no
        // lighting (back to flat), 1 = full modulation. 0.7 default
        // gives clear relief without going dark in unlit areas.
        _FakeLightStrength("Lighting Strength", Range(0, 1)) = 0.7
        // Floor brightness on surfaces facing AWAY from the light. 0 =
        // black backsides (high contrast), 1 = no backside darkening.
        // 0.3 default keeps shadowed faces visible without crushing.
        _FakeLightAmbient("Ambient Floor", Range(0, 1)) = 0.3

        // Fresnel / rim glow. Uses the same normal map sampled above
        // (so it's free — no extra texture sample). Tangent-space view
        // direction is (0,0,1) for billboards, so fresnel becomes
        // `1 - normal.z` raised to a power. Default color black so
        // existing pseudo-lit materials get zero contribution — opt-in
        // via setting a non-zero color. Naturally gated by normal-map
        // variation: a flat-normal material has fresnel = 0 everywhere.
        [HDR] _FresnelColor("Fresnel / Rim Color", Color) = (0, 0, 0, 0)
        _FresnelPower("Fresnel Power (curve sharpness)", Range(1, 16)) = 4.0

        [Header(Edge Fade)]
        // Soft alpha vignette around the rectangular quad borders. Fades
        // the outer EdgeFadeWidth fraction of UV space from full opacity
        // (interior) to zero (rim). Use for cloud / smoke / dust effects
        // whose authored texture has a hard rectangular silhouette and
        // needs the runtime border softening to look organic. Default off.
        [Toggle(_EDGE_FADE_ON)] _EdgeFade("Enable Edge Fade", Float) = 0
        _EdgeFadeWidth("Edge Fade Width", Range(0, 0.5)) = 0.1

        [Header(Camera Fade)]
        // Fade particle alpha based on the fragment's distance from the
        // camera plane (linear eye depth). Common URP/Particles feature
        // for keeping projectile/aura effects from clipping into the
        // player's face — particles fully invisible below "Near",
        // linearly ramping to fully visible by "Far". Default off.
        [Toggle(_CAMERAFADE_ON)] _CameraFade("Enable Camera Fade", Float) = 0
        _CameraNearFadeDistance("Camera Near Fade Distance", Float) = 1.0
        _CameraFarFadeDistance("Camera Far Fade Distance", Float)  = 2.0

        [Header(Channel Mode)]
        [Toggle(_SINGLECHANNEL_ON)] _SingleChannel("Use R Channel as Alpha (CFXR-style)", Float) = 0

        [Header(Emission)]
        // Emission is gated by _EMISSION_ON so a material that doesn't
        // use it pays zero variant cost — no extra texture sample, no
        // extra ALU. Default off; the C# MaterialConverter flips it on
        // automatically when the source material had a non-default
        // _EmissionColor or any flavor of emission map / strength /
        // intensity property. _EmissionMap defaults to "white" so a
        // material that enables emission without authoring a map gets
        // a solid emission color (texture sample becomes identity).
        [Toggle(_EMISSION_ON)] _Emission("Enable Emission", Float) = 0
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0, 0)
        _EmissionMap("Emission Map", 2D) = "white" {}
        _EmissionStrength("Emission Strength", Range(0, 16)) = 1.0

        [Toggle(_HDR_BOOST_ON)] _HdrBoost("Enable HDR Boost", Float) = 0
        _HdrBoostMultiplier("HDR Boost Multiplier", Range(1, 16)) = 1.0

        [Header(Dissolve)]
        [Toggle(_DISSOLVE_ON)] _Dissolve("Enable Dissolve", Float) = 0
        _DissolveMap("Dissolve Map (R channel = noise)", 2D) = "white" {}
        _DissolveAmount("Dissolve Amount", Range(0, 1)) = 0.0
        _DissolveEdgeWidth("Edge Width", Range(0, 0.5)) = 0.05
        [HDR] _DissolveEdgeColor("Edge Color", Color) = (1, 0.5, 0, 1)

        [Header(Noise Modulation)]
        // Sample a scrolling noise texture and modulate BOTH the base
        // color AND the emission by separate intensity dials. Used by
        // Hovl Studio fire/explosion effects to add the "breathing"
        // animated brightness variation — the noise scrolls over time
        // so the bright/dark regions of the particle shift, creating
        // the live-flame look. Default off (zero cost when unused).
        [Toggle(_NOISE_MOD_ON)] _NoiseMod("Enable Noise Modulation", Float) = 0
        _NoiseModTex("Noise Map (R channel)", 2D) = "white" {}
        _NoiseModSpeedX("Scroll X (units/sec)", Float) = 0
        _NoiseModSpeedY("Scroll Y (units/sec)", Float) = 0
        // 0 = no modulation (full base), 1 = base fully driven by noise.
        // Typical fire / explosion materials use ~0.4–0.7.
        _NoiseModBasePower("Base Modulation Power", Range(0, 1)) = 0.5
        // Separate dial for emission modulation. 0 = steady glow,
        // 1 = glow flickers fully with the noise.
        _NoiseModGlowPower("Glow Modulation Power", Range(0, 1)) = 0.5

        [Header(UV Distortion)]
        [Toggle(_DISTORTION_ON)] _Distortion("Enable UV Distortion", Float) = 0
        _DistortionMap("Distortion Map (RG = direction)", 2D) = "bump" {}
        _DistortionStrength("Distortion Strength", Range(0, 0.5)) = 0.1
        _DistortionScrollX("Distortion Scroll X (units/sec)", Float) = 0.0
        _DistortionScrollY("Distortion Scroll Y (units/sec)", Float) = 0.0

        [Header(UV Distortion Layer 2)]
        // Second distortion layer for dual-noise effects like lightning,
        // energy streams, magical turbulence. The two layers scroll at
        // different speeds and sum into a single UV warp — the visual
        // result is "wiggle that doesn't move in any one direction"
        // which a single layer can't produce. Default off, zero cost.
        [Toggle(_DISTORTION2_ON)] _Distortion2("Enable Second Distortion Layer", Float) = 0
        _DistortionMap2("Distortion Map 2 (RG = direction)", 2D) = "bump" {}
        _DistortionStrength2("Distortion Strength 2", Range(0, 0.5)) = 0.1
        _DistortionScroll2X("Distortion 2 Scroll X (units/sec)", Float) = 0.0
        _DistortionScroll2Y("Distortion 2 Scroll Y (units/sec)", Float) = 0.0

        [Header(Radial UV)]
        // Polar / radial UV transformation. Wraps a rectangular texture
        // around a ring or disk: U becomes angle (0..1 around the circle)
        // and V becomes radius (0=center, 1=edge). Used by ring effects,
        // magic circles, shockwaves, halos, portal rings. Default off
        // (zero cost). The inner-radius cutoff carves out a hole in the
        // middle for true ring shapes (vs filled disks).
        [Toggle(_RADIAL_UV_ON)] _RadialUV("Enable Radial UV", Float) = 0
        _RadialUVCenter("Radial Center (UV)", Vector) = (0.5, 0.5, 0, 0)
        _RadialUVInnerRadius("Inner Radius (ring hole)", Range(0, 0.5)) = 0.0
        _RadialUVRotation("Rotation (degrees)", Float) = 0.0

        [Header(Overlay)]
        // Second texture layer blended on top of the base color before
        // vertex color modulation. Mirrors CFXR's "perlin overlay" pattern
        // used to add organic detail / breakup on flat textures.
        // Default off (zero cost when not used).
        [Toggle(_OVERLAY_ON)] _Overlay("Enable Overlay", Float) = 0
        _OverlayTex("Overlay Map", 2D) = "white" {}
        [Enum(Multiply, 0, Additive, 1)] _OverlayBlendMode("Overlay Blend Mode", Float) = 0
        _OverlayStrength("Overlay Strength", Range(0, 1)) = 1.0
        _OverlayScrollX("Overlay Scroll X (units/sec)", Float) = 0.0
        _OverlayScrollY("Overlay Scroll Y (units/sec)", Float) = 0.0

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
            #pragma shader_feature_local _CAMERAFADE_ON
            #pragma shader_feature_local _SINGLECHANNEL_ON
            #pragma shader_feature_local _HDR_BOOST_ON
            #pragma shader_feature_local _EMISSION_ON
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma shader_feature_local _DISTORTION_ON
            #pragma shader_feature_local _DISTORTION2_ON
            #pragma shader_feature_local _OVERLAY_ON
            #pragma shader_feature_local _RADIAL_UV_ON
            #pragma shader_feature_local _EDGE_FADE_ON
            #pragma shader_feature_local _SECONDCOLOR_ON
            #pragma shader_feature_local _PSEUDO_LIT_ON
            #pragma shader_feature_local _NOISE_MOD_ON

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
                float4 _EmissionMap_ST;
                float  _EmissionStrength;
                float  _Cutoff;
                float  _SoftParticlesNear;
                float  _SoftParticlesFar;
                float  _CameraNearFadeDistance;
                float  _CameraFarFadeDistance;
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
                // Distortion layer 2 (dual-noise, lightning, energy)
                float4 _DistortionMap2_ST;
                float  _DistortionStrength2;
                float  _DistortionScroll2X;
                float  _DistortionScroll2Y;
                // Overlay
                float4 _OverlayTex_ST;
                float  _OverlayBlendMode;
                float  _OverlayStrength;
                float  _OverlayScrollX;
                float  _OverlayScrollY;
                // Radial UV
                float4 _RadialUVCenter;
                float  _RadialUVInnerRadius;
                float  _RadialUVRotation;
                // Edge fade
                float  _EdgeFadeWidth;
                // Second color (two-tone tint)
                float4 _SecondColor;
                float4 _SecondColorTex_ST;
                float  _SecondColorSmooth;
                // Pseudo lighting
                float4 _BumpMap_ST;
                float  _BumpScale;
                float4 _FakeLightDir;
                float  _FakeLightStrength;
                float  _FakeLightAmbient;
                float4 _FresnelColor;
                float  _FresnelPower;
                // Noise modulation (Hovl Studio style)
                float4 _NoiseModTex_ST;
                float  _NoiseModSpeedX;
                float  _NoiseModSpeedY;
                float  _NoiseModBasePower;
                float  _NoiseModGlowPower;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_DissolveMap);
            SAMPLER(sampler_DissolveMap);
            TEXTURE2D(_DistortionMap);
            SAMPLER(sampler_DistortionMap);
            TEXTURE2D(_DistortionMap2);
            SAMPLER(sampler_DistortionMap2);
            TEXTURE2D(_OverlayTex);
            SAMPLER(sampler_OverlayTex);
            TEXTURE2D(_SecondColorTex);
            SAMPLER(sampler_SecondColorTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_NoiseModTex);
            SAMPLER(sampler_NoiseModTex);

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
                // ── UV distortion (1 or 2 layers) ────────────────────────
                // Each layer samples its own noise texture at independent
                // scroll velocities and contributes a UV offset. Both
                // layers sample the ORIGINAL quad UV (not the running
                // warped baseUv) so they don't compound multiplicatively
                // — they sum cleanly, which is what produces the natural
                // "doesn't wiggle in any one direction" look for lightning
                // and energy effects. Layer 2 is keyword-gated and
                // independent of layer 1; you can use either or both.
                float2 baseUv = IN.uv;
                #if defined(_DISTORTION_ON) || defined(_DISTORTION2_ON)
                    float2 totalWarp = float2(0.0, 0.0);
                    #if defined(_DISTORTION_ON)
                        float2 dUv = IN.uv * _DistortionMap_ST.xy + _DistortionMap_ST.zw;
                        dUv += _Time.y * float2(_DistortionScrollX, _DistortionScrollY);
                        half2 distort = SAMPLE_TEXTURE2D(_DistortionMap, sampler_DistortionMap, dUv).rg * 2.0h - 1.0h;
                        totalWarp += distort * _DistortionStrength;
                    #endif
                    #if defined(_DISTORTION2_ON)
                        float2 dUv2 = IN.uv * _DistortionMap2_ST.xy + _DistortionMap2_ST.zw;
                        dUv2 += _Time.y * float2(_DistortionScroll2X, _DistortionScroll2Y);
                        half2 distort2 = SAMPLE_TEXTURE2D(_DistortionMap2, sampler_DistortionMap2, dUv2).rg * 2.0h - 1.0h;
                        totalWarp += distort2 * _DistortionStrength2;
                    #endif
                    baseUv += totalWarp;
                #endif

                // ── Radial UV (polar transformation) ─────────────────────
                // Converts planar UV → polar so a horizontal texture stripe
                // wraps around as a ring. U output = angle [0..1) around
                // the circle. V output = normalized radius from the inner
                // radius cutoff to the outer edge of the centered unit
                // circle. Used by ring effects, magic circles, shockwaves,
                // halos, portal rings.
                //
                // Order matters: this runs AFTER distortion (so distortion
                // warps the source UVs before they get reshaped into
                // polar) but BEFORE the base sample (we need to know the
                // final polar UV to fetch a texel).
                #if defined(_RADIAL_UV_ON)
                    float2 centered = baseUv - _RadialUVCenter.xy;

                    // Optional rotation. Most ring effects don't rotate
                    // via this dial (they spin via ParticleSystem Rotation
                    // Over Lifetime), but it's there for cases that do.
                    if (abs(_RadialUVRotation) > 0.001)
                    {
                        float rotRad = _RadialUVRotation * 0.01745329; // π/180
                        float cosR = cos(rotRad);
                        float sinR = sin(rotRad);
                        centered = float2(centered.x * cosR - centered.y * sinR,
                                          centered.x * sinR + centered.y * cosR);
                    }

                    // Polar: angle [0..1) wraps around, radius is 0 at
                    // center and 1 at the edge of the [-0.5, 0.5] centered
                    // square (which is why we multiply length by 2).
                    float angle  = atan2(centered.y, centered.x) * 0.15915494 + 0.5; // 1 / (2π)
                    float radius = length(centered) * 2.0;

                    // Inner-radius cutoff carves the hole in the middle
                    // for true ring shapes (vs filled disks). Fragments
                    // inside the hole get discarded.
                    if (radius < _RadialUVInnerRadius) discard;

                    // Remap [innerRadius, 1] → [0, 1] so the texture
                    // stretches across the visible ring band.
                    float ringSpan = max(1.0 - _RadialUVInnerRadius, 1e-4);
                    float v = saturate((radius - _RadialUVInnerRadius) / ringSpan);

                    baseUv = float2(angle, v);
                #endif

                // ── Base sample ──────────────────────────────────────────
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, baseUv);

                #if defined(_SINGLECHANNEL_ON)
                    // CFXR convention: store alpha in R channel, RGB unused (saves texture memory).
                    // Treat R as the alpha and use the material's tint as the RGB.
                    tex = half4(1.0h, 1.0h, 1.0h, tex.r);
                #endif

                // ── Overlay (second texture layer) ───────────────────────
                // Multiply OR Additive blend of a second texture over the
                // base color. Mirrors CFXR's "perlin overlay" pattern —
                // a scrolling noise texture adds organic breakup detail
                // on top of an otherwise flat base. Both math paths run
                // unconditionally (cheaper than another keyword variant),
                // then we lerp by _OverlayBlendMode (0=multiply, 1=add)
                // and again by _OverlayStrength to blend the result with
                // the un-overlaid base. Zero cost when _OVERLAY_ON is off.
                #if defined(_OVERLAY_ON)
                    float2 overlayUv = baseUv * _OverlayTex_ST.xy + _OverlayTex_ST.zw;
                    overlayUv += _Time.y * float2(_OverlayScrollX, _OverlayScrollY);
                    half3 overlay = SAMPLE_TEXTURE2D(_OverlayTex, sampler_OverlayTex, overlayUv).rgb;
                    half3 multiplied = tex.rgb * overlay;
                    half3 added      = tex.rgb + overlay;
                    half3 blended    = lerp(multiplied, added, _OverlayBlendMode);
                    tex.rgb = lerp(tex.rgb, blended, _OverlayStrength);
                #endif

                // ── Vertex color modulation ──────────────────────────────
                #if defined(_VC_MULTIPLY)
                    tex *= IN.color;
                #elif defined(_VC_ADD)
                    tex.rgb += IN.color.rgb * IN.color.a;
                    tex.a    = saturate(tex.a + IN.color.a);
                #endif
                // _VC_OFF: leave tex untouched.

                // ── Noise modulation pre-sample ──────────────────────────
                // Sample the noise texture ONCE, at the original quad UV
                // (NOT the post-distortion / post-radial baseUv) — the
                // noise scrolls over the quad's local space, independent
                // of any texture-space transformations. We hold the value
                // in `noiseVal` so it can modulate BOTH the diffuse tint
                // (a few lines down) and the emission (in the emission
                // block) without re-sampling.
                //
                // noiseVal defaults to 1.0 so the lerp(1, noise, power)
                // formulas below become identity when the keyword is off.
                half noiseVal = 1.0h;
                #if defined(_NOISE_MOD_ON)
                    float2 noiseUv = IN.uv * _NoiseModTex_ST.xy + _NoiseModTex_ST.zw;
                    noiseUv += _Time.y * float2(_NoiseModSpeedX, _NoiseModSpeedY);
                    noiseVal = SAMPLE_TEXTURE2D(_NoiseModTex, sampler_NoiseModTex, noiseUv).r;
                #endif

                // ── Material tint (optional two-tone via Second Color) ───
                // When _SECONDCOLOR_ON is active, sample the second-color
                // mask texture and lerp between the base color and the
                // second color based on the mask's R channel. The mask is
                // typically a noise / gradient texture; _SecondColorSmooth
                // controls how soft the transition is (0 = hard edge at
                // mask=0.5, 1 = fully smooth gradient across [0,1]).
                #if defined(_SECONDCOLOR_ON)
                    float2 secondUv = baseUv * _SecondColorTex_ST.xy + _SecondColorTex_ST.zw;
                    half mask = SAMPLE_TEXTURE2D(_SecondColorTex, sampler_SecondColorTex, secondUv).r;
                    half halfW = _SecondColorSmooth * 0.5h;
                    half smoothMask = smoothstep(0.5h - halfW, 0.5h + halfW, mask);
                    half4 tint;
                    tint.rgb = lerp(_BaseColor.rgb, _SecondColor.rgb, smoothMask);
                    tint.a   = lerp(_BaseColor.a,   _SecondColor.a,   smoothMask);
                    half4 c = tex * tint;
                #else
                    half4 c = tex * _BaseColor;
                #endif

                // ── Apply noise modulation to the diffuse contribution ──
                // lerp(1, noise, power): power=0 → c unchanged (identity),
                // power=1 → c fully driven by noise. Power dial is the
                // intensity of the noise breathing effect on base color.
                #if defined(_NOISE_MOD_ON)
                    c.rgb *= lerp(1.0h, noiseVal, saturate(_NoiseModBasePower));
                #endif

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

                // ── Soft particles (depth fade against scene) ────────────
                #if defined(_SOFTPARTICLES_ON)
                    float sceneEyeDepth = LinearEyeDepth(
                        SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture,
                                           UnityStereoTransformScreenSpaceTex(IN.projPos.xy / IN.projPos.w)).r,
                        _ZBufferParams);
                    float fragEyeDepth  = IN.projPos.z;
                    float fade = saturate((sceneEyeDepth - fragEyeDepth - _SoftParticlesNear) / max(_SoftParticlesFar, 1e-4));
                    c.a *= fade;
                #endif

                // ── Camera fade (depth fade against camera) ──────────────
                // Faded out close to the camera plane; ramps to fully opaque
                // by _CameraFarFadeDistance. Reuses projPos.z (already in
                // Varyings for soft particles) which is the linear eye depth
                // = camera-plane Z distance to this fragment. Cheap.
                //
                // Common URP/Particles feature; in VR especially useful for
                // projectile auras that approach the player's face.
                #if defined(_CAMERAFADE_ON)
                    float camDist = IN.projPos.z;
                    float camRange = max(_CameraFarFadeDistance - _CameraNearFadeDistance, 1e-4);
                    float camFade = saturate((camDist - _CameraNearFadeDistance) / camRange);
                    c.a *= camFade;
                #endif

                // ── Edge fade (alpha vignette on quad borders) ───────────
                // Smoothly fades the outer EdgeFadeWidth band of UV space
                // from full opacity to zero. Operates on the un-transformed
                // quad UVs (IN.uv) — i.e. the rectangular billboard's edges
                // in world space, NOT the post-distortion / post-radial UV.
                // That's intentional: edge fade is about hiding the quad's
                // silhouette, not the texture content.
                #if defined(_EDGE_FADE_ON)
                    float2 quadDist = abs(IN.uv - 0.5) * 2.0;             // 0 at center, 1 at edge
                    float maxDist = max(quadDist.x, quadDist.y);
                    float edgeFade = 1.0 - smoothstep(1.0 - _EdgeFadeWidth, 1.0, maxDist);
                    c.a *= edgeFade;
                #endif

                // ── Pseudo lighting (unlit pipeline normal-map cheat) ────
                // Sample a tangent-space normal map and dot against a fixed
                // light direction. For billboard particles, tangent space ≈
                // screen space, so a light direction of (0, 1, 1) gives the
                // particle a "lit from top-front" feel — like sun coming
                // over the player's shoulder.
                //
                // Not real lighting: ignores scene lights, no shadows,
                // doesn't subscribe to URP's light loop. But for stylized
                // MR particles where lit-feeling matters more than physical
                // correctness (rocks, droplets, embers, magic orbs), this
                // gives ~95% of the visual win for ~5% of the cost.
                //
                // Order matters: applied AFTER the tinted color is finalized
                // but BEFORE HDR boost and emission. So lighting modulates
                // the diffuse contribution while emission sits on top
                // unaffected — emissive parts always glow regardless of
                // the fake light direction.
                #if defined(_PSEUDO_LIT_ON)
                    float2 nrmUv = baseUv * _BumpMap_ST.xy + _BumpMap_ST.zw;
                    half3 tangentNormal = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, nrmUv),
                        _BumpScale);
                    half3 fakeLight = normalize(_FakeLightDir.xyz);
                    half NdotL = saturate(dot(tangentNormal, fakeLight));
                    // Ambient floor so unlit-facing pixels aren't pure
                    // black — keeps the particle visible all around.
                    half lighting = lerp(_FakeLightAmbient, 1.0h, NdotL);
                    // Strength dial: blend between "no lighting" (flat
                    // diffuse) and "full lighting" (modulated by NdotL).
                    half3 lit = c.rgb * lighting;
                    c.rgb = lerp(c.rgb, lit, _FakeLightStrength);

                    // ── Fresnel rim glow ────────────────────────────
                    // (1 - normal.z) is the grazing-angle term in
                    // tangent space — 0 at center, 1 at edges where the
                    // perturbed normal points sideways. Raised to a
                    // power for sharpness, modulated by _FresnelColor
                    // (alpha as HDR intensity). Naturally produces 0
                    // contribution when _FresnelColor is black, so
                    // pseudo-lit materials without authored fresnel
                    // see zero visible change.
                    half fresnel = 1.0h - saturate(tangentNormal.z);
                    fresnel = pow(fresnel, _FresnelPower);
                    c.rgb += _FresnelColor.rgb * _FresnelColor.a * fresnel;
                #endif

                // ── HDR boost ────────────────────────────────────────────
                // Amplifies the entire base color (independent of emission).
                // Use when an artist wants the whole sprite to glow brighter
                // without authoring an emission map.
                #if defined(_HDR_BOOST_ON)
                    c.rgb *= _HdrBoostMultiplier;
                #endif

                // ── Emission ─────────────────────────────────────────────
                // Optional emission contribution. Gated on _EMISSION_ON so
                // a particle that doesn't use emission costs zero variant
                // budget. Pipeline matches URP/Standard convention:
                //
                //   emission = sample(_EmissionMap, baseUv).rgb
                //            × _EmissionColor.rgb
                //            × _EmissionColor.a    // legacy HDR-intensity-in-alpha
                //            × _EmissionStrength   // explicit dial
                //
                // _EmissionMap defaults to "white" so a material that enables
                // emission without authoring a map gets a uniform glow from
                // _EmissionColor × _EmissionStrength.
                //
                // Sampled with baseUv (post-distortion) so distortion-warped
                // particles keep emission glued to the warped surface — this
                // is the right behavior for heat-haze, magical-trail, and
                // smoke-distort effects.
                #if defined(_EMISSION_ON)
                    float2 emUv = baseUv * _EmissionMap_ST.xy + _EmissionMap_ST.zw;
                    half3 emTex = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, emUv).rgb;
                    half3 emission = emTex
                                   * _EmissionColor.rgb
                                   * _EmissionColor.a
                                   * _EmissionStrength;
                    // Modulate emission by the same noise sample used on
                    // the diffuse, but with the SEPARATE glow-power dial.
                    // Lets fire/explosion materials have a "flickering
                    // glow" effect — Hovl Studio's signature look.
                    #if defined(_NOISE_MOD_ON)
                        emission *= lerp(1.0h, noiseVal, saturate(_NoiseModGlowPower));
                    #endif
                    c.rgb += emission;
                #endif

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
