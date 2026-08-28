import pytest

from app.model_loading import from_pretrained_local_first


class _CachedFactory:
    calls = []

    @classmethod
    def from_pretrained(cls, model_id, **kwargs):
        cls.calls.append((model_id, kwargs))
        return "cached"


class _MissingFactory:
    calls = []

    @classmethod
    def from_pretrained(cls, model_id, **kwargs):
        cls.calls.append((model_id, kwargs))
        if kwargs.get("local_files_only"):
            raise OSError("not cached")
        return "downloaded"


def test_cached_model_never_uses_network_fallback(monkeypatch):
    monkeypatch.setenv("LRC_ALLOW_MODEL_DOWNLOADS", "true")
    _CachedFactory.calls.clear()
    assert from_pretrained_local_first(_CachedFactory, "example/model") == "cached"
    assert _CachedFactory.calls == [("example/model", {"local_files_only": True})]


def test_missing_model_may_download_once(monkeypatch):
    monkeypatch.setenv("LRC_ALLOW_MODEL_DOWNLOADS", "true")
    _MissingFactory.calls.clear()
    assert from_pretrained_local_first(_MissingFactory, "example/model") == "downloaded"
    assert _MissingFactory.calls == [
        ("example/model", {"local_files_only": True}),
        ("example/model", {}),
    ]


def test_strict_offline_mode_reports_missing_cache(monkeypatch):
    monkeypatch.setenv("LRC_ALLOW_MODEL_DOWNLOADS", "false")
    _MissingFactory.calls.clear()
    with pytest.raises(RuntimeError, match="lokalen Cache unvollständig"):
        from_pretrained_local_first(_MissingFactory, "example/model")
    assert len(_MissingFactory.calls) == 1
