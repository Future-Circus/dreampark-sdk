// The one shader every DreamBand projector PARTICLE draws with.
//
// ── Why not URP/Particles/Unlit ──
// Two reasons, both learned the hard way on this project.
//  1. URP/Unlit (the non-particle one) ignores the COLOR vertex stream
//     entirely, so `color over lifetime` and `start color` — the two modules
//     this whole effect animates with — silently did nothing.
//  2. URP/Particles/Unlit DOES read vertex colour, but its blend state is
//     owned by a ShaderGUI: a material built from script gets a half-configured
//     keyword set (_ALPHAPREMULTIPLY_ON / _ALPHAMODULATE_ON / _SURFACE_TYPE_*)
//     that looks right in the inspector and renders wrong in a build. The
//     builder authors these materials with no human in the loop, so the blend
//     state has to be plain material floats, not keywords.
//
// So: one pass, vertex colour x tint x sprite, blend factors exposed as
// properties. Additive materials set (SrcAlpha, One); alpha-blended ones set
// (SrcAlpha, OneMinusSrcAlpha). Nothing else varies.
//
// ── LDR, on purpose ──
// The URP asset has m_SupportsHDR: 0 and no Bloom anywhere, so the colour
// buffer is R8G8B8A8 and anything above 1.0 clamps to flat white. Output is
// saturated here deliberately: brightness is supposed to come from ADDITIVE
// OVERLAP between soft sprites, which accumulates toward white on its own while
// the soft edges stay dim. Raising _BaseColor past 1 does not make it brighter,
// it makes it flatter — see the note block in HoloTheme.cs.

Shader "DreamPark/HoloParticle"
{
    Properties
    {
        [MainTexture] _BaseMap   ("Sprite", 2D) = "white" {}
        [MainColor]   _BaseColor ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 1   // One (additive)
        _Exposure ("Exposure", Range(0,4)) = 1
        // 1 for ParticleSystems, 0 for plain meshes. Unity's built-in Quad has
        // NO colour stream, and what a shader reads from an unbound vertex
        // attribute is backend-dependent — on some Adreno drivers it is zero,
        // which would render the progress bar invisible on exactly the hardware
        // this ships to. So the multiply is opt-in rather than assumed.
        [Toggle] _VertexColor ("Multiply by vertex colour", Float) = 1
        // LEqual (4) everywhere by default. The playlist bookends' readability fallback sets Always (8) on a
        // board that geometry would otherwise hide (BookendLook.SetXray), drawn last.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 4
        // 1 only on UI panels drawn on top (MagicOnTop, Oct 22 2026): the player's real hand still covers them
        // (Meta environment depth, gated by the tracked-hand capsules; DreamParkHandOcclusion.hlsl). 0 = unchanged.
        [Toggle] _DPHandOcclude ("Panel: real hands occlude", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "IgnoreProjector"="True"
            "PreviewType"="Plane"
        }

        Pass
        {
            Name "HoloParticleForward"
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull Off
            // LEqual, not Always: a hand passing in front of the band must
            // occlude its own projector. Depth-defeating light on a passthrough
            // headset reads as broken rather than as an overlay.
            ZTest [_ZTest]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Required for XR single-pass instanced, which is how this project
            // renders. Without it every particle draws to the left eye only.
            #pragma multi_compile_instancing
            // Meta passthrough occlusion keywords (set globally by EnvironmentDepthManager on Quest). Used ONLY for a
            // panel's hand occlusion (_DPHandOcclude); every other HoloParticle material ignores them.
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/DreamPark/Shaders/DreamParkHandOcclusion.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                float3 posWorld   : TEXCOORD1;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                float  _SrcBlend;
                float  _DstBlend;
                float  _Exposure;
                float  _VertexColor;
                float  _DPHandOcclude;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv    = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color = IN.color;
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                OUT.posWorld = TransformObjectToWorld(IN.positionOS.xyz);
                #endif
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);

                // vertex colour carries start colour x colour-over-lifetime;
                // _BaseColor carries the material tint x the rig's fade, written
                // through a MaterialPropertyBlock. Multiplying rather than
                // adding keeps every knob independent.
                // Uniform branch — the compiler folds this to a select, and it
                // costs nothing next to the blend it feeds.
                half4 vc = _VertexColor > 0.5 ? IN.color : half4(1, 1, 1, 1);
                half4 c = tex * _BaseColor * vc;

                c.a   = saturate(c.a * _Exposure);
                c.rgb = saturate(c.rgb);
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
