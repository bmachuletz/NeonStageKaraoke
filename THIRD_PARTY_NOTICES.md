# Third-party notices

Last audited against the repository manifests and locally restored package
metadata on 2026-07-27.

Neon Stage's original source is Apache-2.0. The following software, fonts,
models, services, and build components remain under their own licenses and
terms. Copyright remains with their respective authors. The exact files inside
a released artifact and the license/copyright files shipped with those files
are authoritative. See [`ACKNOWLEDGEMENTS.md`](ACKNOWLEDGEMENTS.md) for our
thanks to the people behind these projects.

## Application and .NET runtime

| Component | Use | License / terms |
|---|---|---|
| [.NET and ASP.NET Core](https://github.com/dotnet/dotnet) | runtime, server, SignalR client, Microsoft.Extensions | MIT; official runtime distributions can include additional third-party notices |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) 11.3.18 | desktop/mobile UI | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 | MVVM utilities | MIT |
| [QRCoder](https://github.com/Shane32/QRCoder) 1.8.0 | QR rendering | MIT |
| [TagLib#](https://github.com/mono/taglib-sharp) 2.3.0 | audio metadata | **LGPL-2.1-only** |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) 3.10.0 | managed VLC binding | LGPL-2.1-or-later |
| [VLC / libVLC](https://code.videolan.org/videolan/vlc) | native audio playback | libVLC core is LGPL-2.1-or-later; individual modules and bundled libraries can use LGPL, GPL, or other compatible licenses. Release artifacts include the distributor copyright file and LGPL/GPL texts. |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 10.0.10 | database provider | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) 2.1.x | native SQLite bridge/bundle | Apache-2.0 |
| [SQLite](https://sqlite.org/copyright.html) | database engine | public domain |
| [SkiaSharp](https://github.com/mono/SkiaSharp), [HarfBuzzSharp](https://github.com/mono/SkiaSharp), MicroCom.Runtime, Tmds.DBus.Protocol | transitive UI/rendering/runtime dependencies | MIT in the restored package metadata; native components retain their notices |
| [Inter](https://github.com/rsms/inter) | bundled Avalonia font | SIL Open Font License 1.1 |

The AppImage dynamically loads its replaceable VLC libraries from its own
`usr/lib` tree. Corresponding VLC source is available from VideoLAN; recipients
retain the LGPL rights to inspect, replace, relink, modify, and redistribute
the applicable components under their licenses.

## Alignment service and Python runtime

These components belong to the optional, separately built alignment service;
they are not included in the server image or editor AppImage.

| Component | License / terms |
|---|---|
| FastAPI | MIT |
| Uvicorn | BSD-3-Clause |
| python-multipart | Apache-2.0 |
| NumPy | BSD-3-Clause with separately documented bundled code under 0BSD, MIT, Zlib and CC0-1.0 |
| SoundFile | BSD-3-Clause; libsndfile has its own LGPL terms |
| PyTorch / torchvision | BSD-3-Clause |
| torchaudio | BSD-2-Clause in the installed 2.11 package; downloaded pipeline weights have independent terms |
| Hugging Face Transformers, Accelerate, SentencePiece | Apache-2.0 |
| Pyphen | GPL-2.0-or-later / LGPL-2.1-or-later / MPL-1.1 tri-license; bundled dictionaries retain their individual LibreOffice dictionary notices |
| ONNX Runtime | MIT |
| python-audio-separator | MIT; downloaded UVR/separation model weights retain independent author/model terms |
| Lightning, TensorBoard, NLTK | Apache-2.0 |
| h5py, pandas, msgspec | BSD-3-Clause |
| Matplotlib | Matplotlib/PSF-based license plus notices for bundled data and fonts |
| TextGrid, TensorBoardX, RapidFuzz | MIT |
| EasyAligner 0.3.3 | MIT |
| stable-ts 2.19.1 | MIT |
| SOFA 1.0.3 code vendored in `lyrics-word-aligner/third_party/SOFA` | MIT; preserve its bundled LICENSE |

Python packages bring transitive dependencies. A distributed alignment image
must additionally preserve the license files installed in each exact wheel and
the NVIDIA CUDA base-image notices.

## AI models and datasets

Model licenses are independent from the libraries that load them. Model files
are ignored and never enter the application releases.

| Model / family | License / restriction |
|---|---|
| Qwen3-ASR and Qwen3 ForcedAligner model revisions used by the pipeline | Apache-2.0 according to their Qwen model cards; pin and retain the exact downloaded model card/revision before redistribution |
| OpenAI Whisper code and published model weights | MIT |
| Silero VAD code/model shipped by its repository | MIT; the cached repository LICENSE is retained locally |
| torchaudio `MMS_FA` / Meta MMS weights | **CC-BY-NC-4.0**; attribution and the non-commercial restriction apply. Do not use this optional verifier for a commercial deployment without separate permission. |
| UVR / Roformer separation weights | model-author-specific terms; `python-audio-separator` being MIT does not relicense downloaded weights |
| SOFA singing checkpoints and their training datasets | verify and retain the exact checkpoint and dataset-provider terms before redistribution; the MIT code license alone does not establish model/dataset rights |

## Engine, system tools, integrations, and services

| Component | Use | License / terms |
|---|---|---|
| Unity Engine and Unity packages | stage runtime/editor | Unity Software Terms and package-specific licenses (commonly Unity Companion License); not covered by Neon Stage's Apache-2.0 license |
| TextMesh Pro resources / Liberation Sans | stage text | Unity package terms / SIL Open Font License 1.1 respectively |
| FFmpeg | decoding, conversion, analysis | LGPL-2.1-or-later or GPL-2.0-or-later depending on the exact build/configuration; container and AppImage artifacts retain the distributor build/version and copyright metadata |
| curl / libcurl | server-container health and worker HTTP | curl license; Debian copyright metadata is retained |
| linuxdeploy / appimagetool | AppImage build tooling | MIT for the upstream projects; verify downloaded build-tool revisions before release |
| AppImage Type-2 runtime | embedded launcher in each AppImage | MIT; its upstream license and bundled-dependency inventory are included as `AppImage-Type2-Runtime-LICENSE.txt` in every AppImage |
| Sunnify Spotify Downloader | optional, separately installed downloader | GPL-3.0; not vendored or bundled by Neon Stage |
| Spotify Web API | metadata/search integration | Spotify Developer Terms and branding rules; API access does not license music or lyrics |
| Qobuz API | optional catalog matching and authorized purchase downloads | Qobuz API/partner and store terms; API access does not grant media rights, and only account-authorized `intent=download` responses are accepted |
| LRCLIB API | lyrics lookup | service terms and copyright in individual lyrics remain applicable |
| [UltraStar Deluxe TXT format](https://github.com/UltraStar-Deluxe/USDX) | optional lyrics import compatibility | UltraStar Deluxe itself is GPL-2.0-or-later. Neon Stage's parser is an independent implementation of the text format; no USDX source code, executable, song, audio, cover, or lyrics data is bundled. Imported user content retains its own rights and terms. |
| Ko-fi widget | support link on the static project website | externally hosted Ko-fi JavaScript and Ko-fi terms/privacy policy; not bundled into application releases |

## Distribution checklist

Before publishing an APK, desktop bundle, container image, alignment image, or
downloadable model pack:

1. Generate inventories from the exact locked/restored artifact, including
   transitive NuGet, Python, Unity, OS/native packages, fonts, and model files.
2. Include every required copyright notice and full license text in the
   artifact and expose the matching notices from the application or website.
3. For LGPL components, keep them replaceable where required and provide the
   applicable license, notices, source location, and relinking information.
4. Do not redistribute MMS under terms incompatible with CC-BY-NC-4.0, and do
   not redistribute other AI weights until their exact revision and license
   have been recorded.
5. Do not distribute songs, stems, lyrics, or cover art without explicit
   distribution rights.
6. Keep Sunnify separately installed and process-isolated; do not incorporate
   its GPL source without a deliberate GPL compliance review.
7. Re-run `./scripts/license/check-release-tree.sh` for every release and
   archive the resulting exact dependency inventory with the build record.

This register is engineering documentation, not legal advice. See
[`docs/legal/licensing.md`](docs/legal/licensing.md) for the audit procedure.
