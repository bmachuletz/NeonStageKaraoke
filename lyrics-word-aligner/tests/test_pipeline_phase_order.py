import unittest
import inspect
import re

from app.lyrics_engine_v2 import AlignmentCandidate
from app.models import LrcLine
from app.pipeline import (PIPELINE_PHASE_ORDER, _manual_editor_ranges,
                          _constrain_following_lines_after_manual_editor_holds,
                          _constrain_preceding_lines_before_manual_editor_starts,
                          _prepare_research_shadow_input,
                          _restore_editor_tail_after_delayed_first_word,
                          _restore_editor_syllable_tail_after_delayed_first_word,
                          _restore_manual_editor_lines,
                          _restore_manual_editor_syllables, run)


class PipelinePhaseOrderTests(unittest.TestCase):
    def test_research_shadow_discards_all_editor_timing_authority(self):
        lines = [LrcLine(12.5, "Sing this line", "", words=[
            {"word": "Sing", "start": 15.0, "end": 16.0,
             "editor_word_manual_adjusted": True,
             "editor_syllables": [{"text": "Sing", "start": 15.0, "end": 16.0}]},
        ], manual_adjusted=True, manual_editor_start=12.0,
                         manual_editor_end=17.0)]
        headers = ["[ar:Artist]", "[neon-manual:12.000,17.000]",
                   "[neon-editor-syllables:payload]"]

        clean_headers, summary = _prepare_research_shadow_input(headers, lines)

        self.assertEqual(["[ar:Artist]"], clean_headers)
        self.assertEqual([], lines[0].words)
        self.assertFalse(lines[0].manual_adjusted)
        self.assertIsNone(lines[0].manual_editor_start)
        self.assertIsNone(lines[0].manual_editor_end)
        self.assertEqual(1, summary["removed_word_timing_records"])
        self.assertFalse(summary["manual_timing_authority"])

    def test_verified_delayed_first_onset_keeps_explicitly_edited_words(self):
        source = [LrcLine(68.486, "Denn die Lage", "", words=[
            {"word": "Denn", "start": 68.486, "end": 69.46,
             "editor_word_manual_adjusted": True},
            {"word": "die", "start": 69.46, "end": 69.68,
             "editor_word_manual_adjusted": True},
            {"word": "Lage", "start": 69.68, "end": 70.10,
             "editor_word_manual_adjusted": True},
        ])]
        current = [LrcLine(69.347, "Denn die Lage", "", words=[
            {"word": "Denn", "start": 69.347, "end": 69.516,
             "timing_source": "ipa-delayed-first-word-onset"},
            {"word": "die", "start": 69.636, "end": 69.876},
            {"word": "Lage", "start": 69.876, "end": 70.348},
        ])]

        result = _restore_editor_tail_after_delayed_first_word(
            current, AlignmentCandidate("editor", "Editor", source, {}))

        self.assertEqual(1, result["restored_lines"])
        self.assertEqual(69.347, current[0].words[0]["start"])
        self.assertEqual(69.46, current[0].words[0]["end"])
        self.assertEqual(69.46, current[0].words[1]["start"])
        self.assertEqual(70.10, current[0].words[2]["end"])

    def test_generated_editor_basis_does_not_override_acoustic_tail(self):
        source = [LrcLine(68.036, "Denn die Lage", "", words=[
            {"word": "Denn", "start": 68.036, "end": 69.516},
            {"word": "die", "start": 69.460, "end": 69.680},
            {"word": "Lage", "start": 69.680, "end": 70.100},
        ])]
        current = [LrcLine(69.347, "Denn die Lage", "", words=[
            {"word": "Denn", "start": 69.347, "end": 69.516,
             "timing_source": "ipa-delayed-first-word-onset"},
            {"word": "die", "start": 69.636, "end": 69.876},
            {"word": "Lage", "start": 69.876, "end": 70.348},
        ])]

        result = _restore_editor_tail_after_delayed_first_word(
            current, AlignmentCandidate("editor", "Editor", source, {}))

        self.assertEqual(0, result["restored_lines"])
        self.assertEqual(69.636, current[0].words[1]["start"])
        self.assertEqual(70.348, current[0].words[2]["end"])

    def test_verified_delayed_onset_keeps_following_editor_syllables(self):
        source = [LrcLine(68.486, "Denn die Lage", "", words=[
            {"word": "Denn", "start": 68.486, "end": 69.46,
             "editor_syllables": [
                 {"text": "Denn", "start": 68.486, "end": 69.46}]},
            {"word": "die", "start": 69.46, "end": 69.68,
             "editor_word_manual_adjusted": True,
             "editor_reference_syllables": [
                 {"text": "die", "start": 69.46, "end": 69.68}]},
            {"word": "Lage", "start": 69.68, "end": 70.10,
             "editor_word_manual_adjusted": True,
             "editor_reference_syllables": [
                 {"text": "La", "start": 69.68, "end": 69.883},
                 {"text": "ge", "start": 69.883, "end": 70.10}]},
        ])]
        current = [LrcLine(69.347, "Denn die Lage", "", words=[
            {"word": "Denn", "start": 69.347, "end": 69.46,
             "timing_source": "ipa-delayed-first-word-onset",
             "syllables": [{"text": "Denn", "start": 69.347, "end": 69.46}]},
            {"word": "die", "start": 69.46, "end": 69.68,
             "syllables": [{"text": "die", "start": 69.46, "end": 69.68}]},
            {"word": "Lage", "start": 69.68, "end": 70.10,
             "syllables": [
                 {"text": "La", "start": 69.68, "end": 70.10},
                 {"text": "ge", "start": 70.10, "end": 70.10}]},
        ])]

        result = _restore_editor_syllable_tail_after_delayed_first_word(
            current, AlignmentCandidate("editor", "Editor", source, {}))

        self.assertEqual(2, result["restored_words"])
        self.assertEqual(3, result["restored_syllables"])
        self.assertEqual(69.883, current[0].words[2]["syllables"][0]["end"])
        self.assertEqual(69.347, current[0].words[0]["syllables"][0]["start"])

    def test_only_the_exact_manually_marked_source_line_is_restored(self):
        source = [
            # A line may be displayed before its first sung word. The manual
            # marker follows this line timestamp, not the word onset.
            LrcLine(9.9, "first line", "", words=[
                {"word": "first", "start": 10.0, "end": 10.5},
                {"word": "line", "start": 10.6, "end": 11.0},
            ]),
            LrcLine(11.0, "second line", "", words=[
                {"word": "second", "start": 11.0, "end": 11.5},
                {"word": "line", "start": 11.6, "end": 12.0},
            ]),
        ]
        current = [
            LrcLine(9.8, "first line", "", words=[
                {"word": "first", "start": 9.8, "end": 10.4},
                {"word": "line", "start": 10.4, "end": 11.2},
            ]),
            LrcLine(11.2, "second line", "", words=[
                {"word": "second", "start": 11.2, "end": 11.6},
                {"word": "line", "start": 11.6, "end": 12.0},
            ]),
        ]
        candidate = AlignmentCandidate("editor", "Editor", source, {})
        # Editor lines may deliberately retain a short display/hold tail after
        # their last sung word. The marker therefore ends after the word data.
        ranges = _manual_editor_ranges(["[neon-manual:9.900,11.250]"])

        summary = _restore_manual_editor_lines(current, candidate, ranges)

        self.assertEqual(1, summary["preserved_lines"])
        self.assertEqual(9.9, current[0].timestamp)
        self.assertEqual(9.9, current[0].manual_editor_start)
        self.assertEqual(11.25, current[0].manual_editor_end)
        self.assertEqual(10.0, current[0].words[0]["start"])
        self.assertEqual(11.2, current[1].words[0]["start"])
        self.assertTrue(current[0].manual_adjusted)

        boundary_summary = _constrain_following_lines_after_manual_editor_holds(current)
        self.assertEqual(1, boundary_summary["adjusted_lines"])
        self.assertEqual(11.25, current[1].timestamp)
        self.assertEqual(11.25, current[1].words[0]["start"])
        self.assertAlmostEqual(11.65, current[1].words[0]["end"])

    def test_generated_predecessor_is_trimmed_before_manual_display_start(self):
        previous = LrcLine(10.0, "automatic tail", "", words=[
            {"word": "automatic", "start": 10.0, "end": 10.5},
            {"word": "tail", "start": 10.5, "end": 11.032,
             "syllables": [{"text": "tail", "start": 10.5, "end": 11.032}]},
        ])
        manual = LrcLine(11.0, "manual line", "", words=[
            {"word": "manual", "start": 11.1, "end": 11.5},
            {"word": "line", "start": 11.5, "end": 12.0},
        ], manual_adjusted=True, manual_editor_start=11.0,
                         manual_editor_end=12.1)

        summary = _constrain_preceding_lines_before_manual_editor_starts(
            [previous, manual])

        self.assertEqual(1, summary["adjusted_lines"])
        self.assertEqual(11.0, previous.words[-1]["end"])
        self.assertEqual(11.0, previous.words[-1]["syllables"][-1]["end"])
        self.assertEqual(11.0, manual.timestamp)
        self.assertEqual(11.1, manual.words[0]["start"])

    def test_two_manual_neighbours_are_never_rewritten(self):
        previous = LrcLine(10.0, "first", "", words=[
            {"word": "first", "start": 10.0, "end": 11.1},
        ], manual_adjusted=True, manual_editor_start=10.0,
                           manual_editor_end=11.1)
        following = LrcLine(11.0, "second", "", words=[
            {"word": "second", "start": 11.2, "end": 12.0},
        ], manual_adjusted=True, manual_editor_start=11.0,
                            manual_editor_end=12.0)

        summary = _constrain_preceding_lines_before_manual_editor_starts(
            [previous, following])

        self.assertEqual(0, summary["adjusted_lines"])
        self.assertEqual(11.1, previous.words[-1]["end"])

    def test_pipeline_progresses_from_broad_hypotheses_to_final_children(self):
        self.assertEqual(len(PIPELINE_PHASE_ORDER), len(set(PIPELINE_PHASE_ORDER)))
        positions = {name: index for index, name in enumerate(PIPELINE_PHASE_ORDER)}
        dependencies = [
            ("transcript-evidence", "primary-word-alignment"),
            ("primary-word-alignment", "independent-forced-refinement"),
            ("independent-forced-refinement", "stage-stem-finalization"),
            ("stage-stem-finalization", "timing-candidate-fusion"),
            ("timing-candidate-fusion", "macro-boundary-refinement"),
            ("missing-lyrics-recovery", "late-word-geometry"),
            ("late-word-geometry", "editor-boundary-protection"),
            ("editor-boundary-protection", "hard-boundary-precheck"),
            ("hard-boundary-precheck", "sentence-word-verification"),
            ("sentence-word-verification", "final-hard-constraints"),
            ("final-hard-constraints", "syllable-derivation"),
            ("syllable-derivation", "validation-and-export"),
        ]
        for prerequisite, dependent in dependencies:
            self.assertLess(positions[prerequisite], positions[dependent])

    def test_manual_editor_syllables_override_only_the_edited_word(self):
        source = [LrcLine(10.0, "singing loudly", "", words=[
            {"word": "singing", "start": 10.0, "end": 11.0,
             "editor_syllables": [
                 {"text": "sing", "start": 10.0, "end": 10.42,
                  "manual_adjusted": True},
                 {"text": "ing", "start": 10.42, "end": 11.0,
                  "manual_adjusted": True},
             ]},
            {"word": "loudly", "start": 11.1, "end": 12.0},
        ])]
        current = [LrcLine(10.0, "singing loudly", "", words=[
            {"word": "singing", "start": 10.0, "end": 11.0,
             "syllables": [{"text": "sing", "start": 10.0, "end": 10.7}]},
            {"word": "loudly", "start": 11.1, "end": 12.0,
             "syllables": [{"text": "loud", "start": 11.1, "end": 11.6}]},
        ])]
        candidate = AlignmentCandidate("editor", "Editor", source, "human-editor")

        summary = _restore_manual_editor_syllables(current, candidate)

        self.assertEqual(1, summary["preserved_words"])
        self.assertAlmostEqual(10.42, current[0].words[0]["syllables"][0]["end"])
        self.assertAlmostEqual(11.6, current[0].words[1]["syllables"][0]["end"])

    def test_actual_mutating_calls_follow_the_declared_quality_funnel(self):
        source = inspect.getsource(run)
        calls = [
            "align_global_phoneme_path(",
            "fuse_alignment_candidates(",
            "preserve_uncorroborated_internal_editor_boundaries(",
            "refine_sustain_releases_with_voicing(",
            "annotate_phoneme_boundaries(",
            "constrain_final_words_to_source_boundaries(",
            "constrain_lyrics_before_nonlexical_vocalizations(",
        ]
        positions = [source.index(call) for call in calls]
        positions.extend([
            source.rindex("final_overlap_fallback = eliminate_remaining_line_overlaps("),
            source.rindex("stage_vocal_boundaries = constrain_to_stage_vocals("),
            source.index("audit_final_word_boundaries("),
            source.index("enrich_lines_with_syllables("),
            source.index("fuse_syllable_evidence("),
            source.index("align_notes_to_syllables("),
        ])
        self.assertEqual(sorted(positions), positions)

    def test_global_phoneme_primary_keeps_independent_safety_baseline(self):
        source = inspect.getsource(run)
        fusion_start = source.index('enter_phase("timing-candidate-fusion")')
        fusion_end = source.index('enter_phase("macro-boundary-refinement")')
        fusion = source[fusion_start:fusion_end]

        self.assertIn("baseline_id = legacy_baseline_id", fusion)
        self.assertIn(
            '"primary-hypothesis-with-independent-safety-baseline"', fusion)
        self.assertNotIn("lines = deepcopy(global_lines)", fusion)

    def test_only_the_known_stages_rebind_the_audio_candidate_list(self):
        # ``candidates`` holds AudioAlignmentCandidate objects and is still read
        # during candidate fusion, far below. A new stage that reuses the name
        # replaces them with its own tuples and fails only at runtime, with an
        # error that points nowhere near the cause.
        source = inspect.getsource(run)
        assignments = re.findall(r"^\s*candidates\s*=\s*(.{0,30})", source,
                                 re.MULTILINE)
        self.assertEqual(2, len(assignments),
                         f"Unerwartete Zuweisung an 'candidates': {assignments}")
        self.assertTrue(assignments[0].startswith("analysis_bundle.candidates"))
        self.assertTrue(assignments[1].startswith("[(full_quality"))

    def test_stage_stem_decision_is_not_confused_with_alignment_audio(self):
        source = inspect.getsource(run)
        self.assertIn(
            '"provided-library-stems-locked" if provided_stage_stems',
            source)
        self.assertIn('"awaiting-separator-candidate-evaluation"', source)
        self.assertIn('baseline_id="configured-stage-separator"', source)
        self.assertLess(source.index("build_analysis_candidates("),
                        source.index("select_stage_stem_candidate("))

    def test_provided_stage_vocals_are_loaded_before_candidate_list_exists(self):
        source = inspect.getsource(run)
        self.assertNotIn("stage_vocal_audio = candidates", source)
        provided_vocals_load = source.index(
            'stage_stems.vocals, temp_dir / "provided-stage-vocals-16k.wav"')
        candidate_list = source.index("candidates = analysis_bundle.candidates")
        self.assertLess(provided_vocals_load, candidate_list)

    def test_medleyvox_is_diagnostic_and_runs_after_final_stage_stem(self):
        source = inspect.getsource(run)
        analysis = source.index("analyze_multiple_singing_voices(")
        final_stem = source.index("stage_release_audio, stage_release_sample_rate")
        candidate_fusion = source.index("fuse_alignment_candidates(")
        self.assertLess(final_stem, analysis)
        self.assertLess(analysis, candidate_fusion)
        self.assertNotIn("lines = analyze_multiple_singing_voices(", source)


if __name__ == "__main__":
    unittest.main()
