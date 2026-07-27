# Neon Stage vendor record

- Upstream: https://github.com/qiuqiao/SOFA
- Upstream commit: `584d6b9a57927843f85decebf4cbf03b9598125f`
- Upstream license: MIT; see `LICENSE` in this directory
- Vendored for: optional singing-specific forced alignment

Neon Stage modifies `modules/utils/load_wav.py` to fall back to librosa when a
new TorchAudio build delegates decoding to an unavailable TorchCodec runtime.
The fallback keeps SOFA inference independent from that optional decoder ABI.

The upstream `.git` directory is deliberately not distributed. This directory
contains the actual reviewed source at the revision above plus the documented
Neon Stage modification, rather than an unresolved Git submodule pointer.
