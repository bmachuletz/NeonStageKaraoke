import unittest

from app.lyrics_engine_v2 import capture_candidate, fuse_alignment_candidates
from app.models import LrcLine


def line(text, start, end, source="qwen-forced", source_timestamp=None):
    words = text.split()
    width = (end - start) / len(words)
    return LrcLine(start, text, "", source_timestamp=(start if source_timestamp is None
                                                       else source_timestamp), words=[
        {"word": word, "start": start + index * width,
         "end": start + (index + 1) * width, "timing_source": source}
        for index, word in enumerate(words)
    ])


class LyricsEngineV2Tests(unittest.TestCase):
    def test_selects_acoustically_supported_chorus_instead_of_old_lrc_position(self):
        old = [line("Wir ham die Scheiße satt", 56.2, 57.5,
                    source="vocal-activity-repair", source_timestamp=56.2)]
        measured = [line("Wir ham die Scheiße satt", 57.54, 59.25,
                         source="stable-ts-whisper", source_timestamp=56.2)]
        candidates = [capture_candidate("old", "old", old, "lrclib"),
                      capture_candidate("measured", "measured", measured, "full-transcript")]

        selected, report = fuse_alignment_candidates(
            candidates, [(57.5, 59.3)], baseline_id="old", mode="select",
            minimum_line_improvement=.01)

        self.assertEqual("measured", report["lines"][0]["selected"])
        self.assertEqual(57.54, selected[0].words[0]["start"])

    def test_shadow_mode_never_changes_baseline(self):
        baseline = [line("Hallo Welt", 1.0, 2.0, source_timestamp=1.0)]
        alternative = [line("Hallo Welt", 3.0, 4.0, source_timestamp=1.0)]

        selected, report = fuse_alignment_candidates(
            [capture_candidate("base", "base", baseline, "lrclib"),
             capture_candidate("other", "other", alternative, "asr")],
            [(3.0, 4.0)], baseline_id="base", mode="shadow", minimum_line_improvement=.01)

        self.assertFalse(report["applied"])
        self.assertEqual(1.0, selected[0].words[0]["start"])

    def test_global_path_rejects_cross_line_collision(self):
        baseline = [line("erste Zeile", 1.0, 2.0), line("zweite Zeile", 2.2, 3.0)]
        collision = [line("erste Zeile", 1.0, 2.5), line("zweite Zeile", 2.3, 3.0)]

        selected, _report = fuse_alignment_candidates(
            [capture_candidate("base", "base", baseline, "lrclib"),
             capture_candidate("collision", "collision", collision, "asr")],
            [(1.0, 3.2)], baseline_id="base", mode="select", minimum_line_improvement=0)

        self.assertLessEqual(selected[0].words[-1]["end"], selected[1].words[0]["start"])

    def test_large_disagreement_needs_independent_support_when_both_cover_vocals(self):
        baseline = [line("lange Zeile", 1.0, 2.0, source_timestamp=1.0)]
        unsupported = [line("lange Zeile", 1.0, 4.0,
                            source="stable-ts-whisper", source_timestamp=1.0)]

        selected, report = fuse_alignment_candidates(
            [capture_candidate("base", "base", baseline, "lrclib"),
             capture_candidate("unsupported", "unsupported", unsupported, "stable")],
            [(1.0, 4.0)], baseline_id="base", mode="select", minimum_line_improvement=0)

        self.assertEqual(2.0, selected[0].words[-1]["end"])
        self.assertFalse(report["lines"][0]["options"]["unsupported"]["eligible"])

    def test_energy_geometry_cannot_replace_more_reliable_acoustic_alignment(self):
        baseline = [line("mehrere Wörter hier", 1.0, 3.0,
                         source="ctc-phoneme-alignment", source_timestamp=1.0)]
        geometric = [line("mehrere Wörter hier", 1.0, 2.0,
                          source="vocal-activity-repair", source_timestamp=1.0)]

        selected, report = fuse_alignment_candidates(
            [capture_candidate("base", "base", baseline, "forced"),
             capture_candidate("geometric", "geometric", geometric, "energy")],
            [(1.0, 3.0)], baseline_id="base", mode="select", minimum_line_improvement=0)

        self.assertEqual(3.0, selected[0].words[-1]["end"])
        self.assertFalse(report["lines"][0]["options"]["geometric"]["eligible"])

    def test_rejects_candidate_with_different_canonical_text(self):
        with self.assertRaises(ValueError):
            fuse_alignment_candidates(
                [capture_candidate("base", "base", [line("Hallo Welt", 1, 2)], "lrclib"),
                 capture_candidate("other", "other", [line("Anderer Text", 1, 2)], "asr")],
                [(1, 2)], baseline_id="base", mode="select")


if __name__ == "__main__":
    unittest.main()
