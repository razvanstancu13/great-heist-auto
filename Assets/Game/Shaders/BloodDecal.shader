// Blood on surfaces: lit by the scene like wet paint (dark at night, in shade and in fog), glossy, soft-edged, drawn over the surface below.
Shader "GreatHeistAuto/BloodDecal"
{
    Properties
    {
        [MainTexture] _BaseMap ("Shape", 2D) = "white" {}
        [MainColor] _Color ("Colour", Color) = (1,1,1,1)
        _Smoothness ("Smoothness", Range(0,1)) = 0.78
        _Fade ("Fade", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-50" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off Cull Off
            Offset -2, -2
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _Color;
                half _Smoothness;
                half _Fade;
            CBUFFER_END
            struct A { float4 pos:POSITION; float3 n:NORMAL; float2 uv:TEXCOORD0; };
            struct V { float4 cs:SV_POSITION; float2 uv:TEXCOORD0; float3 ws:TEXCOORD1; half3 n:TEXCOORD2; half3 sh:TEXCOORD3; };
            V vert(A i)
            {
                V o;
                VertexPositionInputs p = GetVertexPositionInputs(i.pos.xyz);
                o.cs = p.positionCS; o.ws = p.positionWS; o.uv = i.uv;
                o.n = TransformObjectToWorldNormal(i.n);
                o.sh = SampleSH(o.n);
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _Color;
                tex.a *= _Fade;
                InputData d = (InputData)0;
                d.positionWS = i.ws; d.positionCS = i.cs; d.normalWS = normalize(i.n);
                d.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.ws);
                d.shadowCoord = TransformWorldToShadowCoord(i.ws);
                d.fogCoord = InitializeInputDataFog(float4(i.ws, 1), 0);
                d.bakedGI = i.sh; d.shadowMask = half4(1,1,1,1);
                d.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.cs);
                SurfaceData s = (SurfaceData)0;
                s.albedo = tex.rgb; s.alpha = tex.a; s.occlusion = 1; s.metallic = 0;
                s.smoothness = _Smoothness;
                s.normalTS = float3(0,0,1);
                half4 c = UniversalFragmentPBR(d, s);
                c.rgb = MixFog(c.rgb, d.fogCoord);
                return half4(c.rgb, tex.a);
            }
            ENDHLSL
        }
    }
}
