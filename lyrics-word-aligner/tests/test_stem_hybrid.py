import tempfile
import unittest
from pathlib import Path

import numpy as np
import soundfile as sf

from app.separator import StemPaths
from app.stem_hybrid import create_hybrid_stems


class StemHybridTests(unittest.TestCase):
    def test_crossfades_complete_separator_pair_only_inside_confirmed_interval(self):
        sample_rate = 1000
        length = 2000
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            primary = StemPaths(root / "primary-vocals.wav", root / "primary-inst.wav")
            alternative = StemPaths(root / "alt-vocals.wav", root / "alt-inst.wav")
            primary_vocals = np.zeros((length, 2), dtype=np.float32)
            primary_instrumental = np.full((length, 2), .5, dtype=np.float32)
            alternative_vocals = np.full((length, 2), .4, dtype=np.float32)
            alternative_instrumental = np.full((length, 2), .1, dtype=np.float32)
            for path, values in (
                (primary.vocals, primary_vocals),
                (primary.instrumental, primary_instrumental),
                (alternative.vocals, alternative_vocals),
                (alternative.instrumental, alternative_instrumental),
            ):
                sf.write(path, values, sample_rate, subtype="FLOAT")

            hybrid, report = create_hybrid_stems(
                primary, alternative, [(0.6, 1.2)], root / "output",
                padding_seconds=.1, fade_seconds=.05)
            vocals, rate = sf.read(hybrid.vocals, dtype="float32", always_2d=True)
            instrumental, _ = sf.read(
                hybrid.instrumental, dtype="float32", always_2d=True)

            self.assertEqual(sample_rate, rate)
            self.assertTrue(report["enabled"])
            self.assertAlmostEqual(0.0, float(vocals[200, 0]), places=4)
            self.assertAlmostEqual(0.4, float(vocals[800, 0]), places=4)
            self.assertAlmostEqual(0.1, float(instrumental[800, 0]), places=4)
            self.assertAlmostEqual(0.5, float(vocals[800, 0] + instrumental[800, 0]),
                                   places=3)
            self.assertAlmostEqual(0.0, float(vocals[1600, 0]), places=4)

    def test_sorts_and_merges_touching_padded_intervals(self):
        sample_rate = 1000
        length = 3000
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            primary = StemPaths(root / "primary-vocals.wav", root / "primary-inst.wav")
            alternative = StemPaths(root / "alt-vocals.wav", root / "alt-inst.wav")
            for path, value in ((primary.vocals, 0.0), (primary.instrumental, .5),
                                (alternative.vocals, .4), (alternative.instrumental, .1)):
                sf.write(path, np.full((length, 2), value, dtype=np.float32),
                         sample_rate, subtype="FLOAT")

            _hybrid, report = create_hybrid_stems(
                primary, alternative,
                [(2.2, 2.5), (.6, 1.0), (1.15, 1.5), (-2.0, -1.0), (4.0, 5.0)],
                root / "output", padding_seconds=.1, fade_seconds=.05)

            self.assertEqual(5, report["input_interval_count"])
            self.assertEqual(2, report["normalized_interval_count"])
            self.assertEqual(
                [{"start": .6, "end": 1.5}, {"start": 2.2, "end": 2.5}],
                report["intervals"])


if __name__ == "__main__":
    unittest.main()
