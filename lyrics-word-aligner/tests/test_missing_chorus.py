import numpy as np

from app.chorus_anchors import recover_missing_initial_chorus
from app.models import LrcLine


def _line(source: float) -> LrcLine:
    text = "Wir verzweifeln nicht wegen irgendetwas"
    return LrcLine(source, text, text, source_timestamp=source, words=[
        {"word": "Wir", "start": source + 1.0, "end": source + 1.2},
        {"word": "verzweifeln", "start": source + 1.2, "end": source + 1.7},
        {"word": "nicht", "start": source + 1.7, "end": source + 2.0},
        {"word": "wegen", "start": source + 2.1, "end": source + 2.4},
    ])


def test_recovers_only_prefix_from_instrumental_and_preserves_lead_suffix():
    lines = [_line(11.37), _line(14.93), _line(19.46)]
    suffix = dict(lines[0].words[3])

    def transcribe(_audio, _language, _device):
        return {"text": "Der war zweifelig", "words": [
            {"word": "Der", "start": 1.79, "end": 2.1},
            {"word": "war", "start": 2.1, "end": 2.35},
            {"word": "zweifelig", "start": 2.35, "end": 3.6},
        ]}

    result = recover_missing_initial_chorus(
        np.zeros(30 * 16000, dtype=np.float32),
        np.ones(30 * 16000, dtype=np.float32),
        lines, "de", "cpu", transcribe_fn=transcribe)

    assert result["recovered_lines"] == 1
    assert lines[0].words[0]["start"] == 10.16
    assert lines[0].words[2]["end"] == 11.97
    assert lines[0].words[3] == suffix
    assert all(word["timing_source"] == "instrumental-chorus-stable-ts"
               for word in lines[0].words[:3])
