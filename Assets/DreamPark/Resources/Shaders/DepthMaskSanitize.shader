/*
 * Meta's "Meta/EnvironmentDepth/DepthMask" (com.meta.xr.sdk.core 205), copied verbatim
 * except for ONE line in pass 1. DepthMaskCeiling swaps EnvironmentDepthManager's mask
 * material onto this shader at runtime; it lives in Resources so Shader.Find always
 * resolves it in a build.
 *
 * WHY (Oct 13 2026, outdoor playtest + DepthMaskAudit log). Where the depth cameras can
 * measure nothing — open sky, sun glare — they report depth 0, which linearises to the
 * near plane (0.10 m). Meta's occlusion only treats 1.0 as "no data" (it returns 10000 m for
 * it) and reads 0 as a surface 10 cm from your eyes, so everything behind it vanishes: 22 of
 * the 36 overhead occlusions outdoors were exactly this. No mask can exempt it (a mask only
 * exempts real depth FARTHER than itself). Pass 1 now also turns real depth nearer than
 * _InvalidDepthMeters into 1.0, before the soft-occlusion preprocess and every occlusion
 * shader read it. Hands are never measured that close, so they keep occluding.
 *
 * SKY FILTER (Oct 13 2026, second outdoor run). The rest of the sky flicker is phantom
 * surfaces at 0.2-0.6 m that come and go frame to frame — the same distances as a raised
 * hand, so no threshold separates them. What does: a hand persists, the phantoms don't. In
 * texels whose ray points above the horizon (per pixel, ramped in from _SkyStartDeg to
 * _SkyFullDeg so there is no seam), real depth only counts if it was there in the previous
 * frames too: depth = farthest of (now, 1 frame ago, 2 frames ago), the history texels
 * written by pass 2. A far reading counts at once, so nothing trails; a hand sweeping INTO
 * sky content starts occluding up to _SkyHistoryCount depth frames late. Below the horizon
 * nothing changes.
 *
 * HAND GATE (same day, third outdoor run). Some sky phantoms hold still for seconds, so no
 * time filter can drop them. Aidan: the ceiling and the sky never need to occlude anything.
 * So above _SkyFullDeg real depth occludes ONLY where its point lies inside a rough capsule
 * around a tracked hand (wrist->fingertips) or forearm stub — _SkyHand* from DepthMaskCeiling.
 * The occlusion itself is still Meta depth (the real hand silhouette); the tracked pose only
 * says where depth may count. No hand tracked = nothing occludes up there.
 *
 * SAFETY + GROUNDED (Oct 13 2026, playtest 8 — "the horizon is cutting off the gameplay").
 * Below the gate the same phantom appears: open lawn / bay read ~1.2 m away and cut every
 * structure near the horizon. A phantom FLOATS: directly below it in the depth image the
 * ground reads much farther. Real people and objects are GROUNDED: down their column the
 * depth stays at their distance until the ground at their feet. So, below the gate:
 *   - floating surfaces beyond _SafeNearKeep stop occluding;
 *   - grounded, non-floor surfaces within _SafeRadius (horizontal) PUNCH THROUGH all virtual
 *     content (written as _SafePunchMeters): Aidan, safety over accuracy — people and things
 *     you could trip on must show through the game even when it is "in front";
 *   - floor (surface facing up, or within _SafeFloorClearance of the floor) never punches;
 *   - tracked hands keep their true depth everywhere.
 * If the column runs out of the image before proving anything, the surface counts as grounded
 * (when unsure, keep the obstacle).
 *
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

Shader "DreamPark/EnvironmentDepth/DepthMaskSanitize"
{
    SubShader
    {
        Pass
        {
            Name "Pass 0" // write mask's depth to the texture
            ColorMask R
            ZWrite True
            LOD 200

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            float4x4 _DepthMask_MVP_Matrices[2];
            float4 _EnvironmentDepthZBufferParams;
            float _MaskBias;

            struct appdata
            {
                float4 vertex : POSITION;
                uint instanceId : SV_InstanceID;
            };

            struct v2f
            {
                float4 positionCS : SV_POSITION;
                uint depthSlice : SV_RenderTargetArrayIndex;
            };

            v2f vert(const appdata i)
            {
                v2f o;
                o.positionCS = mul(_DepthMask_MVP_Matrices[i.instanceId], float4(i.vertex.xyz, 1.0f));
                o.depthSlice = i.instanceId;
                return o;
            }

            float ToLinearDepth(const float nonLinearDepth)
            {
                const float ndcDepth = nonLinearDepth * 2.0f - 1.0f;
                return 1.0f / (ndcDepth + _EnvironmentDepthZBufferParams.y) * _EnvironmentDepthZBufferParams.x;
            }

            float ToNonLinearDepth(const float linearDepth)
            {
                const float ndcDepth = _EnvironmentDepthZBufferParams.x / linearDepth - _EnvironmentDepthZBufferParams.y;
                return ndcDepth * 0.5f + 0.5f;
            }

            float4 frag(const v2f i) : SV_Target
            {
                float depth = i.positionCS.z;
                #if defined(UNITY_REVERSED_Z)
                depth = 1.0f - depth;
                #endif
                const float linearDepthWithBias = ToLinearDepth(depth) * (1.0f - _MaskBias);
                return float4(ToNonLinearDepth(linearDepthWithBias), 0, 0, 0);
            }
            ENDCG
        }
        Pass
        {
            Name "Pass 1" // combine the mask's depth from previous pass with the envDepthTexture
            ColorMask R
            ZWrite Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            Texture2DArray_float _EnvironmentDepthTexture;
            SamplerState sampler_EnvironmentDepthTexture;

            Texture2DArray_float _MaskTexture;
            SamplerState sampler_MaskTexture;

            float4 _EnvironmentDepthZBufferParams;
            float _InvalidDepthMeters;

            // Sky filter (set by DepthMaskCeiling). History = sanitized raw depth of the
            // previous depth frames, same layout as _EnvironmentDepthTexture.
            Texture2DArray_float _SkyHistory1;
            Texture2DArray_float _SkyHistory2;
            SamplerState sky_point_clamp_sampler;
            float4x4 _SkyInvReprojection[2];   // depth clip -> world, per eye
            float _SkyFilterOn;
            float _SkyStartDeg;
            float _SkyFullDeg;
            float _SkyHistoryCount;            // 0..2 history frames available and wanted
            float4 _SkyHandA[4];               // capsule start (world xyz), w = radius
            float4 _SkyHandB[4];               // capsule end (world xyz)
            float _SkyHandCount;               // 0..4 capsules valid this frame

            // Safety + grounded classification (set by DepthMaskCeiling).
            float4x4 _EnvironmentDepthReprojectionMatrices[2]; // world -> depth clip (Meta global)
            float4 _EnvironmentDepthTexture_TexelSize;
            float _SafeOn;
            float4 _SafeEye;                   // world eye position
            float _SafeFloorY;                 // world floor height
            float _SafeRadius;                 // m, horizontal: grounded obstacles punch through
            float _SafeNearKeep;               // m: nearer than this, depth is always kept
            float _SafeFloorClearance;         // m above the floor that still counts as floor
            float _SafeFloorNormalY;           // |normal.y| above this = floor-like surface
            float _SafePunchMeters;            // depth written for punch-through pixels

            struct Attributes
            {
                uint vertexId : SV_VertexID;
                uint instanceId : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                uint depthSlice : SV_RenderTargetArrayIndex;
            };

            Varyings vert(const Attributes input)
            {
                Varyings output;

                // see GetFullScreenTriangleTexCoord() and GetFullScreenTriangleVertexPosition() in Common.hlsl
                const uint vertexID = input.vertexId;
                const float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                output.uv = float2(uv.x, 1.0 - uv.y);
                output.positionCS = float4(uv * 2.0 - 1.0, 1.0, 1.0);

                output.depthSlice = input.instanceId;
                return output;
            }

            // DreamPark: real depth at the sensor's near plane is a failed measurement.
            bool IsInvalidDepth(const float envDepth)
            {
                // Until the manager has published its z-buffer params (first frame), only
                // exactly-zero depth counts.
                if (_EnvironmentDepthZBufferParams.x == 0.0f) return envDepth <= 0.0001f;
                const float ndc = envDepth * 2.0f - 1.0f;
                const float linearDepth = 1.0f / (ndc + _EnvironmentDepthZBufferParams.y) * _EnvironmentDepthZBufferParams.x;
                return linearDepth <= _InvalidDepthMeters;
            }

            // Degrees above the horizontal of the ray through this depth texel. The occlusion
            // shaders sample uv = (ndc + 1) / 2, so ndc = uv * 2 - 1 here.
            float RayElevationDeg(const float2 uv, const uint slice)
            {
                const float2 ndc = uv * 2.0f - 1.0f;
                const float4 a = mul(_SkyInvReprojection[slice], float4(ndc, -1.0f, 1.0f));
                const float4 b = mul(_SkyInvReprojection[slice], float4(ndc, 0.0f, 1.0f));
                const float3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
                return degrees(asin(clamp(dir.y, -1.0f, 1.0f)));
            }

            // Is the real surface this texel measured inside a tracked-hand capsule?
            bool InsideHand(const float2 uv, const uint slice, const float envDepth)
            {
                const float4 h = mul(_SkyInvReprojection[slice], float4(uv * 2.0f - 1.0f, envDepth * 2.0f - 1.0f, 1.0f));
                const float3 p = h.xyz / h.w;
                for (int i = 0; i < 4; i++)
                {
                    if (i >= (int)_SkyHandCount) break;
                    const float3 a = _SkyHandA[i].xyz;
                    const float3 ab = _SkyHandB[i].xyz - a;
                    const float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-6f));
                    if (distance(p, a + ab * t) <= _SkyHandA[i].w) return true;
                }
                return false;
            }

            float3 WorldPoint(const float2 uv, const uint slice, const float d)
            {
                const float4 h = mul(_SkyInvReprojection[slice], float4(uv * 2.0f - 1.0f, d * 2.0f - 1.0f, 1.0f));
                return h.xyz / h.w;
            }

            float SampleSanitized(const float2 uv, const uint slice)
            {
                const float d = _EnvironmentDepthTexture.SampleLevel(sky_point_clamp_sampler, float3(uv, slice), 0);
                return IsInvalidDepth(d) ? 1.0f : d;
            }

            float2 ProjectUV(const float3 p, const uint slice, out bool ok)
            {
                const float4 c = mul(_EnvironmentDepthReprojectionMatrices[slice], float4(p, 1.0f));
                ok = c.w > 1e-4f;
                return (c.xy / max(c.w, 1e-4f) + 1.0f) * 0.5f;
            }

            // Surface normal from p and two neighbours at offsets a, b; true if it faces up.
            bool FacesUp(const float2 uv, const uint slice, const float3 p, const float2 a, const float2 b)
            {
                const float da = SampleSanitized(uv + a, slice);
                const float db = SampleSanitized(uv + b, slice);
                if (da >= 1.0f || db >= 1.0f) return false;
                const float3 n = cross(WorldPoint(uv + a, slice, da) - p, WorldPoint(uv + b, slice, db) - p);
                const float len = length(n);
                return len > 1e-8f && abs(n.y / len) >= _SafeFloorNormalY;
            }

            // Floor = near the floor height, or a surface facing up (lawn, slopes). Tried in
            // four quadrants: next to a person or a phantom one neighbour pair lands on the
            // other surface, but at least one stays on the ground.
            bool IsFloorLike(const float2 uv, const uint slice, const float3 p)
            {
                if (p.y <= _SafeFloorY + _SafeFloorClearance) return true;
                const float2 t = _EnvironmentDepthTexture_TexelSize.xy * 2.0f;
                return FacesUp(uv, slice, p, float2(t.x, 0), float2(0, t.y)) ||
                       FacesUp(uv, slice, p, float2(-t.x, 0), float2(0, -t.y)) ||
                       FacesUp(uv, slice, p, float2(t.x, 0), float2(0, -t.y)) ||
                       FacesUp(uv, slice, p, float2(-t.x, 0), float2(0, t.y));
            }

            // Walk down the image column under p (world-down projected into the depth view).
            // Floating = we meet ground clearly FARTHER than p before reaching p's own base.
            bool IsFloating(const float2 uv, const uint slice, const float3 p, const float dist)
            {
                bool ok;
                const float2 below = ProjectUV(p - float3(0, 0.3f, 0), slice, ok);
                float2 dir = ok ? below - uv : float2(0, -1);
                dir = length(dir) > 1e-5f ? normalize(dir) : float2(0, -1);
                const float tol = 0.3f + 0.25f * dist;
                const float horiz = length(p.xz - _SafeEye.xz);
                [loop] for (int i = 1; i <= 16; i++)
                {
                    const float2 q = uv + dir * (0.02f * i);
                    if (any(q < 0.0f) || any(q > 1.0f)) return false;      // ran out of view: keep
                    const float dq = SampleSanitized(q, slice);
                    if (dq >= 1.0f) continue;                               // no data: no evidence
                    const float3 pq = WorldPoint(q, slice, dq);
                    const float lq = distance(pq, _SafeEye.xyz);
                    if (lq > dist + tol) return true;                       // farther ground below: floating
                    if (pq.y <= _SafeFloorY + _SafeFloorClearance &&
                        abs(length(pq.xz - _SafeEye.xz) - horiz) < 0.3f + 0.1f * dist)
                        return false;                                       // reached its own base
                }
                return false;
            }

            float ToNonLinear(const float linearDepth)
            {
                const float ndcDepth = _EnvironmentDepthZBufferParams.x / linearDepth - _EnvironmentDepthZBufferParams.y;
                return ndcDepth * 0.5f + 0.5f;
            }

            float4 frag(const Varyings input) : SV_Target
            {
                float3 uv = float3(input.uv, input.depthSlice);
                const float maskDepth = _MaskTexture.Sample(sampler_MaskTexture, uv);
                float envDepth = _EnvironmentDepthTexture.Sample(sampler_EnvironmentDepthTexture, uv);
                if (IsInvalidDepth(envDepth)) envDepth = 1.0f;

                const float elevation = _SkyFilterOn > 0.5f ? RayElevationDeg(input.uv, input.depthSlice) : -90.0f;
                if (elevation >= _SkyFullDeg)
                {
                    // Looking up: the ceiling and sky never occlude. Only real depth at a
                    // tracked hand does — no hand-time delay, so no history here.
                    if (envDepth < 1.0f && !InsideHand(input.uv, input.depthSlice, envDepth)) envDepth = 1.0f;
                }
                else if (_SkyFilterOn > 0.5f && _SkyHistoryCount > 0.5f)
                {
                    // 0 below _SkyStartDeg, 1 above _SkyFullDeg. The ramp lengthens the memory
                    // (0 -> 1 -> 2 frames) rather than blending depths, which would invent
                    // surfaces at in-between distances.
                    const float w = saturate((elevation - _SkyStartDeg) /
                                             max(_SkyFullDeg - _SkyStartDeg, 0.001f));
                    const float frames = w * _SkyHistoryCount;
                    if (frames >= 0.5f) envDepth = max(envDepth, _SkyHistory1.SampleLevel(sky_point_clamp_sampler, uv, 0));
                    if (frames >= 1.5f) envDepth = max(envDepth, _SkyHistory2.SampleLevel(sky_point_clamp_sampler, uv, 0));
                }

                if (_SafeOn > 0.5f && _SkyFilterOn > 0.5f && elevation < _SkyFullDeg && envDepth < 1.0f &&
                    _EnvironmentDepthZBufferParams.x != 0.0f)
                {
                    const float3 p = WorldPoint(input.uv, input.depthSlice, envDepth);
                    const float dist = distance(p, _SafeEye.xyz);
                    if (dist > _SafeNearKeep && !InsideHand(input.uv, input.depthSlice, envDepth))
                    {
                        if (IsFloating(input.uv, input.depthSlice, p, dist))
                            envDepth = 1.0f;                                    // phantom: never occludes
                        else if (length(p.xz - _SafeEye.xz) <= _SafeRadius && !IsFloorLike(input.uv, input.depthSlice, p))
                            envDepth = min(envDepth, ToNonLinear(_SafePunchMeters)); // obstacle: show through
                    }
                }

                const float depth = (maskDepth < envDepth) ? 1.0f : envDepth;
                return float4(depth, 0, 0, 0);
            }
            ENDCG
        }
        Pass
        {
            Name "Pass 2" // DreamPark: sanitized copy of raw depth into a sky-filter history slot
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            Texture2DArray_float _SkySource;
            SamplerState sky_point_clamp_sampler;
            float4 _EnvironmentDepthZBufferParams;
            float _InvalidDepthMeters;

            struct Attributes
            {
                uint vertexId : SV_VertexID;
                uint instanceId : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                uint depthSlice : SV_RenderTargetArrayIndex;
            };

            Varyings vert(const Attributes input)
            {
                Varyings output;
                const uint vertexID = input.vertexId;
                const float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                output.uv = float2(uv.x, 1.0 - uv.y);
                output.positionCS = float4(uv * 2.0 - 1.0, 1.0, 1.0);
                output.depthSlice = input.instanceId;
                return output;
            }

            float4 frag(const Varyings input) : SV_Target
            {
                const float d = _SkySource.SampleLevel(sky_point_clamp_sampler, float3(input.uv, input.depthSlice), 0);
                bool invalid;
                if (_EnvironmentDepthZBufferParams.x == 0.0f) invalid = d <= 0.0001f;
                else
                {
                    const float linearDepth = 1.0f / ((d * 2.0f - 1.0f) + _EnvironmentDepthZBufferParams.y) * _EnvironmentDepthZBufferParams.x;
                    invalid = linearDepth <= _InvalidDepthMeters;
                }
                return float4(invalid ? 1.0f : d, 0, 0, 0);
            }
            ENDCG
        }
    }
}
