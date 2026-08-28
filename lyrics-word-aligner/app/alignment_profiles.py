from __future__ import annotations


# Keep API validation, the isolated worker CLI and the pipeline contract on
# one shared list. A profile accepted by the API must never be rejected only
# after the potentially large multipart upload has completed.
ALIGNMENT_PROFILES = (
    "standard", "editor-guided", "research-shadow", "trusted-ultrastar",
    "basic-pitch-ab", "basic-pitch-postprocess",
)
