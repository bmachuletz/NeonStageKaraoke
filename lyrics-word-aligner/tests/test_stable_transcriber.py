import unittest

from app.models import LrcLine
from app.stable_transcriber import _chunk_windows, realign_with_stable_words
from app.transcript_match import compare_transcripts


class StableTranscriptAlignmentTests(unittest.TestCase):
    def test_chunk_windows_are_bounded_and_cover_audio_once(self):
        windows = _chunk_windows(
            95 * 16000, chunk_seconds=30, overlap_seconds=3)

        self.assertEqual(4, len(windows))
        self.assertTrue(all(end - start <= 30 * 16000
                            for start, end, _keep_start, _keep_end in windows))
        self.assertEqual(0.0, windows[0][2])
        self.assertEqual(95.0, windows[-1][3])
        self.assertTrue(all(abs(left[3] - right[2]) < 0.0001
                            for left, right in zip(windows, windows[1:])))

    def test_chunk_windows_reject_invalid_overlap(self):
        with self.assertRaises(ValueError):
            _chunk_windows(16000, chunk_seconds=30, overlap_seconds=30)

    def test_plain_unanchored_line_is_created_from_complete_stable_match(self):
        line = LrcLine(0.0, "Lass Fahnen wehen", "", words=[], timed_input=False)
        stable_words = [
            {"word": "Lass", "start": 46.8, "end": 47.2, "probability": 0.9},
            {"word": "Fahnen", "start": 47.3, "end": 48.0, "probability": 0.9},
            {"word": "wehen", "start": 48.1, "end": 48.6, "probability": 0.9},
        ]
        comparison = compare_transcripts("Lass Fahnen wehen", "Lass Fahnen wehen")

        result = realign_with_stable_words(
            [line], stable_words, comparison,
            max_start_deviation=float("inf"), allow_unanchored=True,
        )

        self.assertEqual(3, result["accepted_words"])
        self.assertEqual(46.8, line.timestamp)
        self.assertTrue(all(word["timing_source"] == "stable-ts-whisper"
                            for word in line.words))

    def test_plain_line_uses_replacement_as_timing_without_replacing_lyrics(self):
        line = LrcLine(0.0, "bunte Fahnen", "", words=[], timed_input=False)
        stable_words = [
            {"word": "bunte", "start": 48.1, "end": 49.0, "probability": 0.8},
            {"word": "Faden", "start": 49.0, "end": 49.7, "probability": 0.3},
        ]
        comparison = compare_transcripts("bunte Fahnen", "bunte Faden")

        result = realign_with_stable_words(
            [line], stable_words, comparison,
            max_start_deviation=float("inf"), allow_unanchored=True,
        )

        self.assertEqual(2, result["accepted_words"])
        self.assertEqual("bunte Fahnen", line.text)
        self.assertEqual("fahnen", line.words[1]["word"])
        self.assertEqual(49.0, line.words[1]["start"])

    def test_plain_complete_stable_line_replaces_prior_acoustic_timing(self):
        line = LrcLine(57.0, "Komm und sing", "", words=[
            {"word": "Komm", "start": 57.0, "end": 57.3,
             "timing_source": "ctc-phoneme-alignment"},
            {"word": "und", "start": 57.3, "end": 57.5,
             "timing_source": "ctc-phoneme-alignment"},
            {"word": "sing", "start": 57.5, "end": 58.0,
             "timing_source": "ctc-phoneme-alignment"},
        ], timed_input=False)
        stable_words = [
            {"word": "Komm", "start": 50.3, "end": 50.6, "probability": 0.9},
            {"word": "und", "start": 50.6, "end": 50.8, "probability": 0.9},
            {"word": "sing", "start": 50.8, "end": 51.2, "probability": 0.9},
        ]
        comparison = compare_transcripts("Komm und sing", "Komm und sing")

        result = realign_with_stable_words(
            [line], stable_words, comparison,
            max_start_deviation=float("inf"), allow_unanchored=True,
        )

        self.assertEqual(3, result["accepted_words"])
        self.assertEqual(50.3, line.timestamp)

    def test_plain_partial_line_is_interpolated_inside_stable_phrase(self):
        line = LrcLine(145.0, "haben wir selber auch vergeigt", "", words=[
            {"word": word, "start": 145.0 + index * 0.2,
             "end": 145.2 + index * 0.2, "timing_source": "geometric-repair"}
            for index, word in enumerate(("haben", "wir", "selber", "auch", "vergeigt"))
        ], timed_input=False)
        stable_words = [
            {"word": "haben", "start": 132.48, "end": 132.96, "probability": 0.9},
            {"word": "wir", "start": 132.96, "end": 133.24, "probability": 0.9},
            {"word": "selber", "start": 133.24, "end": 133.72, "probability": 0.9},
            {"word": "vergeigt", "start": 133.72, "end": 134.92, "probability": 0.9},
        ]
        comparison = compare_transcripts(
            "haben wir selber auch vergeigt", "haben wir selber vergeigt"
        )

        result = realign_with_stable_words(
            [line], stable_words, comparison,
            max_start_deviation=float("inf"), allow_unanchored=True,
        )

        self.assertEqual("accepted-interpolated", result["line_diagnostics"][0]["status"])
        self.assertEqual(132.48, line.timestamp)
        self.assertAlmostEqual(134.92, line.words[-1]["end"], places=2)

    def test_stable_replaces_corrupted_repetition_when_calibrated_source_agrees(self):
        line = LrcLine(29.4, "the roof is on fire", "", words=[
            {"word": "the", "start": 29.4, "end": 29.8,
             "timing_source": "asr-repetition-anchor"},
            {"word": "roof", "start": 29.8, "end": 30.3,
             "timing_source": "asr-repetition-anchor"},
            {"word": "is", "start": 35.0, "end": 35.3,
             "timing_source": "transition-block-qwen"},
            {"word": "on", "start": 35.3, "end": 35.5,
             "timing_source": "transition-block-qwen"},
            {"word": "fire", "start": 35.6, "end": 36.2,
             "timing_source": "transition-block-qwen"},
        ], source_timestamp=31.2)
        stable_words = [
            {"word": word, "start": start, "end": end, "probability": .95}
            for word, start, end in (
                ("the", 31.9, 32.1), ("roof", 32.1, 32.5),
                ("is", 34.9, 35.2), ("on", 35.2, 35.5), ("fire", 35.5, 36.0),
            )
        ]
        comparison = compare_transcripts(
            "the roof is on fire", " ".join(word["word"] for word in stable_words)
        )

        result = realign_with_stable_words([line], stable_words, comparison)

        self.assertEqual(5, result["accepted_words"])
        self.assertEqual(31.9, line.timestamp)
        self.assertTrue(all(word["timing_source"] == "stable-ts-whisper"
                            for word in line.words))

    def test_promotes_only_complete_matching_line(self):
        lines = [LrcLine(10.0, "Hallo schöne Welt", "", words=[
            {"word": "Hallo", "start": 10.0, "end": 10.2,
             "timing_source": "vocal-activity-repair"},
            {"word": "schöne", "start": 10.2, "end": 10.4,
             "timing_source": "vocal-activity-repair"},
            {"word": "Welt", "start": 10.4, "end": 10.6,
             "timing_source": "vocal-activity-repair"},
        ])]
        stable_words = [
            {"word": "Hallo", "start": 10.1, "end": 10.3, "probability": 0.8},
            {"word": "schöne", "start": 10.35, "end": 10.6, "probability": 0.7},
            {"word": "Welt", "start": 10.65, "end": 11.0, "probability": 0.9},
        ]
        comparison = compare_transcripts("Hallo schöne Welt", "Hallo schöne Welt")

        result = realign_with_stable_words(lines, stable_words, comparison)

        self.assertEqual(3, result["accepted_words"])
        self.assertEqual(10.1, lines[0].words[0]["start"])
        self.assertTrue(all(word["timing_source"] == "stable-ts-whisper"
                            for word in lines[0].words))

    def test_uses_complete_majority_timing_without_replacing_misheard_lyrics(self):
        text = "Einem Volk das aufschreit und verkündet Wir ham die Scheiße satt"
        canonical = text.split()
        recognized_text = "Einem Volk das aufschreit und verkündet der auf die Scheiße satt"
        stable_words = [
            {"word": word, "start": 54.8 + index * .4, "end": 55.1 + index * .4,
             "probability": .15 if word == "der" else .8}
            for index, word in enumerate(recognized_text.split())
        ]
        line = LrcLine(54.4, text, "", source_timestamp=54.4, words=[
            {"word": word, "start": 54.8 + index * .25, "end": 55 + index * .25,
             "timing_source": "vocal-activity-repair"}
            for index, word in enumerate(canonical)
        ])

        result = realign_with_stable_words(
            [line], stable_words, compare_transcripts(text, recognized_text))

        self.assertEqual("accepted-replacement-timing-consensus",
                         result["line_diagnostics"][0]["status"])
        self.assertEqual(canonical, [word["word"] for word in line.words])
        self.assertEqual(stable_words[6]["start"], line.words[6]["start"])
        self.assertEqual("replace", line.words[6]["stable_ts_match"])

    def test_does_not_use_replacement_consensus_across_inserted_asr_word(self):
        text = "wir ham die Scheiße satt"
        recognized_text = "wir haben die Scheiße es satt"
        line = LrcLine(10.0, text, "", source_timestamp=10.0, words=[
            {"word": word, "start": 10 + index * .3, "end": 10.2 + index * .3,
             "timing_source": "vocal-activity-repair"}
            for index, word in enumerate(text.split())
        ])
        stable_words = [
            {"word": word, "start": 10 + index * .3, "end": 10.2 + index * .3,
             "probability": .9}
            for index, word in enumerate(recognized_text.split())
        ]

        result = realign_with_stable_words(
            [line], stable_words, compare_transcripts(text, recognized_text))

        self.assertNotEqual("accepted-replacement-timing-consensus",
                            result["line_diagnostics"][0]["status"])
        self.assertEqual("vocal-activity-repair", line.words[1]["timing_source"])

    def test_replacement_consensus_rejects_unbounded_first_word_leadin(self):
        text = "Einem Volk verkündet Wir ham die Scheiße satt"
        recognized_text = "Einem Volk verkündet der auf die Scheiße satt"
        line = LrcLine(54.4, text, "", source_timestamp=54.4, words=[
            {"word": word, "start": 54.79 + index * .3, "end": 55.05 + index * .3,
             "timing_source": "vocal-activity-repair"}
            for index, word in enumerate(text.split())
        ])
        stable_words = [
            {"word": word,
             "start": 53.66 if index == 0 else 55.08 + (index - 1) * .3,
             "end": 55.08 if index == 0 else 55.3 + (index - 1) * .3,
             "probability": .8}
            for index, word in enumerate(recognized_text.split())
        ]

        result = realign_with_stable_words(
            [line], stable_words, compare_transcripts(text, recognized_text))

        self.assertEqual("accepted-replacement-timing-consensus",
                         result["line_diagnostics"][0]["status"])
        self.assertEqual(54.79, line.words[0]["start"])
        self.assertEqual(55.08, line.words[0]["end"])
        self.assertTrue(line.words[0]["stable_ts_leadin_outlier_rejected"])

    def test_does_not_mix_partial_transcript_match_into_line(self):
        line = LrcLine(10.0, "Hallo schöne Welt", "", words=[
            {"word": word, "start": 10.0, "end": 10.2,
             "timing_source": "vocal-activity-repair"}
            for word in ("Hallo", "schöne", "Welt")
        ])
        stable_words = [
            {"word": "Hallo", "start": 10.1, "end": 10.3, "probability": 0.8},
        ]
        comparison = compare_transcripts("Hallo schöne Welt", "Hallo")

        result = realign_with_stable_words([line], stable_words, comparison)

        self.assertEqual(0, result["accepted_words"])
        self.assertTrue(all(word["timing_source"] == "vocal-activity-repair"
                            for word in line.words))

    def test_accepts_majority_partial_anchors_when_mixed_geometry_is_monotonic(self):
        line = LrcLine(10.0, "eins zwei drei vier", "", words=[
            {"word": word, "start": 10.0 + index * 0.5,
             "end": 10.4 + index * 0.5, "timing_source": "vocal-activity-repair"}
            for index, word in enumerate(("eins", "zwei", "drei", "vier"))
        ])
        stable_words = [
            {"word": "eins", "start": 10.05, "end": 10.35, "probability": 0.9},
            {"word": "zwei", "start": 10.55, "end": 10.9, "probability": 0.9},
            {"word": "vier", "start": 11.55, "end": 11.9, "probability": 0.9},
        ]
        comparison = compare_transcripts("eins zwei drei vier", "eins zwei vier")

        result = realign_with_stable_words([line], stable_words, comparison)

        self.assertEqual(3, result["accepted_words"])
        self.assertEqual("stable-ts-whisper", line.words[0]["timing_source"])
        self.assertEqual("vocal-activity-repair", line.words[2]["timing_source"])
