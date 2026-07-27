import tempfile
import unittest
from pathlib import Path

from app.lrc import parse_lrc, render_enhanced_lrc
from app.models import LrcLine


class LrcParsingTests(unittest.TestCase):
    def test_plain_lrclib_lyrics_are_preserved_as_untimed_lines(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text(
                "[re:LRCLIB plain lyrics; GPU alignment required]\n"
                "Erste Zeile\n\nZweite Zeile\n",
                encoding="utf-8",
            )
            headers, lines = parse_lrc(path)

        self.assertEqual(["[re:LRCLIB plain lyrics; GPU alignment required]"], headers)
        self.assertEqual(["Erste Zeile", "Zweite Zeile"], [line.text for line in lines])
        self.assertTrue(all(not line.timed_input for line in lines))

    def test_timed_lrc_keeps_the_existing_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text("[00:07.92]Erste Zeile\n", encoding="utf-8")
            _, lines = parse_lrc(path)

        self.assertTrue(lines[0].timed_input)
        self.assertEqual(7.92, lines[0].timestamp)

    def test_uncertain_words_remain_editable_in_enhanced_lrc(self):
        line = LrcLine(38.25, "I don't know", "", words=[
            {"word": "I", "start": 38.25, "end": 38.31},
            {"word": "don't", "start": 38.39, "end": 38.59},
            {"word": "know", "start": 38.63, "end": 38.77},
        ], status="uncertain", reason="Review erforderlich")

        rendered = render_enhanced_lrc([], [line])

        self.assertIn("<00:38.250,00:38.310>I", rendered)
        self.assertIn("<00:38.390,00:38.590>don't", rendered)
        self.assertNotEqual("[00:38.25]I don't know\n", rendered)

    def test_render_preserves_millisecond_alignment_precision(self):
        line = LrcLine(50.508, "mystery", "", words=[
            {"word": "mystery", "start": 50.508, "end": 51.762},
        ], status="ok")

        rendered = render_enhanced_lrc([], [line])

        self.assertEqual("[00:50.508]<00:50.508,00:51.762>mystery\n", rendered)


if __name__ == "__main__":
    unittest.main()
