// The one world shader of Great Heist Auto: URP PBR lighting, texture x vertex colour x tint.
// - vertex colour carries the per-object tint of everything merged into static city meshes
// - _TintColor + _SelectiveTint tint characters: low-saturation texels (clothes) take the tint, skin keeps its colour
// - dark texels can be glossier than light ones (_SmoothDark), which makes facade windows reflect the sky
Shader "GreatHeistAuto/TintLit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [Normal] _NormalMap ("Normal", 2D) = "bump" {}
        _NormalScale ("Normal scale", Range(0, 2)) = 1
        [MainColor] _Color ("Colour", Color) = (1,1,1,1)
        _TintColor ("Tint (runtime)", Color) = (1,1,1,1)
        _SelectiveTint ("Selective tint (0 all, 1 clothes only)", Range(0,1)) = 0
        _SmoothLight ("Smoothness of light texels", Range(0,1)) = 0.2
        _SmoothDark ("Smoothness of dark texels", Range(0,1)) = 0.2
        _Metallic ("Metallic", Range(0,1)) = 0
        _DepthBias ("Offset (decals)", Float) = 0
        _Fade ("Fade", Float) = 1
        _WetAmount ("Gets wet in rain", Range(0,1)) = 0
        [HDR] _GlowColor ("Window glow at night", Color) = (0,0,0,1)
        _GlowMask ("Window mask (R window, G random per window)", 2D) = "black" {}
        _GlowShare ("Share of windows lit", Range(0,1)) = 0.55
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
        TEXTURE2D(_GlowMask); SAMPLER(sampler_GlowMask);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _Color;
            half4 _TintColor;
            half _SelectiveTint;
            half _SmoothLight;
            half _SmoothDark;
            half _Metallic;
            half _NormalScale;
            float _DepthBias;
            half _WetAmount;
            half4 _GlowColor;
            half _GlowShare;
            half _Fade;
        CBUFFER_END
        float _GHA_Night, _GHA_Wet;
        // Fading: a fine dissolve, which needs no transparency, so the object keeps its depth and its shadow goes with it.
        // Leaves on cards: cut out along the picture's edge. In every pass, so shadows and depth have the leaves' shape too.
        void CutoutClip(float2 uv)
        {
            #if defined(_GHA_CUTOUT)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a - 0.45);
            #endif
        }
        void FadeClip(float2 pixel)
        {
            #if defined(_GHA_FADE)
                clip(_Fade - frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) - 0.001);
            #endif
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull [_Cull]
            Offset [_DepthBias], [_DepthBias]
            HLSLPROGRAM
            #pragma shader_feature_local _GHA_CUTOUT
            #pragma multi_compile_local _ _GHA_FADE
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct A { float4 pos:POSITION; float3 n:NORMAL; float4 t:TANGENT; float2 uv:TEXCOORD0; half4 c:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V
            {
                float4 cs:SV_POSITION; float2 uv:TEXCOORD0; float3 ws:TEXCOORD1; half3 n:TEXCOORD2; half4 t:TEXCOORD3; half4 c:COLOR; half3 sh:TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO
            };

            V vert(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_TRANSFER_INSTANCE_ID(i, o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(i.pos.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(i.n, i.t);
                o.cs = p.positionCS; o.ws = p.positionWS; o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                o.n = n.normalWS; o.t = half4(n.tangentWS, i.t.w * GetOddNegativeScale()); o.c = i.c;
                o.sh = SampleSH(n.normalWS);
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                FadeClip(i.cs.xy);
                CutoutClip(i.uv);
                UNITY_SETUP_INSTANCE_ID(i);
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half lum = dot(tex.rgb, half3(0.3, 0.59, 0.11));
                half mx = max(tex.r, max(tex.g, tex.b)), mn = min(tex.r, min(tex.g, tex.b));
                half sat = mx > 0.001 ? (mx - mn) / mx : 0;
                half keep = _SelectiveTint * saturate((sat - 0.16) * 6);   // saturated texels (skin) keep their colour
                half3 albedo = tex.rgb * _Color.rgb * i.c.rgb * lerp(_TintColor.rgb, half3(1,1,1), keep);

                // Rain: darker and much glossier.
                half wet = _GHA_Wet * _WetAmount;
                albedo *= lerp(1.0, 0.62, wet);

                float3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv), _NormalScale);
                half3 bitangent = i.t.w * cross(i.n, i.t.xyz);
                half3 nWS = NormalizeNormalPerPixel(TransformTangentToWorld(nTS, half3x3(i.t.xyz, bitangent, i.n)));

                InputData d = (InputData)0;
                d.positionWS = i.ws; d.positionCS = i.cs; d.normalWS = nWS;
                d.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.ws);
                d.shadowCoord = TransformWorldToShadowCoord(i.ws);
                d.fogCoord = InitializeInputDataFog(float4(i.ws, 1), 0);
                d.bakedGI = i.sh; d.shadowMask = half4(1,1,1,1);
                d.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.cs);

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo; s.alpha = 1; s.occlusion = 1; s.normalTS = nTS;
                s.metallic = _Metallic; s.specular = 0;
                s.smoothness = lerp(lerp(_SmoothDark, _SmoothLight, saturate(lum * 2.2)), 0.86, wet);
                half4 c = UniversalFragmentPBR(d, s);

                // Night: whole windows light up, a different set on each building (the seed is in the vertex alpha).
                if (_GlowColor.a > 0.001 && _GHA_Night > 0.001)
                {
                    half2 mask = SAMPLE_TEXTURE2D(_GlowMask, sampler_GlowMask, i.uv).rg;
                    float r = frac(mask.g * 7.31 + i.c.a * 3.77);
                    half on = step(r, _GlowShare);
                    half3 tone = lerp(half3(1.0, 0.83, 0.58), half3(0.78, 0.86, 1.0), step(0.86, frac(r * 13.0)));
                    // Lit from inside: what is painted in the window (blinds, shop displays) stays visible.
                    half3 inner = 0.14 + tex.rgb * 1.5;
                    half3 nearGlow = tone * inner * on * (0.5 + 0.75 * frac(r * 29.0));

                    // Like a mip level for the on/off pattern: once a window is only a few pixels across, single windows would
                    // flicker as noise, so they melt into the facade's dim average. Some buildings are darker overall.
                    float2 texels = i.uv * 1024.0;
                    float footprint = max(length(ddx(texels)), length(ddy(texels)));
                    half farness = smoothstep(5.0, 22.0, footprint);
                    half building = 0.35 + 0.65 * frac(i.c.a * 19.7);
                    half3 farGlow = half3(1.0, 0.86, 0.64) * _GlowShare * 0.5 * building;

                    c.rgb += _GlowColor.rgb * mask.r * _GHA_Night * lerp(nearGlow, farGlow, farness);
                }
                c.rgb = MixFog(c.rgb, d.fogCoord);
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull [_Cull]
            HLSLPROGRAM
            #pragma shader_feature_local _GHA_CUTOUT
            #pragma multi_compile_local _ _GHA_FADE
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection; float3 _LightPosition;
            struct A { float4 pos:POSITION; float3 n:NORMAL; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 cs:SV_POSITION; float2 uv:TEXCOORD0; };
            V vert(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i);
                float3 ws = TransformObjectToWorld(i.pos.xyz);
                float3 n = TransformObjectToWorldNormal(i.n);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 l = normalize(_LightPosition - ws);
                #else
                    float3 l = _LightDirection;
                #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(ws, n, l));
                o.cs = ApplyShadowClamping(cs);
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }
            half4 frag(V i) : SV_Target { FadeClip(i.cs.xy); CutoutClip(i.uv); return 0; }
            ENDHLSL
        }

        // Moving objects report their own motion, so a car or a body the camera follows is not blurred with the world behind it.
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode"="MotionVectors" }
            ColorMask RG
            Cull [_Cull]
            HLSLPROGRAM
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local_vertex _ADD_PRECOMPUTED_VELOCITY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ObjectMotionVectors.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R Cull [_Cull]
            Offset [_DepthBias], [_DepthBias]
            HLSLPROGRAM
            #pragma shader_feature_local _GHA_CUTOUT
            #pragma multi_compile_local _ _GHA_FADE
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct A { float4 pos:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 cs:SV_POSITION; float2 uv:TEXCOORD0; };
            V vert(A i) { V o; UNITY_SETUP_INSTANCE_ID(i); o.cs = TransformObjectToHClip(i.pos.xyz); o.uv = TRANSFORM_TEX(i.uv, _BaseMap); return o; }
            half frag(V i) : SV_Target { FadeClip(i.cs.xy); CutoutClip(i.uv); return i.cs.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On Cull [_Cull]
            Offset [_DepthBias], [_DepthBias]
            HLSLPROGRAM
            #pragma shader_feature_local _GHA_CUTOUT
            #pragma multi_compile_local _ _GHA_FADE
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct A { float4 pos:POSITION; float3 n:NORMAL; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 cs:SV_POSITION; half3 n:TEXCOORD0; float2 uv:TEXCOORD1; };
            V vert(A i) { V o; UNITY_SETUP_INSTANCE_ID(i); o.cs = TransformObjectToHClip(i.pos.xyz); o.uv = TRANSFORM_TEX(i.uv, _BaseMap); o.n = TransformObjectToWorldNormal(i.n); return o; }
            half4 frag(V i) : SV_Target { FadeClip(i.cs.xy); CutoutClip(i.uv); return half4(NormalizeNormalPerPixel(i.n), 0); }
            ENDHLSL
        }
    }
}
