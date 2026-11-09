// Skybox for the day-night cycle: three LAGP panoramas mixed by the sun's height, a real sun disc and moon, fog at the horizon.
Shader "GreatHeistAuto/Sky"
{
    Properties
    {
        _DayTex ("Day panorama", 2D) = "white" {}
        _DuskTex ("Dusk panorama", 2D) = "white" {}
        _NightTex ("Night panorama", 2D) = "black" {}
        _DayW ("Day", Range(0,1)) = 1
        _DuskW ("Dusk", Range(0,1)) = 0
        _NightW ("Night", Range(0,1)) = 0
        _Overcast ("Overcast", Range(0,1)) = 0
        _IsDay ("Is day", Float) = 1
        _SunDir ("Direction to sun or moon", Vector) = (0,1,0,0)
        _FogCol ("Fog colour", Color) = (.7,.7,.75,1)
        _FogAmount ("Fog amount", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_DayTex); SAMPLER(sampler_DayTex);
            TEXTURE2D(_DuskTex); SAMPLER(sampler_DuskTex);
            TEXTURE2D(_NightTex); SAMPLER(sampler_NightTex);
            CBUFFER_START(UnityPerMaterial)
                float _DayW, _DuskW, _NightW, _Overcast, _IsDay, _FogAmount;
                float4 _SunDir, _FogCol;
            CBUFFER_END
            struct A { float4 pos:POSITION; };
            struct V { float4 cs:SV_POSITION; float3 dir:TEXCOORD0; };
            V vert(A i) { V o; o.cs = TransformObjectToHClip(i.pos.xyz); o.dir = i.pos.xyz; return o; }

            float4 frag(V i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float elev = asin(clamp(d.y, -1, 1)) / 1.5708;     // -1..1
                float az = atan2(d.x, d.z) / 6.28318 + 0.5;
                // The panoramas have their horizon low in the picture: the visible half of the sky maps to the part above it.
                float2 uv = float2(az, 0.2 + 0.8 * saturate(elev));
                // Both ends of az meet behind the seam; use the gradient of a continuous function to avoid a mip line there.
                float2 dx = ddx(float2(frac(az + 0.25), uv.y)), dy = ddy(float2(frac(az + 0.25), uv.y));
                float3 c = SAMPLE_TEXTURE2D_GRAD(_DayTex, sampler_DayTex, uv, dx, dy).rgb * _DayW * 0.85
                         + SAMPLE_TEXTURE2D_GRAD(_DuskTex, sampler_DuskTex, uv, dx, dy).rgb * _DuskW * 0.9
                         + SAMPLE_TEXTURE2D_GRAD(_NightTex, sampler_NightTex, uv, dx, dy).rgb * _NightW * 0.9;
                // The generated panoramas have a soft line down their middle: cross-fade to another part of the sky there.
                float seam = smoothstep(0.075, 0.03, abs(az - 0.5));
                if (seam > 0.001)
                {
                    float2 uv2 = uv + float2(0.27, 0.0);
                    float3 c2 = SAMPLE_TEXTURE2D_GRAD(_DayTex, sampler_DayTex, uv2, dx, dy).rgb * _DayW * 0.85
                              + SAMPLE_TEXTURE2D_GRAD(_DuskTex, sampler_DuskTex, uv2, dx, dy).rgb * _DuskW * 0.9
                              + SAMPLE_TEXTURE2D_GRAD(_NightTex, sampler_NightTex, uv2, dx, dy).rgb * _NightW * 0.9;
                    c = lerp(c, c2, seam);
                }

                // Grey cloud deck when overcast.
                float grey = dot(c, float3(.3,.59,.11));
                float3 deck = (_DayW * float3(.62,.65,.7) + _DuskW * float3(.42,.34,.34) + _NightW * float3(.03,.035,.05)) * (0.75 + grey * 0.5);
                c = lerp(c, deck, _Overcast * 0.92);

                // The disc itself, and its glow.
                float3 s = normalize(_SunDir.xyz);
                float cosA = dot(d, s);
                float clear = 1 - _Overcast * 0.92;
                if (_IsDay > 0.5)
                {
                    float3 col = lerp(float3(1.6,.7,.3), float3(1.5,1.3,1.05), _DayW);
                    c += col * smoothstep(0.99945, 0.99975, cosA) * 22 * clear;
                    c += col * pow(saturate(cosA), 340) * 1.2 * clear;
                    c += col * pow(saturate(cosA), 18) * 0.22 * clear;
                }
                else
                {
                    float disc = smoothstep(0.99955, 0.9998, cosA);
                    c = lerp(c, float3(1.5,1.55,1.65), disc * clear);
                    c += float3(.5,.6,.8) * pow(saturate(cosA), 500) * 0.45 * clear;
                }

                // Haze on the horizon, and under it, nothing but fog colour.
                float haze = saturate(1 - saturate(elev) * lerp(5.0, 1.1, _FogAmount));
                c = lerp(c, _FogCol.rgb, haze * lerp(0.75, 1.0, _FogAmount));
                c = lerp(c, _FogCol.rgb, _FogAmount * 0.8);
                if (d.y < 0) c = _FogCol.rgb;
                return float4(c, 1);
            }
            ENDHLSL
        }
    }
}
