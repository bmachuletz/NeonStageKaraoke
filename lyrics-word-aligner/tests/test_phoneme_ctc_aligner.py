import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import numpy as np

from app.phoneme_ctc_aligner import (
    _acoustic_boundary_evidence,
    _repair_collapsed_ipa_runs,
    _repair_ipa_vocal_holes,
    annotate_phoneme_boundaries,
)


class PhonemeCtcBoundaryTests(unittest.TestCase):
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
