import unittest

from app.lyrics_engine_v2 import (_source_reliability, capture_candidate,
                                  fuse_alignment_candidates,
                                  preserve_better_enhanced_input,
                                  preserve_uncorroborated_internal_editor_boundaries)
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
    def test_coarse_internal_rewrite_cannot_replace_editor_word_boundaries(self):
        editor = [line("Und wertvollen Gemälden an der Wand", 20.0, 24.0,
                       source="input-enhanced-lrc", source_timestamp=20.0)]
        generated = [line("Und wertvollen Gemälden an der Wand", 20.0, 24.0,
                          source="qwen-forced", source_timestamp=20.0)]
        # Same sentence edges and text, but the coarse recogniser filled a
        # real internal pause and moved both sides of that word boundary.
        generated[0].words[2]["end"] = 22.9
        generated[0].words[3]["start"] = 22.9
        editor[0].words[2]["end"] = 22.35
        editor[0].words[3]["start"] = 22.75

        selected, report = preserve_uncorroborated_internal_editor_boundaries(
            generated, capture_candidate(
                "input", "editor", editor, "human-editor"))

        self.assertEqual(1, report["preserved_lines"])
        self.assertEqual(22.35, selected[0].words[2]["end"])
        self.assertEqual(22.75, selected[0].words[3]["start"])

    def test_precisely_verified_internal_rewrite_is_retained(self):
        editor = [line("drei genaue Wörter", 10.0, 12.0,
                       source="input-enhanced-lrc", source_timestamp=10.0)]
        generated = [line("drei genaue Wörter", 10.0, 12.0,
                          source="ctc-phoneme-alignment", source_timestamp=10.0)]
        generated[0].words[0]["end"] = 10.35
        generated[0].words[1]["start"] = 10.55

        selected, report = preserve_uncorroborated_internal_editor_boundaries(
            generated, capture_candidate(
                "input", "editor", editor, "human-editor"))

        self.assertEqual(0, report["preserved_lines"])
        self.assertEqual(10.35, selected[0].words[0]["end"])
        self.assertEqual(10.55, selected[0].words[1]["start"])

    def test_measured_left_release_does_not_require_unchanged_right_onset_proof(self):
        editor = [line("Und davon berühmt", 10.0, 12.0,
                       source="input-enhanced-lrc", source_timestamp=10.0)]
        generated = [line("Und davon berühmt", 10.0, 12.0,
                          source="stable-ts-whisper", source_timestamp=10.0)]
        generated[0].words[1]["end"] = 10.95
        generated[0].words[1]["sustain_tonal_release"] = 10.95
        generated[0].words[1]["sustain_release_confidence"] = .91
        editor[0].words[1]["end"] = 11.45
        # The right onset did not move and therefore needs no second vote.
        generated[0].words[2]["start"] = editor[0].words[2]["start"]

        selected, report = preserve_uncorroborated_internal_editor_boundaries(
            generated, capture_candidate(
                "input", "editor", editor, "human-editor"))

        self.assertEqual(0, report["preserved_lines"])
        self.assertEqual(10.95, selected[0].words[1]["end"])

    def test_unproven_long_final_release_cannot_disable_editor_prefix_guard(self):
        editor = [line("Na klar ich werde älter", 10.0, 12.0,
                       source="input-enhanced-lrc", source_timestamp=10.0)]
        generated = [line("Na klar ich werde älter", 10.04, 14.0,
                          source="stable-ts-whisper", source_timestamp=10.0)]
        generated[0].words[1]["end"] += .25
        generated[0].words[2]["start"] += .25

        selected, report = preserve_uncorroborated_internal_editor_boundaries(
            generated, capture_candidate(
                "input", "editor", editor, "human-editor"))

        self.assertEqual(1, report["preserved_lines"])
        self.assertEqual(editor[0].words[2]["start"], selected[0].words[2]["start"])
        self.assertEqual(editor[0].words[-1]["end"], selected[0].words[-1]["end"])
        self.assertFalse(report["lines"][0]["preserved_precise_generated_release"])

    def test_precise_final_release_survives_restored_editor_prefix(self):
        editor = [line("Na klar ich werde älter", 10.0, 12.0,
                       source="input-enhanced-lrc", source_timestamp=10.0)]
        generated = [line("Na klar ich werde älter", 10.04, 13.0,
                          source="stable-ts-whisper", source_timestamp=10.0)]
        generated[0].words[1]["end"] += .25
        generated[0].words[2]["start"] += .25
        generated[0].words[-1]["sustain_tonal_release"] = 13.0
        generated[0].words[-1]["sustain_release_confidence"] = .92

        selected, report = preserve_uncorroborated_internal_editor_boundaries(
            generated, capture_candidate(
                "input", "editor", editor, "human-editor"))

        self.assertEqual(editor[0].words[2]["start"], selected[0].words[2]["start"])
        self.assertEqual(13.0, selected[0].words[-1]["end"])
        self.assertTrue(report["lines"][0]["preserved_precise_generated_release"])

    def test_low_confidence_sofa_section_is_not_scored_as_highly_reliable(self):
        weak = _source_reliability({
            "timing_source": "sofa-singing-alignment",
            "sofa_confidence": .43,
        })
        strong = _source_reliability({
            "timing_source": "sofa-singing-alignment",
            "sofa_confidence": .95,
        })

        self.assertLess(weak, .40)
        self.assertGreater(strong, .80)

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

    def test_corroborated_scope_boundary_replaces_leading_window_edge_fallback(self):
        baseline = [line("Lichtenhagen NSU das alles war kein Zufall", 59.34, 64.78,
                         source_timestamp=59.79)]
        baseline[0].words[0]["window_edge_fallback"] = True
        long_scope = [line("Lichtenhagen NSU das alles war kein Zufall", 60.87, 65.35,
                           source="stable-ts-whisper", source_timestamp=59.79)]
        short_scope = [line("Lichtenhagen NSU das alles war kein Zufall", 60.81, 65.37,
                            source="stable-ts-whisper", source_timestamp=59.79)]
        long_scope[0].words[0]["window_edge_fallback"] = True
        short_scope[0].words[0]["window_edge_fallback"] = True

        selected, report = fuse_alignment_candidates(
            [capture_candidate("baseline", "baseline", baseline, "legacy"),
             capture_candidate("long", "long scope", long_scope,
                               "full-transcript-stable-ts"),
             capture_candidate("short", "short scope", short_scope,
                               "full-transcript-stable-ts")],
            [(59.20, 60.30), (60.78, 65.42)], baseline_id="baseline",
            mode="select", minimum_line_improvement=.035)

        self.assertNotEqual("baseline", report["lines"][0]["selected"])
        self.assertGreaterEqual(selected[0].words[0]["start"], 60.80)
        self.assertTrue(report["lines"][0]["options"]["short"]["edge_fallback_rescue"])
        self.assertTrue(report["lines"][0]["options"]["short"]
                        ["corroborated_scope_edge"])
        self.assertNotIn("window_edge_fallback", selected[0].words[0])
        self.assertTrue(selected[0].words[0]["window_edge_fallback_resolved"])
        self.assertEqual(1, report["lines"][0]["options"]["short"]
                         ["independent_boundary_support"])

    def test_same_family_scopes_do_not_override_unmarked_baseline(self):
        baseline = [line("Lichtenhagen NSU", 59.34, 62.94, source_timestamp=59.79)]
        long_scope = [line("Lichtenhagen NSU", 60.87, 63.10,
                           source="stable-ts-whisper", source_timestamp=59.79)]
        short_scope = [line("Lichtenhagen NSU", 60.81, 63.12,
                            source="stable-ts-whisper", source_timestamp=59.79)]

        selected, report = fuse_alignment_candidates(
            [capture_candidate("baseline", "baseline", baseline, "legacy"),
             capture_candidate("long", "long scope", long_scope, "stable-ts"),
             capture_candidate("short", "short scope", short_scope, "stable-ts")],
            [(59.20, 60.30), (60.78, 63.20)], baseline_id="baseline",
            mode="select", minimum_line_improvement=0)

        self.assertEqual("baseline", report["lines"][0]["selected"])
        self.assertEqual(59.34, selected[0].words[0]["start"])

    def test_boundary_only_rescue_does_not_replace_line_or_move_word_end(self):
        baseline = [line("Ständig nur Gelaber", 18.88, 21.92,
                         source_timestamp=19.33)]
        baseline[0].words[0]["window_edge_fallback"] = True
        baseline[0].words[0]["end"] = 21.12
        baseline[0].words[1]["start"] = 21.12
        baseline[0].words[1]["end"] = 21.50
        baseline[0].words[2]["start"] = 21.50
        measured = [line("Ständig nur Gelaber", 20.35, 22.19,
                         source="stable-ts-whisper", source_timestamp=19.33)]
        measured[0].words[0]["window_edge_fallback"] = True
        original_end = baseline[0].words[0]["end"]

        selected, report = fuse_alignment_candidates(
            [capture_candidate("baseline", "baseline", baseline, "legacy"),
             capture_candidate("measured", "measured", measured,
                               "full-transcript-stable-ts")],
            [(20.30, 22.20)], baseline_id="baseline", mode="select",
            minimum_line_improvement=10.0)

        self.assertEqual("baseline", report["lines"][0]["selected"])
        self.assertEqual(20.35, selected[0].words[0]["start"])
        self.assertEqual(original_end, selected[0].words[0]["end"])
        self.assertEqual(1, report["leading_boundary_rescues"])
        self.assertEqual("boundary-only-acoustic-onset",
                         report["lines"][0]["leading_boundary_rescue"]["method"])

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
