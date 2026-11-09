// Additive light that exists only at night: lamp bulbs and the pools of light on the ground under them. No texture, a radial falloff.
Shader "GreatHeistAuto/NightGlow"
{
    Properties { _Boost ("Boost", Float) = 1 }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend One One ZWrite Off Cull Off
        Offset -1, -1
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _GHA_Night;
            CBUFFER_START(UnityPerMaterial) float _Boost; CBUFFER_END
            struct A { float4 pos:POSITION; float2 uv:TEXCOORD0; half4 c:COLOR; };
            struct V { float4 cs:SV_POSITION; float2 uv:TEXCOORD0; half4 c:COLOR; };
            V vert(A i)
            {
                V o;
                // By day the quads collapse, so they cost nothing to fill.
                o.cs = _GHA_Night > 0.01 ? TransformObjectToHClip(i.pos.xyz) : float4(0,0,0,1);
                o.uv = i.uv; o.c = i.c;
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                float r = saturate(1 - length(i.uv - 0.5) * 2);
                return half4(i.c.rgb * r * r * _GHA_Night * _Boost, 1);
            }
            ENDHLSL
        }
    }
}
