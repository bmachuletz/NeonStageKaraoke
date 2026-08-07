import tempfile
import unittest
from pathlib import Path

from app.canonical_lyrics import parse_timed_words, transfer_canonical_text


class CanonicalLyricsTests(unittest.TestCase):
    def test_reads_enhanced_word_intervals(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "acoustic.lrc"
            path.write_text(
                "[00:10.000]<00:10.000,00:10.300>hey <00:10.400,00:10.900>leben\n",
                encoding="utf-8")
            words = parse_timed_words(path)
        self.assertEqual(["hey", "leben"], [word["word"] for word in words])
        self.assertEqual(10.4, words[1]["start"])
        self.assertEqual(10.9, words[1]["end"])

    def test_preserves_canonical_case_and_lines_on_acoustic_timing(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            canonical = root / "canonical.lrc"
            acoustic = root / "acoustic.lrc"
            canonical.write_text(
                "[00:09.000]Ey, dein Leben ist doch nice, Alice!\n"
                "[00:14.000]Hör' bitte auf zu wei'n, Alice.\n",
                encoding="utf-8")
            acoustic.write_text(
                "[00:10.000]<00:10.000,00:10.200>hey <00:10.220,00:10.400>dein "
                "<00:10.420,00:10.700>leben <00:10.720,00:10.850>ist "
                "<00:10.870,00:11.000>doch <00:11.020,00:11.200>nice "
                "<00:11.220,00:11.500>alice\n"
                "[00:15.000]<00:15.000,00:15.200>hör <00:15.220,00:15.500>bitte "
                "<00:15.520,00:15.700>auf <00:15.720,00:15.850>zu "
                "<00:15.870,00:16.200>weinen <00:16.220,00:16.500>alice\n",
                encoding="utf-8")

            _headers, lines, report = transfer_canonical_text(canonical, acoustic)

        self.assertEqual("Ey, dein Leben ist doch nice, Alice!", lines[0].text)
        self.assertEqual("Hör' bitte auf zu wei'n, Alice.", lines[1].text)
        self.assertAlmostEqual(10.0, lines[0].timestamp, places=2)
        self.assertAlmostEqual(15.0, lines[1].timestamp, places=2)
        self.assertEqual(2, report["directly_anchored_lines"])
        self.assertGreater(report["mapping_coverage"], 0.9)

    def test_rejects_unrelated_canonical_lyrics(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            canonical = root / "canonical.lrc"
            acoustic = root / "acoustic.lrc"
            canonical.write_text("[00:01.000]Völlig anderer Inhalt hier\n", encoding="utf-8")
            acoustic.write_text(
                "[00:01.000]<00:01.000,00:01.200>nothing "
                "<00:01.300,00:01.500>matches "
                "<00:01.600,00:01.800>these "
                "<00:01.900,00:02.100>words\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "passen nur"):
                transfer_canonical_text(canonical, acoustic, minimum_coverage=0.75)

    def test_repeated_choruses_are_bound_to_their_local_occurrence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            canonical = root / "canonical.lrc"
            acoustic = root / "acoustic.lrc"
            canonical.write_text(
                "[00:10.000]I don't know how to live but I got toys\n"
                "[00:30.000]I don't know how to live but I got toys\n",
                encoding="utf-8")
            acoustic.write_text(
                "[00:10.200]<00:10.200,00:10.300>I <00:10.300,00:10.500>don't "
                "<00:10.500,00:10.700>know <00:10.700,00:10.900>how "
                "<00:10.900,00:11.000>to <00:11.000,00:11.300>live "
                "<00:11.300,00:11.450>but <00:11.450,00:11.550>I "
                "<00:11.550,00:11.750>got <00:11.750,00:12.000>toys\n"
                "[00:30.300]<00:30.300,00:30.400>I <00:30.400,00:30.600>don't "
                "<00:30.600,00:30.800>know <00:30.800,00:31.000>how "
                "<00:31.000,00:31.100>to <00:31.100,00:31.400>live "
                "<00:31.400,00:31.550>but <00:31.550,00:31.650>I "
                "<00:31.650,00:31.850>got <00:31.850,00:32.100>toys\n",
                encoding="utf-8")

            _headers, lines, report = transfer_canonical_text(canonical, acoustic)

        self.assertAlmostEqual(10.2, lines[0].timestamp, places=2)
        self.assertAlmostEqual(30.3, lines[1].timestamp, places=2)
        self.assertEqual(2, report["locally_anchored_lines"])


if __name__ == "__main__":
    unittest.main()
