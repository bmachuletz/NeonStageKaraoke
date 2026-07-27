import unittest

import numpy as np

from app.audio import select_alignment_audio


class AlignmentAudioSelectionTests(unittest.TestCase):
    def test_uses_mix_when_separator_removed_nearly_all_signal(self):
        vocals = np.full(16000, 0.001, dtype=np.float32)
        mix = np.full(16000, 0.1, dtype=np.float32)

        selected, report = select_alignment_audio(vocals, mix)

        self.assertIs(selected, mix)
        self.assertTrue(report["fallback_used"])
        self.assertEqual("original-mix", report["source"])

    def test_keeps_healthy_vocal_stem(self):
        vocals = np.full(16000, 0.03, dtype=np.float32)
        mix = np.full(16000, 0.1, dtype=np.float32)

        selected, report = select_alignment_audio(vocals, mix)

        self.assertIs(selected, vocals)
        self.assertFalse(report["fallback_used"])
        self.assertEqual("separated-vocals", report["source"])
