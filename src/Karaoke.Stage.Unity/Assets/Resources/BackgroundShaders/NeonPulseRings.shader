Shader "NeonStage/Background/PulseRings"
{
 Properties
 {
  _Energy("Energy", Range(0,1)) = 0
  _Bass("Bass", Range(0,1)) = 0
  _Mid("Mid", Range(0,1)) = 0
  _Treble("Treble", Range(0,1)) = 0
  _Pulse("Pulse", Range(0,1)) = 0
  _SongTime("Song Time", Float) = 0
  _IsPlaying("Is Playing", Range(0,1)) = 0
 }
 SubShader
 {
  Tags { "Queue"="Background" "RenderType"="Opaque" }
  Cull Off ZWrite Off ZTest Always
  Pass
  {
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"

   struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
   struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
   float _Energy, _Bass, _Mid, _Treble, _Pulse, _SongTime, _IsPlaying;

   v2f vert(appdata input)
   {
    v2f output;
    output.vertex = UnityObjectToClipPos(input.vertex);
    output.uv = input.uv;
    return output;
   }

   fixed4 frag(v2f input) : SV_Target
   {
    float2 point = input.uv * 2.0 - 1.0;
    point.x *= _ScreenParams.x / _ScreenParams.y;
    float radius = length(point);
    float angle = atan2(point.y, point.x);
    float clock = lerp(_Time.y * .08, _SongTime * .11, _IsPlaying);
    float waves = sin(radius * (24.0 + _Mid * 8.0) - clock * 9.0 + sin(angle * 5.0) * .6);
    float rings = smoothstep(.72, .96, waves) * exp(-radius * (1.3 - _Energy * .35));
    float spokes = pow(saturate(cos(angle * 10.0 + clock * 2.0) * .5 + .5), 12.0) * _Treble;
    float core = exp(-radius * (5.0 - _Bass * 2.2));
    float pulseRing = exp(-abs(radius - (.22 + _Pulse * .18)) * 34.0) * _Pulse;

    float3 color = float3(.018, .004, .035);
    color += rings * float3(.72, .025, .48) * (.18 + _Energy);
    color += spokes * float3(.05, .32, .78) * .34;
    color += core * float3(.36, .03, .52) * (.3 + _Bass);
    color += pulseRing * float3(.82, 1.0, .03);
    color *= smoothstep(1.45, .24, radius);
    return fixed4(color, 1.0);
   }
   ENDCG
  }
 }
}
