# Integrating music-reactive background shaders

Neon Stage renders its Unity background through a small shader catalog. A new
background does **not** require changes to `StageVisualView`: add a shader below
`Assets/Resources`, register one JSON entry, build the Stage, and select the new
ID.

The catalog deliberately keeps shader integration compile-time and local. Unity
includes registered resource shaders in Linux and Android builds; Neon Stage
does not download or execute shader code from the server.

## Runtime architecture

```text
instrumental + optional vocals
        |
        v
StageAudioEngine.GetSpectrum()
        |
        v
smoothed energy / frequency bands / bass transient / song clock
        |
        v
StageVisualView -> shared material properties -> selected full-screen shader
        ^
        |
Resources/NeonStageBackgrounds.json
```

The implementation lives in:

- `Assets/NeonStage/Runtime/StageBackgroundShaderCatalog.cs` — selection,
  validation, loading, and fallback;
- `Assets/NeonStage/Runtime/StageVisualView.cs` — audio analysis and property
  updates;
- `Assets/Resources/NeonStageBackgrounds.json` — stable public catalog;
- `Assets/Resources/NeonBackdrop.shader` — default background;
- `Assets/Resources/BackgroundShaders/NeonPulseRings.shader` — complete example.

## Quick start

Paths below are relative to `src/Karaoke.Stage.Unity`.

1. Create `Assets/Resources/BackgroundShaders/MyBackground.shader`.
2. Give it a unique Unity shader name, for example
   `NeonStage/Background/MyBackground`.
3. Declare the music properties you want to consume. Unsupported properties
   are skipped safely by the Stage.
4. Add an entry to `Assets/Resources/NeonStageBackgrounds.json`:

   ```json
   {
     "id": "my-background",
     "displayName": "My Background",
     "resource": "BackgroundShaders/MyBackground"
   }
   ```

   `resource` is the path below any `Resources` folder, without the file
   extension. IDs are case-insensitive and should remain stable.
5. Test a Linux build without changing the default:

   ```bash
   ./scripts/linux/build-unity-stage-linux.sh
   ./scripts/linux/start-unity-stage.sh --background-shader my-background
   ```

   The environment-variable form is equivalent:

   ```bash
   NEONSTAGE_BACKGROUND_SHADER=my-background \
     ./scripts/linux/start-unity-stage.sh
   ```

6. To make the shader the default on Android and Linux, change `defaultId` in
   `NeonStageBackgrounds.json` and rebuild the Stage.

## Shared shader inputs

All values are updated every rendered frame. A shader may declare only the
properties it needs.

| Property | Range | Meaning |
| --- | --- | --- |
| `_Energy` | 0–1 | Smoothed energy across the complete spectrum |
| `_Bass` | 0–1 | Smoothed low-frequency energy |
| `_Mid` | 0–1 | Smoothed mid-frequency energy |
| `_Treble` | 0–1 | Smoothed high-frequency energy |
| `_Pulse` | 0–1 | Fast bass-onset transient with exponential decay |
| `_SongTime` | seconds | Authoritative Stage playback position; remains stable across pause and seek |
| `_IsPlaying` | 0 or 1 | Whether the Stage audio clock is currently running |

The bands are peak-normalized with a decaying local reference. This makes the
visual response useful for both quiet and loud masters without encoding a
fixed mastering level into each shader. Values decay smoothly when playback is
paused or stopped.

Use `_SongTime`, not `_Time`, for animation that must seek, pause, and resume
with the song. Unity's `_Time` is still useful for slow ambient motion that may
continue while the Stage is idle.

## Minimal compatible shader

