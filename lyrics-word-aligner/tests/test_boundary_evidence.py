import unittest

import numpy as np

from app.boundary_evidence import (active_reference, banded_boundary_step,
                                   leakage_aware_evidence)
from app.phoneme_ctc_aligner import _acoustic_boundary_evidence

SAMPLE_RATE = 16000


def silence(duration=2.0):
    return np.zeros(int(duration * SAMPLE_RATE), dtype=np.float32)


def add_tone(signal, start, duration, frequencies, amplitude=0.2):
    first = int(start * SAMPLE_RATE)
    time = np.arange(int(duration * SAMPLE_RATE)) / SAMPLE_RATE
    tone = sum(np.sin(2 * np.pi * frequency * time) for frequency in frequencies)
    tone = (tone / len(frequencies) * amplitude).astype(np.float32)
    signal[first:first + len(tone)] += tone
    return signal


def add_noise(signal, start, duration, amplitude=0.2, low=None):
    """Broadband or high-passed noise: sibilants and drum transients."""
    first = int(start * SAMPLE_RATE)
    count = int(duration * SAMPLE_RATE)
    generator = np.random.default_rng(7)
    noise = generator.normal(0.0, amplitude, count).astype(np.float32)
    if low is not None:
        spectrum = np.fft.rfft(noise)
        frequencies = np.fft.rfftfreq(count, 1.0 / SAMPLE_RATE)
        spectrum[frequencies < low] = 0.0
        noise = np.fft.irfft(spectrum, n=count).astype(np.float32)
    signal[first:first + len(noise)] += noise
    return signal


class BandedStepTests(unittest.TestCase):
    def test_a_fricative_onset_shows_in_the_sibilance_band(self):
        # Almost no low-frequency energy: broadband RMS barely moves.
        vocal = add_noise(silence(), 1.0, 0.5, amplitude=0.25, low=4000.0)

        step = banded_boundary_step(vocal, 1.0)

        self.assertIsNotNone(step)
        self.assertEqual("sibilance", step["leading_band"])
        self.assertGreater(step["bands"]["sibilance"]["independent_db"], 6.0)

    def test_a_voiced_onset_shows_in_the_voicing_band(self):
        vocal = add_tone(silence(), 1.0, 0.5, (180.0, 360.0, 720.0))

        step = banded_boundary_step(vocal, 1.0)

        self.assertEqual("voicing", step["leading_band"])
        self.assertGreater(step["bands"]["voicing"]["independent_db"], 6.0)

    def test_bleed_explained_by_the_instrumental_is_subtracted(self):
        # The same drum transient in both stems: nothing about it is the singer.
        instrumental = add_noise(silence(), 1.0, 0.5, amplitude=0.30)
        vocal = add_noise(silence(), 1.0, 0.5, amplitude=0.30)

        without = banded_boundary_step(vocal, 1.0)
        with leakage_aware_evidence(instrumental) as reference:
            corrected = banded_boundary_step(vocal, 1.0, reference=reference)

        self.assertGreater(without["independent_db"], 6.0)
        self.assertLess(abs(corrected["independent_db"]), 1.0)

    def test_a_real_onset_survives_the_correction(self):
        # The instrumental plays continuously and has no step at the boundary.
        instrumental = add_noise(silence(), 0.0, 2.0, amplitude=0.20)
        vocal = add_tone(silence(), 1.0, 0.5, (180.0, 360.0, 720.0))

        with leakage_aware_evidence(instrumental) as reference:
            corrected = banded_boundary_step(vocal, 1.0, reference=reference)

        self.assertGreater(corrected["independent_db"], 6.0)
        self.assertTrue(corrected["leakage_corrected"])


class ContextTests(unittest.TestCase):
    def test_context_is_inactive_by_default_and_restores_itself(self):
        self.assertIsNone(active_reference())
        with leakage_aware_evidence(silence()):
            self.assertIsNotNone(active_reference())
        self.assertIsNone(active_reference())

    def test_passing_none_keeps_the_historical_measurement(self):
        with leakage_aware_evidence(None):
            self.assertIsNone(active_reference())

    def test_nested_contexts_restore_the_outer_reference(self):
        outer = silence()
        with leakage_aware_evidence(outer):
            first = active_reference()
            with leakage_aware_evidence(None):
                self.assertIsNone(active_reference())
            self.assertIs(first, active_reference())
        self.assertIsNone(active_reference())


class IntegrationTests(unittest.TestCase):
    """The existing evidence gate must change only inside the context."""

    def test_bleed_is_measured_but_does_not_change_the_verdict(self):
        # Measured on a real reference: letting the band-split step decide
        # raised "verified" words but worsened onset accuracy by 43 %, because
        # the same flag also opens the IPA promotion gate. It stays diagnostic.
        instrumental = add_noise(silence(), 1.0, 0.5, amplitude=0.30)
        vocal = add_noise(silence(), 1.0, 0.5, amplitude=0.30)

        historical = _acoustic_boundary_evidence(vocal, 1.0, "onset")
        with leakage_aware_evidence(instrumental):
            measured = _acoustic_boundary_evidence(vocal, 1.0, "onset")

        self.assertEqual(historical["supported"], measured["supported"])
        self.assertEqual(historical["energy_delta_db"], measured["energy_delta_db"])
        self.assertTrue(measured["leakage_corrected"])
        self.assertIn("bands", measured)
        self.assertLess(
            abs(measured["bands"]["voicing"]["independent_db"]),
            abs(measured["bands"]["voicing"]["measured_db"]))

    def test_behaviour_outside_the_context_is_unchanged(self):
        vocal = add_tone(silence(), 1.0, 0.5, (180.0, 360.0, 720.0))

        first = _acoustic_boundary_evidence(vocal, 1.0, "onset")
        with leakage_aware_evidence(None):
            second = _acoustic_boundary_evidence(vocal, 1.0, "onset")

        self.assertEqual(first, second)
        self.assertNotIn("bands", first)


if __name__ == "__main__":
    unittest.main()
