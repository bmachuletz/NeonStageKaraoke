import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

from app.repeated_phrase_refinement import (
    _complete_repeated_unit,
    _initial_phone_class,
    _pronounced_initial_phone_class,
    _repetition_periodicity,
    refine_repeated_phrase_words,
)


class RepeatedPhraseRefinementTests(unittest.TestCase):
    def test_lexically_matching_but_musically_irregular_repeat_is_rejected(self):
        result = _repetition_periodicity(
            [36.880, 37.185, 37.475, 38.325,
             38.597, 39.180, 39.742, 40.633], 2, 4)

        self.assertFalse(result["verified"])
        self.assertEqual("period-outlier", result["reason"])

    def test_regular_repeat_period_is_accepted(self):
        result = _repetition_periodicity(
            [36.880, 37.150, 38.012, 38.290,
             39.106, 39.393, 40.299, 40.621], 2, 4)

        self.assertTrue(result["verified"])

    def test_pronounced_onset_handles_language_specific_silent_letters(self):
        _pronounced_initial_phone_class.cache_clear()
        completed = SimpleNamespace(stdout="n_aɪ_t\n")
        with patch("app.repeated_phrase_refinement.subprocess.run",
                   return_value=completed):
            self.assertEqual("nasal", _initial_phone_class("knight", "en"))

    def test_detects_only_complete_repeated_lines(self):
        self.assertEqual((2, 3), _complete_repeated_unit(
            ["träum", "weiter"] * 3))
        self.assertIsNone(_complete_repeated_unit(
            ["träum", "weiter"] * 3 + ["ich", "hingegen"]))

    def test_repeated_words_receive_independent_acoustic_onsets(self):
        words = []
        for index, text in enumerate(["Träum", "weiter"] * 3):
            start = 1.0 + index * .4
            words.append({"word": text, "start": start, "end": start + .3})
        line = SimpleNamespace(timestamp=1.0, words=words)
        stable = [
            {"word": text, "start": 1.02 + index * .4,
             "end": 1.32 + index * .4, "probability": .95}
            for index, text in enumerate(["Träum", "weiter"] * 3)
        ]
        candidate_times = iter([1.0, 1.4, 1.8, 2.2, 2.6, 3.0])

        def candidates(_audio, prior, _kind, _voicing, _rate, _radius):
            selected = next(candidate_times)
            return [
                {"time": prior, "evidence": .20, "objective": .20,
                 "is_prior": True},
                {"time": selected, "evidence": .82, "objective": .80,
                 "is_prior": False},
            ]

        with patch("app.repeated_phrase_refinement._boundary_candidates",
                   side_effect=candidates), patch(
                       "app.repeated_phrase_refinement._snap_to_activity_rise",
                       side_effect=lambda _audio, prior, _rate: prior + .02):
            summary = refine_repeated_phrase_words(
                np.ones(5 * 16000, dtype=np.float32) * .1,
                [line], stable, "de")

        self.assertEqual(1, summary["refined_lines"])
        self.assertEqual(6, summary["refined_words"])
        self.assertEqual(1.02, line.words[0]["start"])
        self.assertEqual(1.42, line.words[1]["start"])
        self.assertEqual("stable-repetition-acoustic-onset",
                         line.words[0]["timing_source"])

    def test_one_weak_onset_rejects_the_complete_line(self):
        words = [{"word": text, "start": 1 + index * .3,
                  "end": 1.2 + index * .3}
                 for index, text in enumerate(["go"] * 3)]
        line = SimpleNamespace(timestamp=1.0, words=words)
        stable = [{"word": "go", "start": 1 + index * .3,
                   "end": 1.2 + index * .3, "probability": .95}
                  for index in range(3)]
        weak = [
            {"time": 1.0, "evidence": .2, "objective": .2, "is_prior": True},
            {"time": 1.01, "evidence": .3, "objective": .3, "is_prior": False},
        ]

        with patch("app.repeated_phrase_refinement._boundary_candidates",
                   return_value=weak):
            summary = refine_repeated_phrase_words(
                np.ones(3 * 16000, dtype=np.float32) * .1,
                [line], stable, "en")

        self.assertEqual(0, summary["refined_lines"])
        self.assertEqual(1.0, line.words[0]["start"])


if __name__ == "__main__":
    unittest.main()
