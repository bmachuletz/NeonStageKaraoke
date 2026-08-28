import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import numpy as np

from app.phoneme_ctc_aligner import (
    _display_words,
    _attach_syllable_only_phone_paths,
    _acoustic_boundary_evidence,
    _bridge_supported_connected_ipa_blanks,
    _repair_delayed_first_word_onset,
    _repair_delayed_ipa_phrase,
    _repair_reduced_connector_after_sustain,
    _repair_clipped_final_phrase,
    _repair_repetition_tail_cross_line_transition,
    _repair_collapsed_ipa_runs,
    _repair_ipa_vocal_holes,
    _repair_final_release_from_stem_contrast,
    _repair_local_duration_inversion_pair,
    _promote_coherent_sentence_path,
    _promote_isolated_supported_internal_onsets,
    _promote_supported_local_word_intervals,
    _is_structural_vocalization_line,
    _refine_post_vocalization_lexical_restarts,
    _refine_repetition_phoneme_paths,
    _verify_aligned_word_boundaries,
    PhonemeCtcAligner,
    align_global_phoneme_path,
    annotate_phoneme_boundaries,
    audit_final_word_boundaries,
)


class PhonemeCtcBoundaryTests(unittest.TestCase):
    def test_espeak_multiword_expansion_stays_one_display_word(self):
        aligner = object.__new__(PhonemeCtcAligner)
        aligner.language_code = "en-us"
        aligner.vocabulary = {"θ": 1, "ɜː": 2, "t": 3, "i": 4,
                              "n": 5, "aɪ": 6}
        aligner.unknown = 99
        completed = SimpleNamespace(stdout="θ_ɜː_t_i n_aɪ_n")
        with patch("app.phoneme_ctc_aligner.subprocess.run",
                   return_value=completed):
            phones, ids = aligner._phones("39")

        self.assertEqual(["θ", "ɜː", "t", "i", "n", "aɪ", "n"], phones)
        self.assertNotIn(99, ids)

    def test_global_primary_builds_all_geometry_without_input_timestamps(self):
        lines = [
            SimpleNamespace(timestamp=0.0, text="Wir singen", words=[],
                            status="pending", reason=None),
            SimpleNamespace(timestamp=0.0, text="heute weiter", words=[],
                            status="pending", reason=None),
        ]
        fake = Mock()
        fake.model_id = "test-xlsr"
        fake.align_global.return_value = {
            "words": [
                {"word": "wir", "start": 12.1, "end": 12.3,
                 "confidence": .8, "phonemes": [{"phone": "v"}]},
                {"word": "singen", "start": 12.3, "end": 13.0,
                 "confidence": .7, "phonemes": [{"phone": "z"}]},
                {"word": "heute", "start": 21.0, "end": 21.5,
                 "confidence": .9, "phonemes": [{"phone": "h"}]},
                {"word": "weiter", "start": 21.5, "end": 22.2,
                 "confidence": .6, "phonemes": [{"phone": "v"}]},
            ],
            "line_ranges": [(0, 2), (2, 4)],
            "chunks": 3, "frames": 11000, "phonemes": 17,
        }

        with patch("app.phoneme_ctc_aligner.PhonemeCtcAligner", return_value=fake):
            summary = align_global_phoneme_path(
                np.zeros(30 * 16000, dtype=np.float32), lines, "de", "cpu")

        self.assertEqual(12.1, lines[0].timestamp)
        self.assertEqual(21.0, lines[1].timestamp)
        self.assertEqual("xlsr-espeak-global-primary",
                         lines[0].words[0]["timing_source"])
        self.assertEqual(4, summary["placed_words"])
        self.assertFalse(summary["uses_input_timestamps"])
        fake.close.assert_called_once()

    def test_only_extended_sung_calls_are_structural_restart_boundaries(self):
        self.assertTrue(_is_structural_vocalization_line("Wohohohohoh"))
        self.assertTrue(_is_structural_vocalization_line("lalala"))
        self.assertFalse(_is_structural_vocalization_line("Oh"))
        self.assertFalse(_is_structural_vocalization_line("Yeah"))
        self.assertFalse(_is_structural_vocalization_line("away"))

    def test_vocalization_restart_reanchors_later_lexical_occurrence(self):
        def line(text, start, end, words):
            width = (end - start) / len(words)
            return SimpleNamespace(timestamp=start, text=text, words=[
                {"word": word, "start": start + index * width,
                 "end": start + (index + 1) * width}
                for index, word in enumerate(words)
            ])

        lines = [
            line("Wohohohohoh", 10.0, 12.0, ["Wohohohohoh"]),
            line("first phrase", 12.0, 15.0, ["first", "phrase"]),
            line("second phrase here", 15.0, 17.0,
                 ["second", "phrase", "here"]),
            line("Wohohohohoh", 17.0, 21.0, ["Wohohohohoh"]),
            line("fresh lexical restart", 21.0, 24.0,
                 ["fresh", "lexical", "restart"]),
        ]
        paths = [
            [
                {"word": "first", "start": 12.1, "end": 13.2,
                 "confidence": .5, "phonemes": []},
                {"word": "phrase", "start": 13.2, "end": 15.1,
                 "confidence": .5, "phonemes": []},
                {"word": "second", "start": 15.9, "end": 16.5,
                 "confidence": .5, "phonemes": []},
                {"word": "phrase", "start": 16.5, "end": 17.1,
                 "confidence": .5, "phonemes": []},
                {"word": "here", "start": 17.1, "end": 17.3,
                 "confidence": .5, "phonemes": []},
            ],
            [
                {"word": "fresh", "start": 19.8, "end": 20.5,
                 "confidence": .5, "phonemes": []},
                {"word": "lexical", "start": 20.5, "end": 21.4,
                 "confidence": .5, "phonemes": []},
                {"word": "restart", "start": 21.4, "end": 22.5,
                 "confidence": .5, "phonemes": []},
            ],
        ]
        aligner = Mock()
        aligner.align.side_effect = (
            lambda _audio, text, _base: paths[0]
            if len(text.split()) == 5 else paths[1])
        edge = {"supported": True, "score": .8, "kind": "onset"}
        with (patch("app.phoneme_ctc_aligner.refine_ipa_phone_path"),
              patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                    return_value=edge)):
            result = _refine_post_vocalization_lexical_restarts(
                aligner, np.zeros(30 * 16000, dtype=np.float32), lines)

        # The first line after the call is context only; the drifted second
        # occurrence is the line that gets an independent lexical restart.
        self.assertEqual(12.0, lines[1].words[0]["start"])
        self.assertEqual(15.9, lines[2].words[0]["start"])
        self.assertEqual(17.3, lines[3].words[0]["start"])
        # A single lexical line after a call is itself the restart target and
        # shortens the ambiguous vocalization to its measured onset.
        self.assertEqual(19.8, lines[4].words[0]["start"])
        self.assertEqual(19.8, lines[3].words[0]["end"])
        self.assertEqual(2, result["adjusted_lines"])
        self.assertEqual(2, result["adjusted_separator_boundaries"])

    def test_vocalization_restart_uses_measured_release_and_phonetic_fit(self):
        separator = SimpleNamespace(timestamp=10.0, text="Wohohohohoh", words=[
            {"word": "Wohohohohoh", "start": 10.0, "end": 13.8},
        ])
        lexical = SimpleNamespace(timestamp=14.0, text="fresh lexical restart", words=[
            {"word": "fresh", "start": 14.0, "end": 14.6},
            {"word": "lexical", "start": 14.6, "end": 15.4},
            {"word": "restart", "start": 15.4, "end": 16.2},
        ])
        aligner = Mock()

        def path(_audio, _text, base):
            if base >= 12.0:
                return [
                    {"word": "fresh", "start": 12.8, "end": 13.3,
                     "confidence": .6, "phonemes": []},
                    {"word": "lexical", "start": 13.3, "end": 14.1,
                     "confidence": .6, "phonemes": []},
                    {"word": "restart", "start": 14.1, "end": 15.0,
                     "confidence": .6, "phonemes": []},
                ]
            return [
                {"word": "fresh", "start": 11.0, "end": 11.5,
                 "confidence": .4, "phonemes": []},
                {"word": "lexical", "start": 11.5, "end": 12.3,
                 "confidence": .4, "phonemes": []},
                {"word": "restart", "start": 12.3, "end": 13.2,
                 "confidence": .4, "phonemes": []},
            ]

        aligner.align.side_effect = path
        edge = {"supported": True, "score": .7, "kind": "onset"}
        with (patch("app.phoneme_ctc_aligner.refine_ipa_phone_path"),
              patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                    return_value=edge)):
            result = _refine_post_vocalization_lexical_restarts(
                aligner, np.zeros(20 * 16000, dtype=np.float32),
                [separator, lexical],
                stem_contrast_candidates=[{"line": 1, "to": 12.5}])

        self.assertEqual(12.8, lexical.words[0]["start"])
        self.assertEqual(12.8, separator.words[0]["end"])
        self.assertEqual(12.5, result["diagnostics"][0]["separator_release"])
        self.assertTrue(any(call.args[2] >= 12.0
                            for call in aligner.align.call_args_list))

    def test_spoken_gender_markers_do_not_create_phantom_words(self):
        self.assertEqual(
            ["sind", "wir", "romantikerinnen"],
            _display_words("Sind wir Romantiker*innen"))
        self.assertEqual(
            ["liebe", "sängerinnen"],
            _display_words("Liebe Sänger:innen"))

    def test_strong_internal_onset_survives_rejected_sentence_window(self):
        line = SimpleNamespace(timestamp=45.74, words=[
            {"word": "Träume", "start": 45.74, "end": 46.64},
            {"word": "sozusagen", "start": 46.64, "end": 49.85},
        ])
        aligned = [
            {"word": "Träume", "start": 45.42, "end": 46.583,
             "confidence": .207},
            {"word": "sozusagen", "start": 47.765, "end": 49.609,
             "confidence": .443},
        ]
        verification = [
            {"start_evidence": {"supported": True, "score": .68}},
            {"start_evidence": {"supported": True, "score": .739}},
        ]

        repairs = _promote_isolated_supported_internal_onsets(
            line, aligned, verification)

        self.assertEqual(1, len(repairs))
        self.assertEqual(47.765, line.words[1]["start"])
        self.assertEqual(46.64, line.words[0]["end"])
        self.assertEqual("isolated-supported-ipa-onset",
                         line.words[1]["timing_source"])

    def test_weak_internal_candidate_cannot_open_a_false_sustain_gap(self):
        line = SimpleNamespace(timestamp=1.0, words=[
            {"word": "one", "start": 1.0, "end": 1.4},
            {"word": "two", "start": 1.4, "end": 2.6},
        ])
        aligned = [
            {"word": "one", "start": 1.0, "end": 1.4, "confidence": .9},
            {"word": "two", "start": 2.1, "end": 2.5, "confidence": .2},
        ]
        verification = [
            {"start_evidence": {"supported": True, "score": .9}},
            {"start_evidence": {"supported": True, "score": .9}},
        ]

        repairs = _promote_isolated_supported_internal_onsets(
            line, aligned, verification)

        self.assertEqual([], repairs)
        self.assertEqual(1.4, line.words[1]["start"])

    def test_repetition_tail_uses_quiet_spectral_line_transition(self):
        previous = SimpleNamespace(timestamp=41.5, words=[
            {"word": "träum", "start": 43.876, "end": 44.196,
             "timing_source": "asr-repetition-anchor"},
            {"word": "weiter", "start": 44.196, "end": 44.704,
             "timing_source": "asr-repetition-anchor"},
            {"word": "ich", "start": 44.240, "end": 44.698,
             "timing_source": "anchor-tail-vocal-activity"},
            {"word": "hingegen", "start": 44.698, "end": 45.74,
             "timing_source": "anchor-tail-vocal-activity"},
        ])
        following = SimpleNamespace(timestamp=45.74, words=[
            {"word": "Träume", "start": 45.74, "end": 46.64},
            {"word": "sozusagen", "start": 47.765, "end": 49.85},
        ])
        aligned = [
            {"word": "träum", "start": 43.892, "end": 44.213,
             "confidence": .1},
            {"word": "weiter", "start": 44.213, "end": 44.614,
             "confidence": .14},
            {"word": "ich", "start": 44.775, "end": 44.835,
             "confidence": .01},
            {"word": "hingegen", "start": 44.835, "end": 46.06,
             "confidence": .108},
        ]
        spectral_transition = {
            "supported": False, "score": .518, "kind": "transition",
            "energy_delta_db": 3.14, "spectral_distance": .3002,
        }

        with patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                   return_value=spectral_transition):
            repairs = _repair_repetition_tail_cross_line_transition(
                np.zeros(50 * 16000, dtype=np.float32), previous, aligned,
                following)

        self.assertEqual(1, len(repairs))
        self.assertEqual(46.06, previous.words[-1]["end"])
        self.assertEqual(46.06, following.words[0]["start"])
        self.assertTrue(previous.words[-1]["phoneme_release_locked"])

    def test_repetition_tail_needs_independent_spectral_transition(self):
        previous = SimpleNamespace(timestamp=1.0, words=[
            {"word": "again", "start": 1.0, "end": 1.5,
             "timing_source": "asr-repetition-anchor"},
            {"word": "tail", "start": 1.5, "end": 2.0},
        ])
        following = SimpleNamespace(timestamp=2.0, words=[
            {"word": "next", "start": 2.0, "end": 2.7},
        ])
        aligned = [
            {"word": "again", "start": 1.0, "end": 1.5, "confidence": .5},
            {"word": "tail", "start": 1.55, "end": 2.3, "confidence": .5},
        ]

        with patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                   return_value={"supported": False, "score": .2,
                                 "spectral_distance": .12}):
            repairs = _repair_repetition_tail_cross_line_transition(
                np.zeros(3 * 16000, dtype=np.float32), previous, aligned,
                following)

        self.assertEqual([], repairs)
        self.assertEqual(2.0, previous.words[-1]["end"])

    def test_short_ctc_blank_keeps_connected_word_active_to_next_onset(self):
        line = SimpleNamespace(timestamp=29.0, words=[
            {"word": "Ansehen", "start": 29.31, "end": 29.63},
            {"word": "und", "start": 29.71, "end": 29.79},
        ])
        aligned = [
            {"word": "Ansehen", "start": 29.395, "end": 29.656,
             "confidence": .574},
            {"word": "und", "start": 29.696, "end": 29.776,
             "confidence": .384},
        ]

        result = _bridge_supported_connected_ipa_blanks(line, aligned)

        self.assertEqual(1, len(result))
        self.assertEqual(29.71, line.words[0]["end"])
        self.assertEqual(29.71, line.words[1]["start"])

    def test_real_or_unreliable_ipa_pause_is_not_bridged(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "sing", "start": 10.0, "end": 10.4},
            {"word": "again", "start": 10.5, "end": 10.9},
        ])
        aligned = [
            {"word": "sing", "start": 10.0, "end": 10.38,
             "confidence": .8},
            {"word": "again", "start": 10.49, "end": 10.9,
             "confidence": .8},
        ]

        result = _bridge_supported_connected_ipa_blanks(line, aligned)

        self.assertEqual([], result)
        self.assertEqual(10.4, line.words[0]["end"])

    def test_stem_release_needs_nearby_consonant_ipa_end(self):
        line = SimpleNamespace(timestamp=29.0, words=[
            {"word": "Macht", "start": 29.79, "end": 30.682},
        ])
        aligned = [{
            "word": "Macht", "start": 29.796, "end": 30.178,
            "confidence": .888,
            "phonemes": [{"phone": "m", "start": 29.796, "end": 29.85},
                         {"phone": "a", "start": 29.85, "end": 30.12},
                         {"phone": "t", "start": 30.15, "end": 30.178}],
        }]
        candidates = [{"line": 1, "word": "Macht", "from": 30.682,
                       "to": 30.27, "trim_ms": 412,
                       "dominance_db": -16.9}]

        result = _repair_final_release_from_stem_contrast(
            0, line, aligned, candidates)

        self.assertEqual(1, len(result))
        self.assertEqual(30.178, line.words[-1]["end"])
        self.assertEqual("high-confidence-terminal-ipa",
                         result[0]["selected_boundary"])
        self.assertTrue(line.words[-1]["phoneme_release_locked"])

    def test_stem_release_cannot_shorten_a_held_final_vowel(self):
        line = SimpleNamespace(timestamp=46.0, words=[
            {"word": "nie", "start": 47.0, "end": 49.8},
        ])
        aligned = [{
            "word": "nie", "start": 47.0, "end": 47.4,
            "confidence": .9,
            "phonemes": [{"phone": "n", "start": 47.0, "end": 47.1},
                         {"phone": "iː", "start": 47.1, "end": 47.4}],
        }]
        candidates = [{"line": 1, "word": "nie", "from": 49.8,
                       "to": 47.45, "trim_ms": 2350}]

        result = _repair_final_release_from_stem_contrast(
            0, line, aligned, candidates)

        self.assertEqual([], result)
        self.assertEqual(49.8, line.words[-1]["end"])

    def test_local_duration_inversion_repairs_only_the_shared_boundary(self):
        line = SimpleNamespace(timestamp=25.12, words=[
            {"word": "der", "start": 25.12, "end": 25.76},
            {"word": "Wand", "start": 25.76, "end": 25.814,
             "stem_contrast_release_trim_ms": 446},
        ])
        aligned = [
            {"word": "der", "start": 25.154, "end": 25.314,
             "confidence": .1971,
             "phonemes": [{"phone": "d", "start": 25.154, "end": 25.20},
                           {"phone": "ɐ", "start": 25.24, "end": 25.314}]},
            {"word": "Wand", "start": 25.455, "end": 25.937,
             "confidence": .4351,
             "phonemes": [{"phone": "v", "start": 25.455, "end": 25.50},
                           {"phone": "t", "start": 25.90, "end": 25.937}]},
        ]

        result = _repair_local_duration_inversion_pair(line, aligned)

        self.assertEqual(1, len(result))
        self.assertAlmostEqual(25.3845, line.words[0]["end"], places=3)
        self.assertEqual(line.words[0]["end"], line.words[1]["start"])
        self.assertEqual(25.814, line.words[1]["end"])
        self.assertEqual("verified-local-ipa-duration-inversion",
                         line.words[1]["timing_source"])

    def test_local_duration_inversion_needs_compensating_successor(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "held", "start": 10.0, "end": 11.0},
            {"word": "word", "start": 11.0, "end": 11.4},
        ])
        aligned = [
            {"word": "held", "start": 10.0, "end": 10.4,
             "confidence": .8, "phonemes": []},
            {"word": "word", "start": 11.0, "end": 11.4,
             "confidence": .8, "phonemes": []},
        ]

        result = _repair_local_duration_inversion_pair(line, aligned)

        self.assertEqual([], result)
        self.assertEqual(11.0, line.words[0]["end"])

    def test_duration_inversion_does_not_erase_a_real_word_pause(self):
        line = SimpleNamespace(timestamp=40.4, words=[
            {"word": "mehr", "start": 40.4, "end": 41.08},
            {"word": "relevant", "start": 41.2, "end": 41.92},
        ])
        aligned = [
            {"word": "mehr", "start": 40.452, "end": 40.713,
             "confidence": .333, "phonemes": []},
            {"word": "relevant", "start": 40.773, "end": 41.818,
             "confidence": .481, "phonemes": []},
        ]

        result = _repair_local_duration_inversion_pair(line, aligned)

        self.assertEqual([], result)
        self.assertEqual(41.08, line.words[0]["end"])
        self.assertEqual(41.2, line.words[1]["start"])

    @patch("app.phoneme_ctc_aligner._best_supported_boundary")
    def test_stretched_first_word_uses_verified_late_lexical_onset_only(
            self, boundary):
        boundary.return_value = {
            "time": 69.331, "score": .81,
            "evidence": {"supported": True, "score": .81, "kind": "onset"},
        }
        line = SimpleNamespace(timestamp=68.486, words=[
            {"word": "Denn", "start": 68.486, "end": 69.46},
            {"word": "die", "start": 69.46, "end": 69.68},
            {"word": "Lage", "start": 69.68, "end": 70.10},
            {"word": "ist", "start": 70.10, "end": 70.56},
        ])
        aligned = [
            {"word": "Denn", "start": 69.309, "end": 69.61,
             "confidence": .48, "phonemes": []},
            {"word": "die", "start": 69.65, "end": 69.71,
             "confidence": .77, "phonemes": []},
            {"word": "Lage", "start": 69.931, "end": 70.252,
             "confidence": .55, "phonemes": []},
            {"word": "ist", "start": 70.452, "end": 70.693,
             "confidence": .04, "phonemes": []},
        ]

        result = _repair_delayed_first_word_onset(
            np.zeros(16_000 * 75, dtype=np.float32), line, aligned)

        self.assertIsNotNone(result)
        self.assertEqual(69.331, line.timestamp)
        self.assertEqual(69.331, line.words[0]["start"])
        self.assertEqual(69.46, line.words[0]["end"])
        self.assertEqual(69.46, line.words[1]["start"])
        self.assertEqual("ipa-delayed-first-word-onset",
                         line.words[0]["timing_source"])

    @patch("app.phoneme_ctc_aligner._best_supported_boundary")
    def test_stretched_first_word_accepts_already_correct_following_anchors(
            self, boundary):
        boundary.return_value = {
            "time": 69.331, "score": .81,
            "evidence": {"supported": True, "score": .81, "kind": "onset"},
        }
        line = SimpleNamespace(timestamp=68.036, words=[
            {"word": "Denn", "start": 68.036, "end": 69.516},
            {"word": "die", "start": 69.636, "end": 69.876},
            {"word": "Lage", "start": 69.876, "end": 70.348},
            {"word": "bedrohlich", "start": 70.756, "end": 71.716},
        ])
        aligned = [
            {"word": "Denn", "start": 69.317, "end": 69.597,
             "confidence": .47, "phonemes": []},
            {"word": "die", "start": 69.637, "end": 69.718,
             "confidence": .74, "phonemes": []},
            {"word": "Lage", "start": 69.918, "end": 70.258,
             "confidence": .53, "phonemes": []},
            {"word": "bedrohlich", "start": 70.738, "end": 71.759,
             "confidence": .17, "phonemes": []},
        ]

        result = _repair_delayed_first_word_onset(
            np.zeros(16_000 * 75, dtype=np.float32), line, aligned)

        self.assertIsNotNone(result)
        self.assertEqual(3, result["following_anchors"])
        self.assertEqual(69.331, line.words[0]["start"])
        self.assertEqual(69.636, line.words[1]["start"])

    @patch("app.phoneme_ctc_aligner._best_supported_boundary", return_value=None)
    def test_stretched_first_word_is_unchanged_without_acoustic_onset(self, _boundary):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "first", "start": 10.0, "end": 10.9},
            {"word": "second", "start": 10.9, "end": 11.2},
            {"word": "third", "start": 11.2, "end": 11.6},
        ])
        aligned = [
            {"word": "first", "start": 10.72, "end": 10.94,
             "confidence": .8, "phonemes": []},
            {"word": "second", "start": 11.05, "end": 11.3,
             "confidence": .8, "phonemes": []},
            {"word": "third", "start": 11.4, "end": 11.7,
             "confidence": .8, "phonemes": []},
        ]

        result = _repair_delayed_first_word_onset(
            np.zeros(16_000 * 13, dtype=np.float32), line, aligned)

        self.assertIsNone(result)
        self.assertEqual(10.0, line.timestamp)
        self.assertEqual(10.0, line.words[0]["start"])

    @patch("app.phoneme_ctc_aligner._best_supported_boundary", return_value=None)
    @patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence")
    def test_short_sung_pickup_uses_verified_release_and_duration_path(
            self, evidence, _boundary):
        evidence.return_value = {
            "supported": True, "score": .80, "kind": "release",
            "energy_delta_db": -18.0, "spectral_distance": .26,
        }
        line = SimpleNamespace(timestamp=165.88, words=[
            {"word": "Ich", "start": 165.88, "end": 166.80},
            *[{"word": f"w{i}", "start": 166.9 + i * .3,
               "end": 167.1 + i * .3} for i in range(5)],
        ])
        aligned = [
            {"word": "Ich", "start": 166.742, "end": 166.902,
             "confidence": .005, "phonemes": []},
            *[{"word": f"w{i}", "start": 166.92 + i * .3,
               "end": 167.12 + i * .3, "confidence": .4, "phonemes": []}
              for i in range(5)],
        ]

        result = _repair_delayed_first_word_onset(
            np.zeros(175 * 16_000, dtype=np.float32), line, aligned)

        self.assertIsNotNone(result)
        self.assertEqual(166.742, line.words[0]["start"])
        self.assertEqual(166.902, line.words[0]["end"])
        self.assertIn("duration-informed", result["source"])

    @patch("app.phoneme_ctc_aligner._best_supported_boundary")
    def test_longer_first_word_uses_confirmed_late_onset_and_stable_release(
            self, boundary):
        boundary.return_value = {
            "time": 72.143, "score": .701,
            "evidence": {"supported": True, "score": .701, "kind": "onset"},
        }
        line = SimpleNamespace(timestamp=71.741, words=[
            {"word": "Vergessen", "start": 71.741, "end": 72.812},
            {"word": "wieder", "start": 72.812, "end": 73.452},
            {"word": "mal", "start": 73.452, "end": 73.772},
        ])
        aligned = [
            {"word": "Vergessen", "start": 72.143, "end": 72.745,
             "confidence": .525, "phonemes": []},
            {"word": "wieder", "start": 72.845, "end": 73.266,
             "confidence": .384, "phonemes": []},
            {"word": "mal", "start": 73.487, "end": 73.788,
             "confidence": .55, "phonemes": []},
        ]

        result = _repair_delayed_first_word_onset(
            np.zeros(76 * 16_000, dtype=np.float32), line, aligned)

        self.assertIsNotNone(result)
        self.assertEqual(72.143, line.words[0]["start"])
        self.assertEqual(72.812, line.words[0]["end"])

    @patch("app.phoneme_ctc_aligner._best_supported_boundary", return_value=None)
    @patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence")
    def test_legato_first_word_uses_matching_release_and_following_path(
            self, evidence, _boundary):
        evidence.return_value = {"supported": False, "score": .50,
                                 "kind": "release"}
        line = SimpleNamespace(timestamp=14.334, words=[
            {"word": "Uns", "start": 14.334, "end": 15.070},
            {"word": "verbinden", "start": 15.070, "end": 15.870},
            {"word": "alte", "start": 15.870, "end": 16.350},
            {"word": "Narben", "start": 16.510, "end": 17.230},
        ])
        aligned = [
            {"word": "Uns", "start": 14.758, "end": 15.039,
             "confidence": .324, "phonemes": []},
            {"word": "verbinden", "start": 15.059, "end": 15.903,
             "confidence": .569, "phonemes": []},
            {"word": "alte", "start": 15.963, "end": 16.264,
             "confidence": .286, "phonemes": []},
            {"word": "Narben", "start": 16.606, "end": 17.128,
             "confidence": .715, "phonemes": []},
        ]

        result = _repair_delayed_first_word_onset(
            np.zeros(19 * 16_000, dtype=np.float32), line, aligned)

        self.assertIsNotNone(result)
        self.assertEqual(14.758, line.words[0]["start"])
        self.assertEqual(15.039, line.words[0]["end"])
        self.assertIn("legato-release-geometry", result["source"])

    def test_rejected_outer_edge_can_still_supply_internal_syllable_phones(self):
        line = SimpleNamespace(words=[{
            "word": "sozusagen", "start": 10.0, "end": 12.0,
        }])
        aligned = [{
            "word": "sozusagen", "start": 10.004, "end": 11.81,
            "confidence": .49,
            "phonemes": [
                {"phone": "z", "start": 10.004, "end": 10.08},
                {"phone": "o", "start": 10.08, "end": 10.30},
                {"phone": "ts", "start": 10.31, "end": 10.39},
                {"phone": "u", "start": 10.39, "end": 10.67},
                {"phone": "z", "start": 10.72, "end": 10.79},
                {"phone": "a", "start": 10.79, "end": 11.10},
                {"phone": "g", "start": 11.13, "end": 11.20},
                {"phone": "ə", "start": 11.20, "end": 11.65},
                {"phone": "n", "start": 11.65, "end": 11.81},
            ],
        }]

        attached = _attach_syllable_only_phone_paths(line, aligned)

        self.assertEqual(1, attached)
        self.assertNotIn("phonemes", line.words[0])
        self.assertEqual("xlsr-espeak-ctc-internal-only",
                         line.words[0]["syllable_phoneme_source"])

    def test_distant_ipa_occurrence_is_not_used_for_syllables(self):
        line = SimpleNamespace(words=[{
            "word": "weiter", "start": 10.0, "end": 11.0,
        }])
        aligned = [{
            "word": "weiter", "start": 10.35, "end": 11.1,
            "confidence": .9,
            "phonemes": [
                {"phone": "v", "start": 10.35, "end": 10.4},
                {"phone": "aɪ", "start": 10.4, "end": 10.7},
            ],
        }]

        attached = _attach_syllable_only_phone_paths(line, aligned)

        self.assertEqual(0, attached)
        self.assertNotIn("syllable_phonemes", line.words[0])

    def test_promotes_a_coherent_sentence_path_far_from_bad_baseline(self):
        previous = SimpleNamespace(words=[{"word": "before", "start": 8.0, "end": 8.8}])
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "we", "start": 10.0, "end": 10.8},
            {"word": "sing", "start": 10.8, "end": 11.6},
            {"word": "now", "start": 11.6, "end": 12.0},
        ])
        following = SimpleNamespace(words=[{"word": "after", "start": 13.0, "end": 13.4}])
        aligned = [
            {"word": "we", "start": 9.4, "end": 9.65, "confidence": .45},
            {"word": "sing", "start": 9.72, "end": 10.18, "confidence": .08},
            {"word": "now", "start": 10.22, "end": 10.7, "confidence": .36},
        ]
        verification = [{
            "start_evidence": {"supported": True, "score": .75},
            "end_evidence": {"supported": False, "score": .2},
        }, {
            "start_evidence": {"supported": True, "score": .71},
            "end_evidence": {"supported": False, "score": .1},
        }, {
            "start_evidence": {"supported": False, "score": .2},
            "end_evidence": {"supported": True, "score": .68},
        }]

        result = _promote_coherent_sentence_path(
            [previous, line, following], 1, aligned, verification)

        self.assertIsNotNone(result)
        self.assertEqual(9.4, line.words[0]["start"])
        self.assertEqual(10.18, line.words[1]["end"])
        self.assertEqual("coherent-sentence-ipa-path", line.words[1]["timing_source"])

    def test_long_sentence_is_not_replaced_from_two_acoustic_edges(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": f"word{index}", "start": 10.0 + index * .42,
             "end": 10.36 + index * .42}
            for index in range(9)
        ])
        aligned = [
            {"word": f"word{index}", "start": 10.22 + index * .42,
             "end": 10.58 + index * .42, "confidence": .45}
            for index in range(9)
        ]
        verification = [{
            "start_evidence": {"supported": index < 2, "score": .75 if index < 2 else .2},
            "end_evidence": {"supported": False, "score": .2},
        } for index in range(9)]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNone(result)
        self.assertEqual(10.0 + 8 * .42, line.words[-1]["start"])

    def test_severely_deformed_long_sentence_keeps_majority_escape_hatch(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": f"word{index}", "start": 10.0 + index * .65,
             "end": 10.58 + index * .65}
            for index in range(9)
        ])
        aligned = [
            {"word": f"word{index}", "start": 10.05 + index * .35,
             "end": 10.32 + index * .35, "confidence": .45}
            for index in range(9)
        ]
        verification = [{
            "start_evidence": {"supported": index < 3, "score": .75 if index < 3 else .2},
            "end_evidence": {"supported": False, "score": .2},
        } for index in range(9)]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNotNone(result)
        self.assertEqual("majority-boundary-disagreement",
                         result["promotion_reason"])

    def test_coherent_late_phrase_can_override_a_stale_lrc_gap(self):
        previous = SimpleNamespace(words=[
            {"word": "before", "start": 57.8, "end": 58.7},
        ])
        line = SimpleNamespace(timestamp=58.8, words=[
            {"word": "wollen", "start": 58.8, "end": 59.8},
            {"word": "wir", "start": 59.8, "end": 60.4},
            {"word": "noch", "start": 60.4, "end": 61.1},
            {"word": "gehen", "start": 61.1, "end": 62.4},
        ])
        following = SimpleNamespace(words=[
            {"word": "after", "start": 66.4, "end": 66.8},
        ])
        aligned = [
            {"word": "wollen", "start": 60.62, "end": 60.92,
             "confidence": .08},
            {"word": "wir", "start": 61.01, "end": 61.22,
             "confidence": .42},
            {"word": "noch", "start": 61.36, "end": 61.78,
             "confidence": .38},
            {"word": "gehen", "start": 61.88, "end": 62.25,
             "confidence": .51},
        ]
        verification = [{
            "start_evidence": {"supported": index == 1, "score": .82},
            "end_evidence": {"supported": index in (2, 3), "score": .72},
        } for index in range(len(aligned))]
        onset = {"time": 60.59, "score": .86,
                 "evidence": {"supported": True, "score": .86}}

        with patch("app.phoneme_ctc_aligner._best_supported_boundary",
                   return_value=onset):
            result = _promote_coherent_sentence_path(
                [previous, line, following], 1, aligned, verification,
                audio=np.zeros(70 * 16000, dtype=np.float32))

        self.assertIsNotNone(result)
        self.assertEqual("coherent-late-phrase-rescue",
                         result["promotion_reason"])
        self.assertEqual(60.59, line.words[0]["start"])
        self.assertEqual(61.01, line.words[1]["start"])

    def test_late_phrase_without_independent_onset_is_not_promoted(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "one", "start": 10.0, "end": 10.5},
            {"word": "two", "start": 10.5, "end": 11.0},
            {"word": "three", "start": 11.0, "end": 12.0},
        ])
        aligned = [
            {"word": "one", "start": 10.6, "end": 10.9, "confidence": .5},
            {"word": "two", "start": 10.65, "end": 11.1, "confidence": .5},
            {"word": "three", "start": 11.15, "end": 12.1,
             "confidence": .5},
        ]
        verification = [{
            "start_evidence": {"supported": False, "score": .2},
            "end_evidence": {"supported": False, "score": .2},
        } for _ in aligned]

        with patch("app.phoneme_ctc_aligner._best_supported_boundary",
                   return_value=None):
            result = _promote_coherent_sentence_path(
                [line], 0, aligned, verification,
                audio=np.zeros(13 * 16000, dtype=np.float32))

        self.assertIsNone(result)
        self.assertEqual(10.0, line.words[0]["start"])

    def test_single_bad_sustain_does_not_replace_an_otherwise_stable_line(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "the", "start": 10.0, "end": 10.3},
            {"word": "line", "start": 10.4, "end": 10.8},
            {"word": "holds", "start": 10.9, "end": 12.4},
        ])
        aligned = [
            {"word": "the", "start": 10.01, "end": 10.29, "confidence": .7},
            {"word": "line", "start": 10.41, "end": 10.79, "confidence": .7},
            {"word": "holds", "start": 10.91, "end": 11.25, "confidence": .7},
        ]
        verification = [{
            "start_evidence": {"supported": True, "score": .8},
            "end_evidence": {"supported": True, "score": .8},
        } for _ in aligned]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNone(result)
        self.assertEqual(12.4, line.words[-1]["end"])

    def test_local_duration_inversion_promotes_the_coherent_sentence_path(self):
        line = SimpleNamespace(timestamp=19.586, words=[
            {"word": "Du", "start": 19.586, "end": 19.666},
            {"word": "träumst", "start": 19.666, "end": 20.146},
            {"word": "von", "start": 20.146, "end": 20.386},
            {"word": "Sonne", "start": 20.386, "end": 20.706},
            {"word": "und", "start": 20.706, "end": 21.265},
            {"word": "Strand", "start": 21.265, "end": 21.340},
        ])
        aligned = [
            {"word": "Du", "start": 19.628, "end": 19.689,
             "confidence": .1504},
            {"word": "träumst", "start": 19.769, "end": 20.091,
             "confidence": .0818},
            {"word": "von", "start": 20.111, "end": 20.332,
             "confidence": .6449},
            {"word": "Sonne", "start": 20.393, "end": 20.614,
             "confidence": .5453},
            {"word": "und", "start": 20.634, "end": 20.775,
             "confidence": .2846},
            {"word": "Strand", "start": 20.815, "end": 21.257,
             "confidence": .3564},
        ]
        verification = [{
            "start_evidence": {"supported": index in (0, 2), "score": .66},
            "end_evidence": {"supported": index == 4, "score": .62},
        } for index in range(len(aligned))]

        following = SimpleNamespace(timestamp=22.2, words=[
            {"word": "next", "start": 22.3, "end": 22.7},
        ])
        result = _promote_coherent_sentence_path(
            [line, following], 0, aligned, verification)

        self.assertIsNotNone(result)
        self.assertEqual("local-duration-inversion-rescue",
                         result["promotion_reason"])
        self.assertEqual([4, 5], result["duration_inversion"]["word_indices"])
        self.assertEqual(20.634, line.words[4]["start"])
        self.assertEqual(20.815, line.words[5]["start"])
        self.assertEqual(21.257, line.words[5]["end"])

    def test_single_wrong_duration_without_compensating_neighbour_is_not_promoted(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "one", "start": 10.0, "end": 10.3},
            {"word": "held", "start": 10.3, "end": 11.3},
            {"word": "word", "start": 11.3, "end": 11.7},
        ])
        aligned = [
            {"word": "one", "start": 10.01, "end": 10.29,
             "confidence": .7},
            {"word": "held", "start": 10.31, "end": 10.7,
             "confidence": .7},
            {"word": "word", "start": 11.31, "end": 11.69,
             "confidence": .7},
        ]
        verification = [{
            "start_evidence": {"supported": True, "score": .8},
            "end_evidence": {"supported": True, "score": .8},
        } for _ in aligned]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNone(result)
        self.assertEqual(11.3, line.words[1]["end"])

    def test_distant_sentence_requires_a_verified_strong_first_word(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "denn", "start": 10.0, "end": 10.8},
            {"word": "die", "start": 10.8, "end": 11.2},
            {"word": "lage", "start": 11.2, "end": 12.0},
        ])
        aligned = [
            {"word": "denn", "start": 11.0, "end": 11.25, "confidence": .43},
            {"word": "die", "start": 11.3, "end": 11.5, "confidence": .7},
            {"word": "lage", "start": 11.55, "end": 11.9, "confidence": .7},
        ]
        verification = [{
            "start_evidence": {"supported": True, "score": .80},
            "end_evidence": {"supported": False, "score": .2},
        } for _ in aligned]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNone(result)
        self.assertEqual(10.0, line.words[0]["start"])

    def test_verified_strong_onset_repairs_a_multi_second_first_word_error(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "von", "start": 10.0, "end": 12.7},
            {"word": "staendiger", "start": 12.7, "end": 13.4},
            {"word": "verfuegbarkeit", "start": 13.4, "end": 14.4},
        ])
        aligned = [
            {"word": "von", "start": 12.4, "end": 12.55, "confidence": .82},
            {"word": "staendiger", "start": 12.65, "end": 13.35, "confidence": .5},
            {"word": "verfuegbarkeit", "start": 13.45, "end": 14.3,
             "confidence": .5},
        ]
        verification = [{
            "start_evidence": {"supported": True, "score": .86},
            "end_evidence": {"supported": False, "score": .2},
        }, {
            "start_evidence": {"supported": False, "score": .2},
            "end_evidence": {"supported": True, "score": .7},
        }, {
            "start_evidence": {"supported": False, "score": .2},
            "end_evidence": {"supported": False, "score": .2},
        }]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNotNone(result)
        self.assertEqual("verified-displaced-onset", result["promotion_reason"])
        self.assertEqual(12.4, line.words[0]["start"])

    def test_confirmed_phrase_repairs_a_weak_first_word_stretched_to_its_onset(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "ich", "start": 10.0, "end": 10.92},
            {"word": "bin", "start": 11.06, "end": 11.28},
            {"word": "kein", "start": 11.32, "end": 11.65},
            {"word": "mensch", "start": 11.7, "end": 12.1},
        ])
        aligned = [
            {"word": "ich", "start": 10.92, "end": 11.08, "confidence": .01},
            {"word": "bin", "start": 11.18, "end": 11.35, "confidence": .42},
            {"word": "kein", "start": 11.4, "end": 11.7, "confidence": .5},
            {"word": "mensch", "start": 11.75, "end": 12.12, "confidence": .6},
        ]
        verification = [{
            "start_evidence": {"supported": False, "score": .3},
            "end_evidence": {"supported": True, "score": .75},
        }, *[{
            "start_evidence": {"supported": True, "score": .7},
            "end_evidence": {"supported": False, "score": .2},
        } for _ in range(3)]]

        result = _promote_coherent_sentence_path(
            [line], 0, aligned, verification)

        self.assertIsNotNone(result)
        self.assertEqual("collapsed-first-word-gap-rescue",
                         result["promotion_reason"])
        self.assertEqual(10.92, line.words[0]["start"])

    def test_tiny_decoder_overlap_does_not_reject_coherent_sentence(self):
        line = SimpleNamespace(timestamp=10.0, words=[
            {"word": "na", "start": 10.0, "end": 10.8,
             "stage_vocal_release_trim_ms": 800,
             "pre_stage_vocal_end": 11.6},
            {"word": "klar", "start": 10.8, "end": 11.4},
            {"word": "weiter", "start": 11.4, "end": 12.0},
        ])
        aligned = [
            {"word": "na", "start": 10.8, "end": 10.983, "confidence": .35},
            {"word": "klar", "start": 10.980, "end": 11.3, "confidence": .45},
            {"word": "weiter", "start": 11.34, "end": 11.8, "confidence": .5},
        ]
        verification = [{
            "start_evidence": {"supported": True, "score": .72},
            "end_evidence": {"supported": True, "score": .7},
        } for _ in aligned]

        result = _promote_coherent_sentence_path([line], 0, aligned, verification)

        self.assertIsNotNone(result)
        self.assertEqual(1, result["normalized_rounding_overlaps"])
        self.assertLessEqual(line.words[0]["end"], line.words[1]["start"])
        self.assertNotIn("stage_vocal_release_trim_ms", line.words[0])
        self.assertNotIn("pre_stage_vocal_end", line.words[0])

    def test_quiet_gap_confirms_low_score_consonant_word_interval(self):
        sample_rate = 16000
        audio = np.zeros(3 * sample_rate, dtype=np.float32)
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "du", "start": .5, "end": .75},
            {"word": "träumst", "start": .75, "end": 1.60},
            {"word": "von", "start": 2.25, "end": 2.50},
        ])
        aligned = [
            {"word": "du", "start": .5, "end": .73, "confidence": .4,
             "phonemes": [{"phone": "u", "start": .55, "end": .73}]},
            {"word": "träumst", "start": .84, "end": 1.41, "confidence": .074,
             "phonemes": [{"phone": "ɔʏ", "start": .9, "end": 1.2},
                          {"phone": "t", "start": 1.35, "end": 1.41}]},
            {"word": "von", "start": 2.22, "end": 2.48, "confidence": .5,
             "phonemes": [{"phone": "v", "start": 2.22, "end": 2.3}]},
        ]
        verification = [{"start_evidence": {"supported": False, "score": .1}},
                        {"start_evidence": {"supported": True, "score": .74}},
                        {"start_evidence": {"supported": True, "score": .8}}]

        result = _promote_supported_local_word_intervals(
            audio, line, aligned, verification)

        self.assertEqual(1, len(result))
        self.assertEqual(.84, line.words[1]["start"])
        self.assertEqual(1.41, line.words[1]["end"])
        self.assertTrue(line.words[1]["phoneme_release_locked"])

    def test_close_confident_ipa_trims_overlong_consonant_before_quiet_gap(self):
        sample_rate = 16000
        audio = np.zeros(36 * sample_rate, dtype=np.float32)
        line = SimpleNamespace(timestamp=30.2, words=[
            {"word": "Von", "start": 30.2, "end": 30.6},
            {"word": "schnellen", "start": 30.7, "end": 32.0},
            {"word": "Autos", "start": 32.154, "end": 33.144},
            {"word": "und", "start": 33.65, "end": 33.9},
        ])
        aligned = [
            {"word": "Von", "start": 30.2, "end": 30.6, "confidence": .5,
             "phonemes": [{"phone": "n", "start": 30.5, "end": 30.6}]},
            {"word": "schnellen", "start": 30.7, "end": 32.0, "confidence": .5,
             "phonemes": [{"phone": "n", "start": 31.9, "end": 32.0}]},
            {"word": "Autos", "start": 32.115, "end": 32.776,
             "confidence": .43,
             "phonemes": [{"phone": "aʊ", "start": 32.12, "end": 32.6},
                          {"phone": "s", "start": 32.68, "end": 32.776}]},
            {"word": "und", "start": 33.657, "end": 33.9, "confidence": .5,
             "phonemes": [{"phone": "ʊ", "start": 33.657, "end": 33.8}]},
        ]
        verification = [
            {"start_evidence": {"supported": False, "score": 0.0}}
            for _ in aligned]

        result = _promote_supported_local_word_intervals(
            audio, line, aligned, verification)

        self.assertEqual(1, len(result))
        self.assertEqual(32.115, line.words[2]["start"])
        self.assertEqual(32.776, line.words[2]["end"])
        self.assertEqual("close-high-confidence-ipa", result[0]["onset_support"])

    def test_continuous_repetition_path_ignores_display_line_split(self):
        lines = [SimpleNamespace(words=[
            {"word": "dream", "start": 10.0, "end": 10.2},
            {"word": "again", "start": 10.2, "end": 10.5},
        ]), SimpleNamespace(words=[
            {"word": "dream", "start": 10.5, "end": 10.8},
            {"word": "again", "start": 10.8, "end": 11.1},
            {"word": "after", "start": 11.2, "end": 11.5},
        ])]
        pair = {"expected": {"start": 0, "end": 4},
                "audio_start": 10.0, "audio_end": 11.1,
                "trailing_words_in_line": 1}
        aligner = Mock()
        aligner.align.return_value = [
            {"word": "dream", "start": 10.05, "end": 10.25, "confidence": .2},
            {"word": "again", "start": 10.27, "end": 10.48, "confidence": .2},
            {"word": "dream", "start": 10.72, "end": 10.9, "confidence": .2},
            {"word": "again", "start": 10.92, "end": 11.08, "confidence": .2},
            {"word": "after", "start": 11.3, "end": 11.55, "confidence": .2},
        ]

        result = _refine_repetition_phoneme_paths(
            aligner, np.zeros(13 * 16000, dtype=np.float32), lines, [pair])

        self.assertEqual(1, result["accepted_blocks"])
        self.assertEqual(10.72, pair["audio_words"][1]["end"])
        self.assertEqual(11.3, pair["audio_words"][-1]["end"])
        self.assertEqual("continuous-phrase-ipa-repetition-v1", pair["timestamp_method"])

    def test_continuous_repetition_path_never_overlaps_trailing_context(self):
        lines = [SimpleNamespace(words=[
            {"word": "dream", "start": 10.0, "end": 10.5},
            {"word": "again", "start": 10.5, "end": 11.1},
            {"word": "after", "start": 11.1, "end": 11.5},
        ])]
        pair = {"expected": {"start": 0, "end": 2},
                "audio_start": 10.0, "audio_end": 11.1,
                "trailing_words_in_line": 1}
        aligner = Mock()
        aligner.align.return_value = [
            {"word": "dream", "start": 10.0, "end": 10.45, "confidence": .3},
            {"word": "again", "start": 10.5, "end": 11.13, "confidence": .3},
            {"word": "after", "start": 11.08, "end": 11.5, "confidence": .3},
        ]

        result = _refine_repetition_phoneme_paths(
            aligner, np.zeros(13 * 16000, dtype=np.float32), lines, [pair])

        self.assertEqual(1, result["accepted_blocks"])
        self.assertEqual(11.08, pair["audio_words"][-1]["end"])

    def test_strong_onset_repetition_path_can_override_a_false_stable_anchor(self):
        lines = [SimpleNamespace(words=[
            *[{"word": word, "start": 10.0 + index * .3,
               "end": 10.25 + index * .3}
              for index, word in enumerate(["dream", "again"] * 4)],
            {"word": "after", "start": 12.6, "end": 13.0},
        ])]
        pair = {"expected": {"start": 0, "end": 8, "unit_words": 2},
                "audio_start": 8.0, "audio_end": 10.0,
                "trailing_words_in_line": 1}
        aligner = Mock()
        aligner.align.return_value = [
            *[{"word": word, "start": 10.0 + index * .3,
               "end": 10.24 + index * .3, "confidence": .15}
              for index, word in enumerate(["dream", "again"] * 4)],
            {"word": "after", "start": 12.5, "end": 13.0,
             "confidence": .3},
        ]

        with patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                   return_value={"supported": True, "score": .75}):
            result = _refine_repetition_phoneme_paths(
                aligner, np.zeros(15 * 16000, dtype=np.float32), lines, [pair])

        self.assertEqual(1, result["accepted_blocks"])
        self.assertTrue(result["diagnostics"][0]["stable_context_overridden"])
        self.assertEqual(10.0, pair["audio_start"])

    def test_final_audit_detects_a_boundary_changed_after_verification(self):
        proof = {
            "verified": True, "current_start": 1.0, "current_end": 1.5,
        }
        lines = [SimpleNamespace(words=[{
            "word": "Test", "start": 1.0, "end": 1.38,
            "stage_vocal_release_trim_ms": 120,
            "phoneme_word_verification": proof,
        }])]

        report = audit_final_word_boundaries(lines)

        self.assertEqual(0, report["verified_words"])
        self.assertEqual(1, report["hard_constrained_words"])
        self.assertEqual(0, report["geometry_violations"])

    def test_sentence_verification_checks_every_word_and_release(self):
        sample_rate = 16000
        audio = np.zeros(2 * sample_rate, dtype=np.float32)
        # Two acoustically distinct word islands with a clean final release.
        first_time = np.arange(int(.35 * sample_rate), dtype=np.float32) / sample_rate
        second_time = np.arange(int(.40 * sample_rate), dtype=np.float32) / sample_rate
        audio[int(.40 * sample_rate):int(.75 * sample_rate)] = (
            .28 * np.sin(2 * np.pi * 180 * first_time))
        audio[int(.85 * sample_rate):int(1.25 * sample_rate)] = (
            .28 * np.sin(2 * np.pi * 310 * second_time))
        line = SimpleNamespace(words=[
            {"word": "Hallo", "start": .4, "end": .75},
            {"word": "Welt", "start": .85, "end": 1.25},
        ])
        aligned = [
            {"word": "hallo", "start": .4, "end": .75,
             "confidence": .8, "phonemes": []},
            {"word": "welt", "start": .85, "end": 1.25,
             "confidence": .8, "phonemes": []},
        ]

        result = _verify_aligned_word_boundaries(audio, line, aligned, .18)

        self.assertEqual(2, len(result))
        self.assertTrue(result[0]["start_evidence"]["supported"])
        self.assertTrue(result[1]["end_evidence"]["supported"])
        self.assertTrue(all(item["verified"] for item in result))

    def test_clipped_name_tail_disproves_false_successor_onset(self):
        previous = SimpleNamespace(timestamp=1.0, words=[
            {"word": "left", "start": 1.0, "end": 1.4},
            {"word": "San", "start": 1.4, "end": 1.66},
            {"word": "Fran", "start": 1.66, "end": 1.8,
             "timing_source": "overlap-display-lane-fallback"},
        ])
        following = SimpleNamespace(timestamp=1.8, words=[
            {"word": "The", "start": 1.8, "end": 2.3},
            {"word": "tire", "start": 3.0, "end": 3.3},
        ])
        aligned = [
            {"word": "left", "start": 1.0, "end": 1.4,
             "confidence": .8, "phonemes": []},
            {"word": "san", "start": 1.78, "end": 2.12,
             "confidence": .76, "phonemes": []},
            {"word": "fran", "start": 2.18, "end": 2.72,
             "confidence": .79, "phonemes": []},
        ]
        onset = {"time": 1.62, "score": .8,
                 "evidence": {"supported": True, "score": .8}}
        successor = {"time": 2.88,
                     "evidence": {"supported": True, "score": .82}}

        with patch("app.phoneme_ctc_aligner._best_supported_boundary",
                   return_value=onset), \
                patch("app.phoneme_ctc_aligner._first_supported_onset",
                      return_value=successor):
            repairs = _repair_clipped_final_phrase(
                np.zeros(4 * 16000, dtype=np.float32), previous, aligned, following)

        self.assertEqual(1, len(repairs))
        self.assertEqual(1.62, previous.words[-2]["start"])
        self.assertEqual(1.88, previous.words[-1]["start"])
        self.assertEqual(2.75, previous.words[-1]["end"])
        self.assertEqual(2.88, following.words[0]["start"])
        self.assertEqual(2.88, following.timestamp)
        self.assertLess(previous.words[-1]["end"], following.words[0]["start"])

    def test_single_clipped_final_word_uses_ipa_end_and_real_successor_onset(self):
        previous = SimpleNamespace(timestamp=50.4, words=[
            {"word": "Von", "start": 50.4, "end": 50.65},
            {"word": "brennenden", "start": 50.65, "end": 52.18},
            {"word": "Barrikaden", "start": 52.42, "end": 53.95,
             "timing_source": "overlap-display-lane-fallback"},
        ])
        following = SimpleNamespace(timestamp=53.95, words=[
            {"word": "Einem", "start": 53.95, "end": 54.46},
            {"word": "Volk", "start": 55.39, "end": 55.71},
        ])
        aligned = [
            {"word": "Von", "start": 50.42, "end": 50.64,
             "confidence": .8},
            {"word": "brennenden", "start": 50.70, "end": 51.89,
             "confidence": .5},
            {"word": "Barrikaden", "start": 52.47, "end": 54.296,
             "confidence": .53},
        ]
        successor = {"time": 54.79,
                     "evidence": {"supported": True, "score": .81}}

        with patch("app.phoneme_ctc_aligner._first_supported_onset",
                   return_value=successor):
            repairs = _repair_clipped_final_phrase(
                np.zeros(60 * 16000, dtype=np.float32), previous, aligned,
                following)

        self.assertEqual(1, len(repairs))
        self.assertEqual(54.296, previous.words[-1]["end"])
        self.assertEqual(54.79, following.words[0]["start"])
        self.assertEqual(55.30, following.words[0]["end"])
        self.assertEqual("single-final-ipa-cross-line-acoustic-repair-v1",
                         repairs[0]["source"])

    def test_single_clipped_final_word_rejects_distant_ipa_onset(self):
        previous = SimpleNamespace(timestamp=1.0, words=[
            {"word": "wrong", "start": 1.0, "end": 1.5,
             "timing_source": "overlap-display-lane-fallback"},
        ])
        following = SimpleNamespace(timestamp=1.5, words=[
            {"word": "next", "start": 1.5, "end": 2.0},
            {"word": "word", "start": 2.3, "end": 2.7},
        ])
        aligned = [{"word": "wrong", "start": 1.3, "end": 1.9,
                    "confidence": .9}]

        repairs = _repair_clipped_final_phrase(
            np.zeros(3 * 16000, dtype=np.float32), previous, aligned, following)

        self.assertEqual([], repairs)
        self.assertEqual(1.5, previous.words[-1]["end"])

    def test_reduced_and_after_sustain_does_not_own_the_held_vowel(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "away", "start": .5, "end": 2.2,
             "acoustic_end": 2.08, "sustain_release_confidence": .82},
            {"word": "and", "start": 2.68, "end": 3.88},
            {"word": "play", "start": 3.90, "end": 7.3},
        ])
        aligned = [
            {"word": "away", "start": .55, "end": 2.17,
             "confidence": .3, "phonemes": []},
            {"word": "and", "start": 3.79, "end": 3.95,
             "confidence": .04, "phonemes": []},
            {"word": "play", "start": 4.03, "end": 4.2,
             "confidence": .27, "phonemes": []},
        ]

        repairs = _repair_reduced_connector_after_sustain(
            np.ones(8 * 16000, dtype=np.float32) * .05, line, aligned)

        self.assertEqual(1, len(repairs))
        self.assertEqual(3.69, line.words[0]["end"])
        self.assertEqual(3.69, line.words[1]["start"])
        self.assertEqual(3.79, line.words[1]["end"])
        self.assertEqual(3.79, line.words[2]["start"])
        self.assertEqual("ipa-reduced-connector-repair",
                         line.words[2]["timing_source"])

    def test_delayed_internal_phrase_survives_weak_final_phone(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "there", "start": .5, "end": .7},
            {"word": "we", "start": .7, "end": .9},
            {"word": "were", "start": .9, "end": 1.2},
            {"word": "standing", "start": 1.2, "end": 1.7},
            {"word": "around", "start": 1.7, "end": 2.8},
        ])
        aligned = [
            {"word": "there", "start": .5, "end": .7, "confidence": .8,
             "phonemes": []},
            {"word": "we", "start": .7, "end": .9, "confidence": .8,
             "phonemes": []},
            {"word": "were", "start": .9, "end": 1.2, "confidence": .5,
             "phonemes": []},
            {"word": "standing", "start": 1.62, "end": 2.32,
             "confidence": .76, "phonemes": [
                 {"phone": "s", "start": 1.62, "end": 1.68},
                 {"phone": "æ", "start": 1.72, "end": 2.0}]},
            # The release score may be weak; its onset still gives the inner
            # boundary while the existing end remains authoritative.
            {"word": "around", "start": 2.37, "end": 2.96,
             "confidence": .19, "phonemes": [
                 {"phone": "ɚ", "start": 2.37, "end": 2.45}]},
        ]
        audio = np.zeros(4 * 16000, dtype=np.float32)
        time = np.arange(int(.35 * 16000), dtype=np.float32) / 16000
        audio[int(1.2 * 16000):int(1.55 * 16000)] = (
            .22 * np.sin(2 * np.pi * 180 * time))
        time = np.arange(int(1.15 * 16000), dtype=np.float32) / 16000
        audio[int(1.65 * 16000):int(2.8 * 16000)] = (
            .3 * np.sin(2 * np.pi * 260 * time))

        repairs = _repair_delayed_ipa_phrase(audio, line, aligned)

        self.assertEqual(1, len(repairs))
        self.assertEqual([3, 4], repairs[0]["word_indices"])
        self.assertGreaterEqual(line.words[3]["start"], 1.60)
        self.assertEqual(2.37, line.words[3]["end"])
        self.assertEqual(2.37, line.words[4]["start"])
        self.assertEqual(2.8, line.words[4]["end"])
        self.assertEqual("ipa-delayed-phrase-repair",
                         line.words[3]["timing_source"])

    def test_adjacent_collapsed_words_expand_into_supported_ipa_phrase(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "Autos", "start": .5, "end": .9},
            {"word": "und", "start": 1.0, "end": 1.08},
            {"word": "einer", "start": 1.2, "end": 1.28},
            {"word": "Yacht", "start": 1.8, "end": 2.2},
        ])
        aligned = [
            {"word": "autos", "start": .5, "end": .9, "confidence": .7,
             "phonemes": [{"phone": "s", "start": .8, "end": .9}]},
            {"word": "und", "start": 1.0, "end": 1.35, "confidence": .3,
             "phonemes": [{"phone": "ʊ", "start": 1.0, "end": 1.2},
                          {"phone": "n", "start": 1.2, "end": 1.35}]},
            # A sung vowel can have a weak IPA label score.  The atomic run is
            # still bounded by a strong following word and real vocal energy.
            {"word": "einer", "start": 1.38, "end": 1.70, "confidence": .02,
             "phonemes": [{"phone": "aɪ", "start": 1.38, "end": 1.58},
                          {"phone": "n", "start": 1.58, "end": 1.70}]},
            {"word": "yacht", "start": 1.8, "end": 2.2, "confidence": .8,
             "phonemes": [{"phone": "j", "start": 1.8, "end": 1.9}]},
        ]
        sample_rate = 16000
        audio = np.zeros(3 * sample_rate, dtype=np.float32)
        time = np.arange(int(.7 * sample_rate), dtype=np.float32) / sample_rate
        audio[sample_rate:int(1.7 * sample_rate)] = (
            .3 * np.sin(2 * np.pi * 220 * time))

        repairs = _repair_collapsed_ipa_runs(audio, line, aligned)

        self.assertEqual(1, len(repairs))
        self.assertEqual([1, 2], repairs[0]["word_indices"])
        self.assertEqual(1.0, line.words[1]["start"])
        self.assertEqual(1.35, line.words[1]["end"])
        self.assertEqual(1.38, line.words[2]["start"])
        self.assertEqual(1.70, line.words[2]["end"])
        self.assertEqual("ipa-collapsed-run-repair",
                         line.words[2]["timing_source"])

    def test_single_short_function_word_remains_protected(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "ich", "start": .5, "end": .9},
            {"word": "und", "start": 1.0, "end": 1.08},
            {"word": "du", "start": 1.5, "end": 1.8},
        ])
        aligned = [
            {"word": "ich", "start": .5, "end": .9, "confidence": .8,
             "phonemes": []},
            {"word": "und", "start": 1.0, "end": 1.35, "confidence": .8,
             "phonemes": []},
            {"word": "du", "start": 1.5, "end": 1.8, "confidence": .8,
             "phonemes": []},
        ]
        audio = np.ones(3 * 16000, dtype=np.float32) * .1

        repairs = _repair_collapsed_ipa_runs(audio, line, aligned)

        self.assertEqual([], repairs)
        self.assertEqual(1.08, line.words[1]["end"])

    def test_occupied_gap_is_closed_by_anchored_ipa_boundaries(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "schnellen", "start": .5, "end": .82},
            {"word": "Autos", "start": 1.30, "end": 1.88},
        ])
        aligned = [
            {"word": "schnellen", "start": .56, "end": 1.063,
             "confidence": .65, "phonemes": []},
            {"word": "autos", "start": 1.103, "end": 1.784,
             "confidence": .18, "phonemes": []},
        ]
        audio = np.zeros(3 * 16000, dtype=np.float32)
        time = np.arange(int(.48 * 16000), dtype=np.float32) / 16000
        audio[int(.82 * 16000):int(1.30 * 16000)] = (
            .3 * np.sin(2 * np.pi * 220 * time))

        repairs = _repair_ipa_vocal_holes(audio, line, aligned)

        self.assertEqual(1, len(repairs))
        self.assertEqual(1.063, line.words[0]["end"])
        self.assertEqual(1.103, line.words[1]["start"])
        self.assertEqual("ipa-vocal-hole-repair",
                         line.words[0]["timing_source"])
        self.assertEqual(.5, line.words[0]["start"])
        self.assertEqual(1.88, line.words[1]["end"])

    def test_silent_gap_is_not_closed(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "Autos", "start": .5, "end": .9},
            {"word": "und", "start": 1.6, "end": 1.9},
        ])
        aligned = [
            {"word": "autos", "start": .5, "end": 1.15,
             "confidence": .7, "phonemes": []},
            {"word": "und", "start": 1.20, "end": 1.9,
             "confidence": .7, "phonemes": []},
        ]

        repairs = _repair_ipa_vocal_holes(
            np.zeros(3 * 16000, dtype=np.float32), line, aligned)

        self.assertEqual([], repairs)
        self.assertEqual(.9, line.words[0]["end"])
        self.assertEqual(1.6, line.words[1]["start"])

    def test_unanchored_ipa_gap_candidate_is_not_used(self):
        line = SimpleNamespace(timestamp=.5, words=[
            {"word": "eins", "start": .5, "end": .9},
            {"word": "zwei", "start": 1.4, "end": 1.8},
        ])
        aligned = [
            {"word": "eins", "start": .2, "end": 1.15,
             "confidence": .3, "phonemes": []},
            {"word": "zwei", "start": 1.20, "end": 2.1,
             "confidence": .3, "phonemes": []},
        ]
        audio = np.ones(3 * 16000, dtype=np.float32) * .1

        repairs = _repair_ipa_vocal_holes(audio, line, aligned)

        self.assertEqual([], repairs)

    def test_only_individually_supported_word_votes_are_attached(self):
        line = SimpleNamespace(text="eins zwei drei", words=[
            {"word": "eins", "start": 1.0, "end": 1.3},
            {"word": "zwei", "start": 1.4, "end": 1.7},
            {"word": "drei", "start": 1.8, "end": 2.1},
        ])
        aligned = [
            {"word": "eins", "start": 1.0, "end": 1.3, "confidence": .8,
             "phonemes": [{"phone": "aɪ", "start": 1.05, "end": 1.2}]},
            # The complete line is plausible, but this individual word is too
            # far from its trusted window and must not influence syllables.
            {"word": "zwei", "start": 1.62, "end": 1.8, "confidence": .8,
             "phonemes": [{"phone": "aɪ", "start": 1.65, "end": 1.75}]},
            {"word": "drei", "start": 1.8, "end": 2.1, "confidence": .8,
             "phonemes": [{"phone": "aɪ", "start": 1.85, "end": 2.0}]},
        ]
        fake = Mock()
        fake.align.return_value = aligned
        fake.model_id = "test-model"

        with patch("app.phoneme_ctc_aligner.PhonemeCtcAligner", return_value=fake):
            summary = annotate_phoneme_boundaries(
                np.zeros(3 * 16000, dtype=np.float32), [line], "de", "cpu")

        self.assertEqual(2, summary["accepted_words"])
        self.assertEqual("xlsr-espeak-ctc", line.words[0]["phoneme_source"])
        self.assertNotIn("phonemes", line.words[1])
        self.assertEqual("xlsr-espeak-ctc", line.words[2]["phoneme_source"])
        fake.close.assert_called_once()

    def test_high_confidence_ipa_onset_is_promoted_only_with_acoustic_support(self):
        line = SimpleNamespace(text="leben", timestamp=1.0, words=[
            {"word": "leben", "start": 1.0, "end": 1.8},
        ])
        fake = Mock()
        fake.align.return_value = [{
            "word": "leben", "start": 1.08, "end": 1.78, "confidence": .84,
            "phonemes": [
                {"phone": "l", "start": 1.08, "end": 1.15},
                {"phone": "eː", "start": 1.15, "end": 1.55},
            ],
        }]
        fake.model_id = "test-model"
        supported = {"supported": True, "score": .81, "kind": "onset"}

        with patch("app.phoneme_ctc_aligner.PhonemeCtcAligner", return_value=fake), \
                patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                      return_value=supported):
            summary = annotate_phoneme_boundaries(
                np.zeros(3 * 16000, dtype=np.float32), [line], "de", "cpu")

        self.assertEqual(1.08, line.words[0]["start"])
        self.assertEqual(1.08, line.timestamp)
        self.assertEqual(80.0, line.words[0]["phoneme_start_refinement_ms"])
        self.assertEqual(1, summary["promoted_word_onsets"])

    def test_ipa_candidate_without_independent_evidence_does_not_move_word(self):
        line = SimpleNamespace(text="leben", timestamp=1.0, words=[
            {"word": "leben", "start": 1.0, "end": 1.8},
        ])
        fake = Mock()
        fake.align.return_value = [{
            "word": "leben", "start": 1.08, "end": 1.78, "confidence": .84,
            "phonemes": [{"phone": "l", "start": 1.08, "end": 1.15}],
        }]
        fake.model_id = "test-model"

        with patch("app.phoneme_ctc_aligner.PhonemeCtcAligner", return_value=fake), \
                patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                      return_value={"supported": False, "score": .2, "kind": "onset"}):
            summary = annotate_phoneme_boundaries(
                np.zeros(3 * 16000, dtype=np.float32), [line], "de", "cpu")

        self.assertEqual(1.0, line.words[0]["start"])
        self.assertEqual(0, summary["promoted_word_onsets"])

    def test_energy_and_spectrum_confirm_a_real_vocal_onset(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate, dtype=np.float32)
        time = np.arange(sample_rate // 2, dtype=np.float32) / sample_rate
        audio[sample_rate // 2:] = 0.35 * np.sin(2 * np.pi * 220 * time)

        evidence = _acoustic_boundary_evidence(audio, 0.5, "onset")

        self.assertTrue(evidence["supported"])
        self.assertGreaterEqual(evidence["score"], .60)

    def test_phoneme_onset_cannot_reintroduce_a_line_overlap(self):
        previous = SimpleNamespace(text="vorher", timestamp=.5, words=[
            {"word": "vorher", "start": .5, "end": 1.04},
        ])
        current = SimpleNamespace(text="jetzt", timestamp=1.10, words=[
            {"word": "jetzt", "start": 1.10, "end": 1.50},
        ])
        fake = Mock()
        fake.align.side_effect = [
            [{"word": "vorher", "start": .5, "end": 1.04, "confidence": .9,
              "phonemes": [{"phone": "f", "start": .5, "end": .6}]}],
            [{"word": "jetzt", "start": 1.02, "end": 1.50, "confidence": .9,
              "phonemes": [{"phone": "j", "start": 1.02, "end": 1.08}]}],
        ]
        fake.model_id = "test-model"

        with patch("app.phoneme_ctc_aligner.PhonemeCtcAligner", return_value=fake), \
                patch("app.phoneme_ctc_aligner._acoustic_boundary_evidence",
                      return_value={"supported": True, "score": .9, "kind": "onset"}):
            summary = annotate_phoneme_boundaries(
                np.zeros(2 * 16000, dtype=np.float32),
                [previous, current], "de", "cpu")

        self.assertEqual(1.10, current.words[0]["start"])
        self.assertEqual(0, summary["promoted_word_onsets"])


if __name__ == "__main__":
    unittest.main()
