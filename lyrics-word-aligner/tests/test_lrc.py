import tempfile
import unittest
import base64
import json
from pathlib import Path

from app.lrc import parse_lrc, render_enhanced_lrc
from app.models import LrcLine


class LrcParsingTests(unittest.TestCase):
    def test_manual_editor_syllables_are_attached_to_exact_word(self):
        payload = {
            "Line": 0, "Word": 0, "Text": "Träume", "Start": 10.0, "End": 11.0,
            "Syllables": [
                {"Text": "Träu", "Start": 10.0, "End": 10.61,
                 "ManuallyAdjusted": True},
                {"Text": "me", "Start": 10.61, "End": 11.0,
                 "ManuallyAdjusted": True},
            ],
        }
        token = base64.urlsafe_b64encode(
            json.dumps(payload).encode("utf-8")).decode("ascii").rstrip("=")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "editor.lrc"
            path.write_text(
                f"[neon-editor-syllables:{token}]\n"
                "[00:10.000]<00:10.000,00:11.000>Träume\n", encoding="utf-8")
            headers, lines = parse_lrc(path)

        self.assertEqual(1, len(headers))
        syllables = lines[0].words[0]["editor_syllables"]
        self.assertEqual(10.0, lines[0].words[0]["editor_word_start"])
        self.assertEqual(11.0, lines[0].words[0]["editor_word_end"])
        self.assertEqual(["Träu", "me"], [item["text"] for item in syllables])
        self.assertAlmostEqual(10.61, syllables[0]["end"])

    def test_automatic_editor_syllables_are_reference_only(self):
        payload = {
            "Line": 0, "Word": 0, "Text": "Lage", "Start": 10.0, "End": 10.5,
            "WordManuallyAdjusted": False,
            "Syllables": [
                {"Text": "La", "Start": 10.0, "End": 10.24,
                 "ManuallyAdjusted": False},
                {"Text": "ge", "Start": 10.24, "End": 10.5,
                 "ManuallyAdjusted": False},
            ],
        }
        token = base64.urlsafe_b64encode(
            json.dumps(payload).encode("utf-8")).decode("ascii").rstrip("=")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "editor.lrc"
            path.write_text(
                f"[neon-editor-syllables:{token}]\n"
                "[00:10.000]<00:10.000,00:10.500>Lage\n", encoding="utf-8")
            _headers, lines = parse_lrc(path)

        word = lines[0].words[0]
        self.assertEqual(["La", "ge"], [
            item["text"] for item in word["editor_reference_syllables"]])
        self.assertNotIn("editor_syllables", word)

    def test_enhanced_word_ranges_are_loaded_as_one_line(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "enhanced.lrc"
            path.write_text(
                "[00:19.620]<00:19.620,00:20.120>Du "
                "<00:20.120,00:21.310>träumst\n",
                encoding="utf-8",
            )
            _headers, lines = parse_lrc(path)

        self.assertEqual(1, len(lines))
        self.assertEqual("Du träumst", lines[0].text)
        self.assertEqual(2, len(lines[0].words))
        self.assertAlmostEqual(19.620, lines[0].words[0]["start"])
        self.assertAlmostEqual(21.310, lines[0].words[1]["end"])
        self.assertEqual("input-enhanced-lrc", lines[0].words[1]["timing_source"])

    def test_accepts_editor_timespan_precision_beyond_milliseconds(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "precise.lrc"
            path.write_text("[00:10.4100000]Exakte Zeile\n", encoding="utf-8")
            _headers, lines = parse_lrc(path)
        self.assertEqual(1, len(lines))
        self.assertAlmostEqual(10.41, lines[0].timestamp, places=6)
        self.assertEqual("Exakte Zeile", lines[0].text)

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

    def test_structure_markers_are_not_canonical_lyrics(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text(
                "[00:10.00]Erste Zeile\n"
                "[00:14.00][Chorus: Alle]\n"
                "[00:16.00]Zweite Zeile\n",
                encoding="utf-8",
            )
            _headers, lines = parse_lrc(path)

        self.assertEqual(["Erste Zeile", "Zweite Zeile"], [line.text for line in lines])
        self.assertAlmostEqual(14.0, lines[0].source_end_boundary)

    def test_plain_structure_markers_are_ignored(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text("Verse 1\nHallo Welt\nRefrain:\nWir sind hier\n", encoding="utf-8")
            _headers, lines = parse_lrc(path)

        self.assertEqual(["Hallo Welt", "Wir sind hier"], [line.text for line in lines])

    def test_timed_lrc_keeps_the_existing_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text("[00:07.92]Erste Zeile\n", encoding="utf-8")
            _, lines = parse_lrc(path)

        self.assertTrue(lines[0].timed_input)
        self.assertEqual(7.92, lines[0].timestamp)

    def test_timestamped_empty_line_is_preserved_as_phrase_end_boundary(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text(
                "[02:16.42]Just get back in the van and drive far away and play\n"
                "[02:24.78]\n"
                "[02:58.98]There we were standing around\n",
                encoding="utf-8",
            )
            headers, lines = parse_lrc(path)

        self.assertEqual(2, len(lines))
        self.assertAlmostEqual(144.78, lines[0].source_end_boundary)
        rendered = render_enhanced_lrc(headers, lines)
        self.assertIn("[02:24.780]\n", rendered)

    def test_empty_marker_after_next_text_does_not_bound_previous_line(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "song.lrc"
            path.write_text(
                "[00:10.00]First\n[00:12.00]Second\n[00:14.00]\n",
                encoding="utf-8",
            )
            _, lines = parse_lrc(path)

        self.assertIsNone(lines[0].source_end_boundary)
        self.assertAlmostEqual(14.0, lines[1].source_end_boundary)

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

    def test_voice_lane_and_unicode_label_roundtrip(self):
        source = LrcLine(12.0, "Woho", "", words=[
            {"word": "Woho", "start": 12.0, "end": 13.2},
        ], voice_lane=1, voice_label="Zweite Stimme")
        rendered = render_enhanced_lrc([], [source])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "voices.lrc"
            path.write_text(rendered, encoding="utf-8")
            headers, lines = parse_lrc(path)
        self.assertEqual([], headers)
        self.assertEqual(1, lines[0].voice_lane)
        self.assertEqual("Zweite Stimme", lines[0].voice_label)
        self.assertEqual(rendered, render_enhanced_lrc(headers, lines))


if __name__ == "__main__":
    unittest.main()
