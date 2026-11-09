// Head and tail lights of a driven car: vertex colour gives white or red, _Lit and _Brake come from Vehicle per car.
Shader "GreatHeistAuto/CarLamps"
{
    Properties { _Lit ("Lit", Float) = 0 _Brake ("Brake", Float) = 0 }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Offset -2, -2
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial) float _Lit, _Brake; CBUFFER_END
            struct A { float4 pos:POSITION; half4 c:COLOR; };
            struct V { float4 cs:SV_POSITION; half4 c:COLOR; };
            V vert(A i) { V o; o.cs = TransformObjectToHClip(i.pos.xyz); o.c = i.c; return o; }
            half4 frag(V i) : SV_Target
            {
                // Alpha 1 marks a head lamp, alpha 0 a tail lamp.
                float head = i.c.a;
                float power = head * (0.1 + _Lit * 5) + (1 - head) * (0.15 + _Lit * 1.6 + _Brake * 3.5);
                return half4(i.c.rgb * power, 1);
            }
            ENDHLSL
        }
    }
}
