from __future__ import annotations

import os
from typing import Any


def _downloads_allowed() -> bool:
    return os.getenv("LRC_ALLOW_MODEL_DOWNLOADS", "true").strip().lower() in {
        "1", "true", "yes", "on"
    }


def from_pretrained_local_first(factory: Any, model_id: str, **kwargs: Any) -> Any:
    """Load a Transformers asset without probing the network when cached.

    Hugging Face normally sends metadata HEAD requests even for cached models.
    A mounted, complete cache must therefore be tried explicitly first. Network
    access remains a fallback for the initial model installation only.
    """
    try:
        return factory.from_pretrained(model_id, local_files_only=True, **kwargs)
    except OSError as local_error:
        if not _downloads_allowed():
            raise RuntimeError(
                f"Modell '{model_id}' fehlt oder ist im lokalen Cache unvollständig. "
                "Netzwerk-Downloads sind durch LRC_ALLOW_MODEL_DOWNLOADS=false deaktiviert."
            ) from local_error
        return factory.from_pretrained(model_id, **kwargs)


def hub_file_local_first(repo_id: str, filename: str, **kwargs: Any) -> str:
    """Resolve one Hub file locally first and download it only when absent."""
    from huggingface_hub import hf_hub_download

    try:
        return hf_hub_download(
            repo_id=repo_id, filename=filename, local_files_only=True, **kwargs)
    except OSError as local_error:
        if not _downloads_allowed():
            raise RuntimeError(
                f"Modelldatei '{repo_id}/{filename}' fehlt im lokalen Cache. "
                "Netzwerk-Downloads sind durch LRC_ALLOW_MODEL_DOWNLOADS=false deaktiviert."
            ) from local_error
        return hf_hub_download(repo_id=repo_id, filename=filename, **kwargs)
