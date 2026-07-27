import unittest

from app.models import LrcLine
from app.sections import plan_sections


class SectionPlanningTests(unittest.TestCase):
    def test_splits_at_instrumental_pause(self):
        lines = [LrcLine(value, str(value), "") for value in (10.0, 13.0, 16.0, 30.0, 33.0)]
        sections = plan_sections(lines, 60.0, pause_gap=7.0)
        self.assertEqual([(0, 3), (3, 5)], [(x["first"], x["end"]) for x in sections])
        self.assertLess(sections[0]["audio_end"], 30.0)

    def test_caps_long_continuous_section(self):
        lines = [LrcLine(float(value), str(value), "") for value in range(0, 70, 5)]
        sections = plan_sections(lines, 80.0, max_duration=20.0)
        self.assertGreater(len(sections), 2)
        self.assertTrue(all(section["audio_end"] > section["audio_start"] for section in sections))
