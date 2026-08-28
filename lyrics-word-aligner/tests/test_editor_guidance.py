import unittest

from app.editor_guidance import build_editor_guided_candidate
from app.models import LrcLine


def line(start, text, word_starts, duration=.3):
    words = [{"word": word, "start": value, "end": value + duration,
              "timing_source": "automatic"}
             for word, value in zip(text.split(), word_starts)]
    return LrcLine(start, text, text, words=words, source_timestamp=start)


class EditorGuidanceTests(unittest.TestCase):
    def test_interpolates_manual_residual_without_moving_anchor_lines(self):
        generated = [
            line(10, "eins jetzt", [10, 10.5]),
            line(20, "zwei jetzt", [20, 20.5]),
            line(30, "drei jetzt", [30, 30.5]),
        ]
        editor = [
            line(10.2, "eins jetzt", [10.2, 10.7]),
            line(20, "zwei jetzt", [20, 20.5]),
            line(30.4, "drei jetzt", [30.4, 30.9]),
        ]
        result, report = build_editor_guided_candidate(
            generated, editor, [(10.2, 11.1), (30.4, 31.3)])
        self.assertAlmostEqual(10, result[0].words[0]["start"])
        self.assertAlmostEqual(20.297, result[1].words[0]["start"], places=3)
        self.assertAlmostEqual(30, result[2].words[0]["start"])
        self.assertEqual("editor-guided-calibration",
                         result[1].words[0]["timing_source"])
        self.assertEqual(2, report["manual_anchor_lines"])
        self.assertEqual(1, report["guided_lines"])

    def test_rejects_incompatible_or_wild_manual_reference(self):
        generated = [line(10, "eins zwei", [10, 10.5]),
                     line(12, "drei vier", [12, 12.5])]
        editor = [line(13, "eins zwei", [13, 13.5]),
                  line(12, "drei vier", [12, 12.5])]
        result, report = build_editor_guided_candidate(
            generated, editor, [(13, 14)])
        self.assertFalse(report["enabled"])
        self.assertEqual(12, result[1].words[0]["start"])


if __name__ == "__main__":
    unittest.main()
