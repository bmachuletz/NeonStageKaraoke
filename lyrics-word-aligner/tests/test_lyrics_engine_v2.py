import unittest

from app.lyrics_engine_v2 import (capture_candidate, fuse_alignment_candidates,
                                  preserve_better_enhanced_input)
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

    def test_ineligible_candidate_never_wins_to_avoid_baseline_transition_penalty(self):
        baseline = [
            line("erste Zeile", 1.0, 3.0, source_timestamp=1.0),
            line("zweite Zeile", 2.5, 4.0, source_timestamp=2.5),
        ]
        unsupported = [
            line("erste Zeile", 1.0, 2.0,
                 source="stable-ts-whisper", source_timestamp=1.0),
            line("zweite Zeile", 2.5, 4.0,
                 source="stable-ts-whisper", source_timestamp=2.5),
        ]

        selected, report = fuse_alignment_candidates(
            [capture_candidate("base", "base", baseline, "lrclib"),
             capture_candidate("unsupported", "unsupported", unsupported, "stable")],
            [(1.0, 4.0)], baseline_id="base", mode="select", minimum_line_improvement=0)

        self.assertFalse(report["lines"][0]["options"]["unsupported"]["eligible"])
        self.assertEqual("base", report["lines"][0]["selected"])
        self.assertEqual(3.0, selected[0].words[-1]["end"])

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

    def test_better_enhanced_editor_timing_survives_shadow_alignment(self):
        generated = [line("an der Wand", 24.8, 28.38,
                          source="stable-ts-whisper", source_timestamp=24.8)]
        editor = [line("an der Wand", 24.8, 25.99,
                       source="input-enhanced-lrc", source_timestamp=24.8)]

        selected, report = preserve_better_enhanced_input(
            generated, capture_candidate("input", "editor", editor, "human-editor"),
            [(24.74, 25.99)], minimum_improvement=.01)

        self.assertEqual(1, report["preserved_lines"])
        self.assertEqual(25.99, selected[0].words[-1]["end"])

    def test_worse_enhanced_input_never_replaces_acoustic_result(self):
        generated = [line("Hallo Welt", 3.0, 4.0,
                          source="ctc-phoneme-alignment", source_timestamp=1.0)]
        editor = [line("Hallo Welt", 1.0, 2.0,
                       source="input-enhanced-lrc", source_timestamp=1.0)]

        selected, report = preserve_better_enhanced_input(
            generated, capture_candidate("input", "editor", editor, "human-editor"),
            [(3.0, 4.0)], minimum_improvement=.01)

        self.assertEqual(0, report["preserved_lines"])
        self.assertEqual(3.0, selected[0].words[0]["start"])

    def test_valid_editor_line_replaces_overlapping_automatic_words(self):
        generated = [line("to live but", 10.0, 11.0,
                          source="qwen-forced", source_timestamp=10.0)]
        generated[0].words[0]["end"] = 10.5
        generated[0].words[1]["start"] = 10.3
        editor = [line("to live but", 10.0, 11.0,
                       source="input-enhanced-lrc", source_timestamp=10.0)]

        selected, report = preserve_better_enhanced_input(
            generated, capture_candidate("input", "editor", editor, "human-editor"),
            [(10.0, 11.0)], minimum_improvement=.01)

        self.assertEqual(1, report["preserved_lines"])
        self.assertLessEqual(selected[0].words[0]["end"], selected[0].words[1]["start"])

    def test_editor_release_wins_when_generated_line_ends_inside_vocal_phrase(self):
        generated = [line("century digital boy", 34.7, 36.7,
                          source="qwen-forced", source_timestamp=34.7)]
        editor = [line("century digital boy", 34.7, 37.8,
                       source="input-enhanced-lrc", source_timestamp=34.7)]

        selected, report = preserve_better_enhanced_input(
            generated, capture_candidate("input", "editor", editor, "human-editor"),
            [(34.0, 37.82)], minimum_improvement=.01)

        self.assertEqual(1, report["preserved_lines"])
        self.assertEqual(37.8, selected[0].words[-1]["end"])

    def test_coherent_editor_chorus_repairs_automatic_cross_line_collision(self):
        generated = [
            line("digital boy", 34.1, 38.7, source="qwen-forced", source_timestamp=34.1),
            line("I got toys", 36.7, 41.5, source="qwen-forced", source_timestamp=38.2),
            line("my daddy", 41.64, 44.7, source="qwen-forced", source_timestamp=41.8),
        ]
        editor = [
            line("digital boy", 34.1, 37.8, source="input-enhanced-lrc", source_timestamp=34.1),
            line("I got toys", 38.2, 41.76, source="input-enhanced-lrc", source_timestamp=38.2),
            line("my daddy", 41.76, 44.6, source="input-enhanced-lrc", source_timestamp=41.8),
        ]

        selected, report = preserve_better_enhanced_input(
            generated, capture_candidate("input", "editor", editor, "human-editor"),
            [(34.05, 37.85), (38.15, 44.65)], minimum_improvement=.01)

        self.assertEqual(1, len(report["preserved_blocks"]))
        self.assertEqual(3, report["preserved_lines"])
        self.assertEqual(37.8, selected[0].words[-1]["end"])
        self.assertEqual(38.2, selected[1].words[0]["start"])
        self.assertEqual(41.76, selected[2].words[0]["start"])

    def test_cross_line_block_does_not_restore_invalid_editor_words(self):
        generated = [
            line("digital boy", 34.1, 38.7, source="qwen-forced", source_timestamp=34.1),
            line("I got toys", 36.7, 41.5, source="qwen-forced", source_timestamp=38.2),
        ]
        editor = [
            line("digital boy", 34.1, 37.8, source="input-enhanced-lrc", source_timestamp=34.1),
            line("I got toys", 38.2, 41.7, source="input-enhanced-lrc", source_timestamp=38.2),
        ]
        editor[1].words[0]["end"] = 40.0
        editor[1].words[1]["start"] = 39.0

        selected, report = preserve_better_enhanced_input(
            generated, capture_candidate("input", "editor", editor, "human-editor"),
            [(34.05, 41.75)], minimum_improvement=.01)

        self.assertEqual([], report["preserved_blocks"])
        self.assertEqual(38.7, selected[0].words[-1]["end"])


if __name__ == "__main__":
    unittest.main()
