from app.chorus_anchors import apply_trusted_chorus_anchors
from app.models import LrcLine


def _line(source: float, start: float, text: str = "Wir verzweifeln nicht daran") -> LrcLine:
    return LrcLine(
        timestamp=source,
        source_timestamp=source,
        text=text,
        original=text,
        words=[
            {"word": "Wir", "start": start, "end": start + 0.3, "timing_source": "ctc"},
            {"word": "verzweifeln", "start": start + 0.3, "end": start + 1.0, "timing_source": "ctc"},
            {"word": "nicht", "start": start + 1.0, "end": start + 1.4, "timing_source": "ctc"},
        ],
    )


def test_repeated_late_chorus_is_shifted_to_line_anchor(monkeypatch):
    monkeypatch.delenv("LRC_CHORUS_ANCHOR_MIN_REPETITIONS", raising=False)
    lines = [_line(11.37, 12.55), _line(14.93, 15.48), _line(19.46, 20.81)]
    durations = [[word["end"] - word["start"] for word in line.words] for line in lines]

    result = apply_trusted_chorus_anchors(lines)

    assert result["shifted_lines"] == 3
    assert [line.words[0]["start"] for line in lines] == [11.37, 14.93, 19.46]
    assert [[word["end"] - word["start"] for word in line.words] for line in lines] == durations
    assert all(word["timing_source"] == "lrc-chorus-anchor" for line in lines for word in line.words[:3])


def test_only_backing_vocal_prefix_moves_while_lead_suffix_stays_acoustic():
    lines = [_line(11.37, 12.55), _line(14.93, 15.48), _line(19.46, 20.81)]
    for line in lines:
        line.words.append({
            "word": "wegen", "start": line.words[-1]["end"] + 0.8,
            "end": line.words[-1]["end"] + 1.2, "timing_source": "ctc",
        })
    suffix_intervals = [(line.words[3]["start"], line.words[3]["end"]) for line in lines]

    apply_trusted_chorus_anchors(lines)

    assert [(line.words[3]["start"], line.words[3]["end"]) for line in lines] == suffix_intervals
    assert all(line.words[3]["timing_source"] == "ctc" for line in lines)


def test_unique_early_or_implausibly_late_line_is_unchanged():
    lines = [
        _line(10.0, 10.1),
        _line(20.0, 22.5),
        _line(30.0, 30.4),
        _line(40.0, 40.5, "Eine völlig andere Textzeile"),
    ]

    result = apply_trusted_chorus_anchors(lines)

    assert result["shifted_lines"] == 1
    assert lines[0].words[0]["start"] == 10.1
    assert lines[1].words[0]["start"] == 22.5
    assert lines[2].words[0]["start"] == 30.0
    assert lines[3].words[0]["start"] == 40.5
