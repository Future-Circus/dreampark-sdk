// DreamPark hand occlusion for UI PANELS (Oct 22 2026, ui-on-top; Aidan: "UI Panels should draw over everything but
// if we can still have our hands occlude over the panels thats ideal").
//
// Panels draw over walls, occluders and 3D objects (MagicOnTop: ZTest Always, after the world). The ONE thing that may
// still cover them is the player's real hand. The silhouette is Meta's environment depth, the same texture every
// occlusion shader reads (sanitized by DepthMaskSanitize only with the custom_occlusion flag; raw otherwise). It only
// counts where the real surface it reads lies inside one of the rough tracked-hand / forearm capsules that
// DepthMaskCeiling already sends DepthMaskSanitize's hand gate (published as globals _DPHandA / _DPHandB / _DPHandCount).
// The pose only says WHERE depth may count. Nothing renders a hand mesh, and there are no hand or forearm mesh occluders.
//
// Active only when Meta's EnvironmentDepthManager has set HARD_OCCLUSION / SOFT_OCCLUSION (Quest). Everywhere else
// (Editor, iOS) DPHandVisibility is 1. Per material: _DPHandOcclude (HoloParticle, DreamPark/SpriteOnTop) /
// _DPHandOnly (DreamPark TMP SDF); global kill switch _DPHandsOn (MagicOnTop.Hands).
//
// Include AFTER the shader's own Core / UnityCG include. If Meta's EnvironmentOcclusion include came first (the
// DreamPark TMP shaders), its texture and sampling are reused; otherwise the depth texture is declared here as the
// 2D array it is on the headset (single-pass instanced / multiview).
#ifndef DREAMPARK_HAND_OCCLUSION_INCLUDED
#define DREAMPARK_HAND_OCCLUSION_INCLUDED

#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)

#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED) || defined(USING_STEREO_MATRICES)
#define DP_HAND_EYE ((uint)unity_StereoEyeIndex)
#else
#define DP_HAND_EYE 0u
#endif

float4 _DPHandA[4];      // capsule start (world xyz), w = radius
float4 _DPHandB[4];      // capsule end (world xyz)
float _DPHandCount;      // 0..4 capsules valid this frame
float _DPHandsOn;        // global kill switch (MagicOnTop.Hands); 0 until C# sets it

#if !defined(META_DEPTH_ENVIRONMENT_OCCLUSION_INCLUDED)
Texture2DArray<float> _EnvironmentDepthTexture;
SamplerState sampler_EnvironmentDepthTexture;
float4x4 _EnvironmentDepthReprojectionMatrices[2];
float4 _EnvironmentDepthZBufferParams;

float DPEnvDepthLinear(float2 uv, uint slice)
{
    const float ndc = _EnvironmentDepthTexture.SampleLevel(sampler_EnvironmentDepthTexture, float3(uv, slice), 0) * 2.0 - 1.0;
    if (ndc >= 1.0) return 10000.0;
    return (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
}
#else
float DPEnvDepthLinear(float2 uv, uint slice) { return SampleEnvironmentDepthLinear(uv); }
#endif

// A reading at or under this is the sensor failing (glare, a blocked camera; 0 reads as the 0.1 m near plane), never a hand.
#define DP_HAND_MIN_DEPTH 0.15
// Slack around a capsule for depth noise and a hand that moved since the depth frame.
#define DP_HAND_DEPTH_SLACK 0.05

float DPPointSegDistSq(float3 p, float3 a, float3 b)
{
    const float3 ab = b - a;
    const float len = dot(ab, ab);
    const float t = len > 1e-8 ? saturate(dot(p - a, ab) / len) : 0.0;
    const float3 d = p - (a + ab * t);
    return dot(d, d);
}

// Squared distance between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision Detection 5.1.9).
float DPSegSegDistSq(float3 p1, float3 q1, float3 p2, float3 q2)
{
    const float3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
    const float a = dot(d1, d1), e = dot(d2, d2), f = dot(d2, r);
    float s, t;
    if (a <= 1e-8 && e <= 1e-8) return dot(r, r);
    if (a <= 1e-8) { s = 0.0; t = saturate(f / e); }
    else
    {
        const float c = dot(d1, r);
        if (e <= 1e-8) { t = 0.0; s = saturate(-c / a); }
        else
        {
            const float b = dot(d1, d2), den = a * e - b * b;
            s = den > 1e-8 ? saturate((b * f - c * e) / den) : 0.0;
            t = (b * s + f) / e;
            if (t < 0.0) { t = 0.0; s = saturate(-c / a); }
            else if (t > 1.0) { t = 1.0; s = saturate((b - c) / a); }
        }
    }
    const float3 d = (p1 + d1 * s) - (p2 + d2 * t);
    return dot(d, d);
}

// 1 = the panel shows here; 0 = the player's real hand is in front of it.
float DPHandVisibility(float3 posWorld, float bias)
{
    if (_DPHandsOn < 0.5 || _DPHandCount < 0.5) return 1.0;
    const float3 eye = _WorldSpaceCameraPos.xyz;
    bool gate = false;
    [unroll] for (int i = 0; i < 4; i++)
    {
        if (i < (int)_DPHandCount && DPSegSegDistSq(eye, posWorld, _DPHandA[i].xyz, _DPHandB[i].xyz) <= _DPHandA[i].w * _DPHandA[i].w)
            gate = true;
    }
    if (!gate) return 1.0;
    const uint slice = DP_HAND_EYE;
    const float4 ds = mul(_EnvironmentDepthReprojectionMatrices[slice], float4(posWorld, 1.0));
    const float2 uv = (ds.xy / ds.w + 1.0) * 0.5;
    const float panelDepth = (1.0 / ((ds.z / ds.w) + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
    const float realDepth = DPEnvDepthLinear(uv, slice);
    if (realDepth > panelDepth - bias * panelDepth || realDepth <= DP_HAND_MIN_DEPTH) return 1.0;
    // Nov 6 2026 (a straight band cut the "GET READY" card): the capsule only says the RAY passes near a hand, and a hand
    // near the eye covers a wide swath of rays. The real surface itself must be in the capsule, or the floor, a wall or a
    // bad reading anywhere along that swath counted as the hand. Depth scales along the ray.
    const float3 surface = eye + (posWorld - eye) * (realDepth / max(panelDepth, 1e-4));
    [unroll] for (int j = 0; j < 4; j++)
    {
        const float reach = _DPHandA[j].w + DP_HAND_DEPTH_SLACK;
        if (j < (int)_DPHandCount && DPPointSegDistSq(surface, _DPHandA[j].xyz, _DPHandB[j].xyz) <= reach * reach) return 0.0;
    }
    return 1.0;
}

#else
float DPHandVisibility(float3 posWorld, float bias) { return 1.0; }
#endif

#endif
