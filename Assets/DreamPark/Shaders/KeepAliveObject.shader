/**
 * This shader is a hot fix for a Quest bug we encountered where when the Depth Texture was on.
 * 
 * BUG: If the user looked at meshes or anything in view, and then, turned to an area without anything in view,
 *      the blitted frame would freeze in the corner of your screen until you looked at something again that was in view.
 * 
 * SOLUTION: This shader is put on a material that just writes a transparent color, so a color frame is submitted.
 * Then, we put a 0.00001 scale sphere 1 meter in front a HeadTracker in the DreamPark prefab to make sure a color frame is always being executed.
 * This will force the camera to not freeze a blitted frame.
 * 
 * WHY it happens? Quest or Unity or Vulkan settings seems to try to save rendering when it thinks it doesn't need to render anything
 * when depth texture is on.
 */
Shader "DreamPark/KeepAliveObject"
{
    Properties
    {
        _Tint("Tint (ignored visually)", Color) = (0,0,0,0)
        _Alpha("Alpha (tiny > 0 recommended)", Range(0,1)) = 0.01
    }

    SubShader
    {
        // Transparent queue so it renders even when opaques are empty
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }

        // --- PASS 0: Depth ping (no color). Writes FAR depth, never occludes real geometry.
        Pass
        {
            Name "KeepAliveDepth"
            Tags { "LightMode"="DepthOnly" }

            Cull Back
            ZWrite On
            ZTest Always
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings  { float4 positionCS : SV_Position; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                return o;
            }

            float4 frag (out float outDepth : SV_Depth) : SV_Target
            {
                // Write FAR depth for both normal-Z and reversed-Z
                #if defined(UNITY_REVERSED_Z)
                    outDepth = 0.0;  // FAR in reversed-Z (URP/Quest often uses this)
                #else
                    outDepth = 1.0;  // FAR in normal-Z
                #endif
                return 0;
            }
            ENDHLSL
        }

        // --- PASS 1: Color touch. Writes a tiny transparent color so a scene frame is submitted.
        Pass
        {
            Name "KeepAliveColor"
            Tags { "LightMode"="UniversalForward" }

            Cull Back
            ZWrite Off
            ZTest Always
            // Overwrite (One Zero) or standard blend (SrcAlpha OneMinusSrcAlpha) both work.
            // We'll use standard alpha blend; with small alpha it's invisible.
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGBA

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings  { float4 positionCS : SV_Position; };

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float  _Alpha;
            CBUFFER_END

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                return o;
            }

            float4 frag () : SV_Target
            {
                // Visually invisible but guarantees a color write. 
                // If you truly want zero visual impact, _Alpha = 0 is fine too.
                return float4(_Tint.rgb, _Alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}