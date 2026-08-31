import unittest
from types import SimpleNamespace

import numpy as np

from app.line_transitions import analyze_line_transitions


class LineTransitionTests(unittest.TestCase):
    sample_rate = 16000

    def _audio(self, duration=4.0):
        times = np.arange(int(duration * self.sample_rate)) / self.sample_rate
        return times

    def _lines(self, previous_end=1.3, next_start=1.8):
        previous = SimpleNamespace(
            manual_adjusted=False,
            words=[{"word": "Haaaallo", "start": .8, "end": previous_end}],
        )
        following = SimpleNamespace(
            manual_adjusted=False,
            words=[{"word": "Welt", "start": next_start, "end": 2.6}],
        )
        return [previous, following], previous, following

    def test_clear_pause_moves_release_and_onset_together(self):
        times = self._audio()
        vocals = np.zeros_like(times, dtype=np.float32)
        vocals[(times >= .75) & (times < 1.20)] = .25
        vocals[(times >= 1.65) & (times < 2.60)] = .35
        lines, previous, following = self._lines()

        report = analyze_line_transitions(
            lines, vocals, sample_rate=self.sample_rate)

        self.assertEqual(1, report["separated"])
        self.assertEqual(1, report["adjusted"])
        self.assertEqual(1.3, previous.words[-1]["end"])
        self.assertGreaterEqual(following.words[0]["start"], 1.60)
        self.assertLess(following.words[0]["start"], 1.90)

    def test_clear_earlier_attack_moves_onset_backward(self):
        times = self._audio()
        vocals = np.zeros_like(times, dtype=np.float32)
        vocals[(times >= .75) & (times < 1.20)] = .25
        vocals[(times >= 1.66) & (times < 2.60)] = .35
        lines, previous, following = self._lines(next_start=1.80)

        report = analyze_line_transitions(
            lines, vocals, sample_rate=self.sample_rate)

        self.assertEqual(1, report["separated"])
        self.assertEqual(1, report["adjusted"])
        self.assertEqual("corrected-onset", report["transitions"][0]["status"])
        self.assertGreaterEqual(following.words[0]["start"], 1.60)
        self.assertLess(following.words[0]["start"], 1.86)
        self.assertEqual(1.3, previous.words[-1]["end"])

    def test_early_onset_cannot_overlap_previous_line(self):
        times = self._audio()
        vocals = np.zeros_like(times, dtype=np.float32)
        vocals[(times >= .75) & (times < 1.24)] = .25
        vocals[(times >= 1.30) & (times < 2.60)] = .35
        lines, previous, following = self._lines(
            previous_end=1.32, next_start=1.50)

        report = analyze_line_transitions(
            lines, vocals, sample_rate=self.sample_rate)

        self.assertEqual(1, report["adjusted"])
        self.assertEqual(1.32, previous.words[-1]["end"])
        self.assertGreaterEqual(following.words[0]["start"], 1.32)

    def test_legato_without_energy_valley_is_not_split(self):
        times = self._audio()
        vocals = (.25 * np.sin(2 * np.pi * 180 * times)).astype(np.float32)
        lines, previous, following = self._lines(1.3, 1.35)

        report = analyze_line_transitions(
            lines, vocals, sample_rate=self.sample_rate)

        self.assertEqual(1, report["ambiguous"])
        self.assertEqual(0, report["adjusted"])
        self.assertEqual(1.3, previous.words[-1]["end"])
        self.assertEqual(1.35, following.words[0]["start"])

    def test_tiny_decoder_gap_is_not_treated_as_a_new_attack(self):
        times = self._audio()
        vocals = np.zeros_like(times, dtype=np.float32)
        vocals[(times >= .75) & (times < 1.25)] = .25
        vocals[(times >= 1.28) & (times < 2.3)] = .30
        lines, previous, following = self._lines(1.30, 1.28)

        report = analyze_line_transitions(
            lines, vocals, sample_rate=self.sample_rate, minimum_pause=.055)

        self.assertEqual(0, report["adjusted"])
        self.assertEqual(1.30, previous.words[-1]["end"])
        self.assertEqual(1.28, following.words[0]["start"])

    def test_manual_boundaries_are_reported_but_protected(self):
        times = self._audio()
        vocals = np.zeros_like(times, dtype=np.float32)
        vocals[(times >= .75) & (times < 1.20)] = .25
        vocals[(times >= 1.65) & (times < 2.60)] = .35
        lines, previous, following = self._lines()
        previous.manual_adjusted = True

        report = analyze_line_transitions(
            lines, vocals, sample_rate=self.sample_rate)

        self.assertEqual(1, report["separated"])
        self.assertEqual(0, report["adjusted"])
        self.assertEqual(1.3, previous.words[-1]["end"])
        self.assertEqual(1.8, following.words[0]["start"])


if __name__ == "__main__":
    unittest.main()
