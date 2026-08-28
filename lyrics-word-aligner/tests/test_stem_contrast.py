import unittest
from types import SimpleNamespace

import numpy as np

from app.stem_contrast import refine_final_releases_with_stem_contrast


class StemContrastReleaseTests(unittest.TestCase):
    sample_rate = 16000

    def _signals(self, duration=3.0):
        times = np.arange(int(duration * self.sample_rate)) / self.sample_rate
        instrumental = .10 * np.sin(2 * np.pi * 220 * times)
        return times, instrumental.astype(np.float32)

    def test_separator_residue_is_not_mistaken_for_held_vocal(self):
        times, instrumental = self._signals()
        vocals = np.zeros_like(instrumental)
        sung = (times >= .5) & (times < 1.30)
        residue = (times >= 1.30) & (times < 2.1)
        vocals[sung] = .30 * np.sin(2 * np.pi * 180 * times[sung])
        vocals[residue] = .008 * np.sin(2 * np.pi * 220 * times[residue])
        line = SimpleNamespace(manual_adjusted=False, words=[
            {"word": "ending", "start": .5, "end": 2.0,
             "acoustic_end": 1.25, "sustain_extension_ms": 750},
        ])

        result = refine_final_releases_with_stem_contrast(
            [line], vocals, instrumental, sample_rate=self.sample_rate,
            mode="select")

        self.assertEqual(1, result["adjusted_words"])
        self.assertGreaterEqual(line.words[-1]["end"], 1.30)
        self.assertLessEqual(line.words[-1]["end"], 1.40)
        self.assertIn("stem_contrast_release_trim_ms", line.words[-1])

    def test_short_vocal_trough_does_not_hide_a_returning_release(self):
        times, instrumental = self._signals()
        vocals = .30 * np.sin(2 * np.pi * 180 * times)
        vocals[(times < .5) | (times >= 1.62)] *= .025
        vocals[(times >= 1.02) & (times < 1.14)] *= .025
        line = SimpleNamespace(manual_adjusted=False, words=[
            {"word": "stop", "start": .5, "end": 2.1},
        ])

        result = refine_final_releases_with_stem_contrast(
            [line], vocals.astype(np.float32), instrumental,
            sample_rate=self.sample_rate, mode="select")

        self.assertEqual(1, result["adjusted_words"])
        self.assertGreater(line.words[-1]["end"], 1.60)

    def test_genuinely_dominant_held_vocal_is_unchanged(self):
        times, instrumental = self._signals()
        vocals = .30 * np.sin(2 * np.pi * 180 * times)
        line = SimpleNamespace(manual_adjusted=False, words=[
            {"word": "held", "start": .5, "end": 2.0},
        ])

        result = refine_final_releases_with_stem_contrast(
            [line], vocals.astype(np.float32), instrumental,
            sample_rate=self.sample_rate, mode="select")

        self.assertEqual(0, result["adjusted_words"])
        self.assertEqual(2.0, line.words[-1]["end"])


if __name__ == "__main__":
    unittest.main()
