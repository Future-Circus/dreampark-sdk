Shader "Custom/BrokenScreen"
{
 Properties {
  [HideInInspector] _MainTex("Base (RGB)", 2D) = "white" {}
  _DirectionMap("Direction (RG)", 2D) = "bump" {}
  _DiffuseTex("Diffuse Texture", 2D) = "white" {}
  _AlphaTex("Alpha Texture", 2D) = "white" {}
  _Refraction("Refraction", Range(0.0, 1.0)) = 0.0
  _VignetteRadius("Vignette Radius", Range(0.0, 1.0)) = 0.5
  _VignetteSoftness("Vignette Softness", Range(0.0, 1.0)) = 0.3
 }
 
 SubShader {

  Tags {
   "Queue"="Transparent"
   "RenderType"="Transparent"
   "IgnoreProjector"="True"
  }

  Blend SrcAlpha OneMinusSrcAlpha
  ZWrite Off
  Cull Off

  Pass {
   ZTest Always
   Fog { Mode off }
   
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma fragmentoption ARB_precision_hint_fastest

   #include "UnityCG.cginc"

   sampler2D _MainTex;
   float4  _MainTex_TexelSize;
   sampler2D _DirectionMap;
   sampler2D  _DiffuseTex;
   sampler2D _AlphaTex;
   fixed _Refraction, _Broken;
   float _VignetteRadius;
   float _VignetteSoftness;
   
   struct appdata
   {
    float4 vertex : POSITION;
    half2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
   };
    
   struct v2f
   {
    float4 pos : SV_POSITION;
    half2 uv : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
   };


   v2f vert(appdata v)
   {
    v2f o;

    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2f, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    
    o.pos = UnityObjectToClipPos(v.vertex);
    half2 uv = MultiplyUV(UNITY_MATRIX_TEXTURE0, v.texcoord);

    o.uv = uv;
    return o;
   } 
   
   fixed4 frag(v2f i) : COLOR
   {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half2 directionColor = UnpackNormal(tex2D(_DirectionMap, i.uv )).xy;
    directionColor *= -1;

    half3 screen = tex2D(_MainTex, i.uv + directionColor * _Refraction).rgb;

    half3 diffuseColor = tex2D(_DiffuseTex, i.uv);
    fixed3 finalColor = lerp(screen, screen + diffuseColor, _Refraction);

    // vignette 
    float2 centeredUV = i.uv - 0.5f;
    float dist = length(centeredUV);
    float vignette = smoothstep(_VignetteRadius, _VignetteRadius * (1.0 - _VignetteSoftness), dist);

    float alpha = tex2D(_AlphaTex, i.uv).r * vignette;

    return fixed4(finalColor, alpha);
   }
   
   ENDCG
  }
  
 } 
 FallBack Off
}
