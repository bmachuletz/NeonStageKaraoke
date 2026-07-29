import unittest

from app.ctc_aligner import _section_allows_atomic_replacement
from app.models import LrcLine


class CtcCandidatePriorityTests(unittest.TestCase):
    def test_atomic_section_does_not_overwrite_stable_ts_line(self):
        section = [LrcLine(1.0, "Hallo", "", words=[
            {"word": "Hallo", "start": 1.0, "end": 1.4,
             "timing_source": "stable-ts-whisper"},
        ])]

        self.assertFalse(_section_allows_atomic_replacement(section, {id(section[0])}))

    def test_atomic_section_does_not_overwrite_qwen_line_for_heuristic_neighbour(self):
        section = [LrcLine(1.0, "Hallo", "", words=[
            {"word": "Hallo", "start": 1.0, "end": 1.4,
             "timing_source": "qwen-forced"},
        ])]

        self.assertFalse(_section_allows_atomic_replacement(section, set()))

    def test_atomic_section_remains_available_when_every_line_is_replaceable(self):
        section = [LrcLine(1.0, "Hallo", "", words=[
            {"word": "Hallo", "start": 1.0, "end": 1.4,
             "timing_source": "geometric-repair"},
        ])]

        self.assertTrue(_section_allows_atomic_replacement(section, {id(section[0])}))
