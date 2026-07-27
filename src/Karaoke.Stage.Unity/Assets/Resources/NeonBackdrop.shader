Shader "NeonStage/AudioBackdrop"
{
 Properties { _Energy("Energy",Range(0,1))=0 _Bass("Bass",Range(0,1))=0 _Pulse("Pulse",Range(0,1))=0 }
 SubShader { Tags { "Queue"="Background" } Cull Off ZWrite Off ZTest Always
  Pass { CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
   struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
   float _Energy,_Bass,_Pulse;
   v2f vert(appdata v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;return o;}
   fixed4 frag(v2f i):SV_Target {
    float2 p=i.uv*2-1;p.x*=_ScreenParams.x/_ScreenParams.y;
    p.x+=sin(p.y*7+_Time.y*(.7+_Bass*1.8))*_Energy*.035;
    float horizon=abs(p.y+.42);
    float gx=abs(frac((p.x/max(.08,-p.y+.7))*7+.5)-.5);
    float gy=abs(frac((1/max(.08,-p.y+.72))*2.2-_Time.y*(.08+_Energy*.04))-.5);
    float grid=smoothstep(.075+_Bass*.025,.008,min(gx,gy))*smoothstep(.15,-.3,p.y);
    float glow=exp(-horizon*(10-_Bass*4.5));
    float radial=exp(-length(p-float2(0,-.25))*(2.3-_Energy));
    float3 col=float3(.025,.006,.045)+float3(.28,.035,.42)*(glow+radial*.3);
    col+=float3(.75,.02,.38)*grid*(.18+_Energy*.9);
    col+=float3(.55,.72,.01)*glow*(.035+_Pulse*.48);
    col+=float3(.04,.35,.72)*radial*_Bass*.22;
    col*=smoothstep(1.35,.3,length(p*float2(.72,1)));
    return fixed4(col,1);
   }
  ENDCG }
 }
}
