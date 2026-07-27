# Thank you, open-source community

Neon Stage exists because many people chose to publish their work, explain it,
review contributions, maintain packages, and make difficult multimedia and
machine-learning problems approachable. We are sincerely grateful to every
maintainer and contributor involved.

## Development approach

A large part of Neon Stage has been created through AI-assisted “vibe coding.”
The product direction, requirements, testing, and acceptance decisions are
human-led, while substantial portions of implementation and documentation were
developed collaboratively with AI coding tools. This disclosure is part of our
commitment to being transparent about how the project is made.

Special thanks go to:

- [Avalonia](https://github.com/AvaloniaUI/Avalonia) and
  [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet) for the
  cross-platform editor foundation.
- [VideoLAN](https://www.videolan.org/),
  [VLC](https://code.videolan.org/videolan/vlc), and
  [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) for dependable
  multimedia playback.
- [QRCoder](https://github.com/Shane32/QRCoder),
  [TagLib#](https://github.com/mono/taglib-sharp),
  [SQLite](https://sqlite.org/), and
  [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) for the small,
  focused building blocks used throughout the application.
- [Skia](https://skia.org/), [SkiaSharp](https://github.com/mono/SkiaSharp),
  [HarfBuzz](https://harfbuzz.github.io/), and the
  [Inter typeface](https://github.com/rsms/inter) for rendering text and UI.
- [FFmpeg](https://ffmpeg.org/) for the audio-processing foundation used by
  the server and alignment workflows.
- [PyTorch](https://pytorch.org/),
  [Hugging Face Transformers](https://github.com/huggingface/transformers),
  [Qwen](https://huggingface.co/Qwen),
  [OpenAI Whisper](https://github.com/openai/whisper),
  [stable-ts](https://github.com/jianfch/stable-ts),
  [Silero VAD](https://github.com/snakers4/silero-vad),
  [EasyAligner](https://github.com/kb-labb/easyaligner), and
  [SOFA](https://github.com/qiuqiao/SOFA) for speech recognition and forced
  alignment research and tooling.
- [python-audio-separator](https://github.com/nomadkaraoke/python-audio-separator),
  [Ultimate Vocal Remover](https://github.com/Anjok07/ultimatevocalremovergui),
  and their model authors and contributors for making practical stem
  separation available to the community.
- [LRCLIB](https://lrclib.net/) and its community for an open lyrics service.
- [UltraStar Deluxe](https://github.com/UltraStar-Deluxe/USDX) and its
  community for documenting and maintaining a widely used timed karaoke text
  format. Neon Stage implements compatible TXT import independently and does
  not bundle UltraStar song collections.
- [Qobuz](https://www.qobuz.com/) for high-quality purchase formats and its
  integration ecosystem. The optional provider only uses download access
  explicitly authorized by Qobuz for the configured account.
- The authors and maintainers of all direct and transitive packages that are
  too numerous to name individually here.

This page is an expression of gratitude, not a substitute for legal notices.
The authoritative dependency and license overview is maintained in
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). Model weights, datasets,
host tools, and online services can carry terms independent of the software
that loads them.
