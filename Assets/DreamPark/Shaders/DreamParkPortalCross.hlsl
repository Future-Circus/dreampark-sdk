// DreamPark portal crossers: per-renderer opening clip (Nov 2026, sdk-crossers; Aidan: "Its actually so important objects
// come through portals smoothly that if we have a hollistic solution we should include it with the portal in the sdk").
//
// A crosser registered against ONE opening (PortalScene.AddCrosser / dp.portal_cross) is split at that opening's plane, per
// pixel: the room's cameras draw only the part on the room side, the portal render only the part on the scene side, mapped
// through the scene's play -> scene transform (an offset scene). PortalScene writes these through the crosser renderers'
// MaterialPropertyBlock, and only while one actually straddles the plane or is mapped. Every other renderer keeps the
// values' default 0 (they are globals nobody sets), so for them this is one uniform branch per vertex and per pixel.
//
//   _DPCross      x = 1: clip at _DPCrossPlane; y = 1: positions are mapped by _DPCrossMap (the portal render only)
//   _DPCrossPlane world-space plane of THIS render: a pixel stays where dot(xyz, positionWS) + w >= 0
//   _DPCrossMap   play space -> scene (world), used when _DPCross.y = 1
//
// Used by DreamPark-UniversalShader / DreamPark-Unlit (Shader Graph custom functions DPPortalCrossVertex in the vertex
// stage and DPPortalCrossClip on Alpha, so every pass clips) and DreamPark/Particles. Include after URP's Core.hlsl.
#ifndef DREAMPARK_PORTAL_CROSS_INCLUDED
#define DREAMPARK_PORTAL_CROSS_INCLUDED

float4 _DPCross;
float4 _DPCrossPlane;
float4x4 _DPCrossMap;

// world position mapped into the scene (the portal render of a mapped crosser); unchanged otherwise
float3 DPCrossMapPositionWS(float3 positionWS)
{
    [branch] if (_DPCross.y > 0.5) positionWS = mul(_DPCrossMap, float4(positionWS, 1.0)).xyz;
    return positionWS;
}

// discard this pixel when it is on the other side of the crosser's opening (no-op unless _DPCross.x = 1)
void DPCrossClipWS(float3 positionWS)
{
    [branch] if (_DPCross.x > 0.5) clip(dot(_DPCrossPlane.xyz, positionWS) + _DPCrossPlane.w);
}

// ── Shader Graph custom functions (precision Single) ────────────────────────────────────────────────────────────────────
// Vertex stage: object-space position / normal / tangent, mapped into the scene for a mapped crosser's portal render.
void DPPortalCrossVertex_float(float3 PositionOS, float3 NormalOS, float3 TangentOS,
                               out float3 Position, out float3 Normal, out float3 Tangent)
{
    Position = PositionOS;
    Normal = NormalOS;
    Tangent = TangentOS;
    [branch] if (_DPCross.y > 0.5)
    {
        float3 positionWS = mul(_DPCrossMap, float4(TransformObjectToWorld(PositionOS), 1.0)).xyz;
        Position = TransformWorldToObject(positionWS);
        float3 normalWS = mul((float3x3)_DPCrossMap, TransformObjectToWorldNormal(NormalOS));
        Normal = TransformWorldToObjectNormal(normalWS);
        float3 tangentWS = mul((float3x3)_DPCrossMap, TransformObjectToWorldDir(TangentOS));
        Tangent = TransformWorldToObjectDir(tangentWS);
    }
}

// Fragment stage, on the Alpha chain (so the shadow / depth passes clip too): Alpha passes through unchanged.
void DPPortalCrossClip_float(float3 PositionWS, float AlphaIn, out float AlphaOut)
{
    DPCrossClipWS(PositionWS);
    AlphaOut = AlphaIn;
}

void DPPortalCrossClip_half(float3 PositionWS, half AlphaIn, out half AlphaOut)
{
    DPCrossClipWS(PositionWS);
    AlphaOut = AlphaIn;
}

#endif