```hlsl
Shader "NeonStage/Background/MyBackground"
{
 Properties
 {
  _Energy("Energy", Range(0,1)) = 0
  _Bass("Bass", Range(0,1)) = 0
  _Pulse("Pulse", Range(0,1)) = 0
  _SongTime("Song Time", Float) = 0
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
   struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
   struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
   float _Energy, _Bass, _Pulse, _SongTime;
   v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
   fixed4 frag(v2f i):SV_Target
   {
    float2 p=i.uv*2-1;
    p.x*=_ScreenParams.x/_ScreenParams.y;
    float ring=exp(-abs(length(p)-(.3+_Pulse*.12))*24);
    float glow=exp(-length(p)*(3-_Bass));
    float3 color=float3(.018,.004,.035);
    color+=glow*float3(.45,.02,.6)*(.2+_Energy);
    color+=ring*float3(.85,1,.03);
    return fixed4(color,1);
   }
   ENDCG
  }
 }
}
```

The built-in `NeonPulseRings.shader` is a more complete reference using every
frequency band, the transient value, playback state, and song time.

## Selection priority

The loader chooses the first non-empty value in this order:

1. `--background-shader ID` or `--background-shader=ID`;
2. `NEONSTAGE_BACKGROUND_SHADER=ID`;
3. Unity PlayerPrefs key `NeonStage.BackgroundShader`;
4. `defaultId` from `NeonStageBackgrounds.json`.

An unknown ID falls back to the catalog default. A missing shader asset falls
back to the legacy `NeonBackdrop` resource. The Unity player log records the
selected ID and actual shader name.

Android launchers normally cannot supply a process environment variable. For a
distributed APK, set the catalog default before building. A future settings UI
may write the documented PlayerPrefs key without changing this contract.

## Design rules for a karaoke background

- Keep the lyric area readable. Dark values and low-frequency structure belong
  behind the center of the screen; reserve bright peaks for short accents.
- Treat `_Pulse` as an accent, not continuous brightness. A full-screen flash
  on every kick is uncomfortable and obscures word progress.
- Prefer smooth functions to branches and cap iterative effects. The Stage must
  remain responsive on ARM Android karaoke hardware as well as desktop GPUs.
- Start with `#pragma target 3.0`. Use newer targets only after testing every
  supported Android device.
- Return an opaque color. The background is the bottom full-screen UI layer;
  transparency usually wastes fill rate without revealing useful content.
- Never sample cover art or external network textures implicitly. Backgrounds
  must remain deterministic, media-independent, and safe for release builds.
- Do not include third-party shader code unless its license permits repository
  and binary redistribution. Record required attribution in
  `THIRD_PARTY_NOTICES.md` and `ACKNOWLEDGEMENTS.md`.

## Validation checklist

1. Open the Unity project and confirm that the shader has no compile errors.
2. Build Linux with `./scripts/linux/build-unity-stage-linux.sh`.
3. Launch using the new ID and inspect the player log for
   `Neon Stage background shader:`.
4. Verify idle, countdown, playback, pause, resume, and seek behavior.
5. Test a quiet song and a heavily mastered song; normalization should keep both
   reactive without permanent clipping.
6. Confirm that lyrics, QR code, next-song card, and controls remain readable.
7. Build and test the ARM32 Android APK when targeting the Ikarao Shell S2.
8. Run the repository media and credential guards before publishing.

## Troubleshooting

### The Stage uses Neon Grid instead of the requested shader

Check that the catalog ID matches, the resource path omits `.shader`, and the
file is below an `Assets/Resources` directory. Read the Unity player log for the
fallback warning.

### The screen is pink

Unity could load the asset, but the shader failed to compile or is unsupported
by the active graphics API. Inspect the Unity Editor console, keep the shader at
target 3.0, and avoid desktop-only instructions.

### The effect animates but does not follow music

Confirm that the property names match exactly, including the leading underscore.
Use `_SongTime` for transport-synchronized motion and `_Energy`, `_Bass`,
`_Mid`, `_Treble`, or `_Pulse` for audio response.

### The effect reacts only at song start

Do not retain a one-time material value. Read the declared properties directly
inside the fragment shader; `StageVisualView` refreshes them every frame. The
audio engine also supplies an RMS fallback on platforms whose streaming decoder
does not expose a reliable FFT continuously.
