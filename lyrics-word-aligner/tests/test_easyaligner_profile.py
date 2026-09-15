import unittest

import numpy as np

from app.easyaligner_profile import (_apply_phoneme_onset_candidates,
                                     backing_recovery_candidate_indices,
                                     _split_trailing_backing_lines,
                                     align_independent_backing_lines,
                                     apply_source_timing_guardrails,
                                     assign_easyaligner_timings,
                                     recover_missing_backing_vocals,
                                     reconcile_guardrail_lane_overlaps,
                                     refine_easyaligner_sustain_releases)
from app.models import LrcLine


class EasyAlignerProfileTests(unittest.TestCase):
    def test_primary_stem_collapse_selects_line_and_neighbours_for_recovery(self):
        lines = []
        for source, start, score, text in (
                (10.0, 10.1, .8, "lead before"),
                (20.0, 25.0, .01, "missing chorus"),
                (30.0, 30.1, .8, "lead after")):
            line = LrcLine(source, text, "", source_timestamp=source)
            line.words = [{"word": word, "start": start + index * .1,
                           "end": start + (index + 1) * .1,
                           "easyaligner_score": score}
                          for index, word in enumerate(text.split())]
            lines.append(line)

        self.assertEqual([0, 1, 2], backing_recovery_candidate_indices(lines))

    def test_all_vocals_recovery_projects_simultaneous_weak_chorus_to_lane_two(self):
        lines = []
        for source, start, score, text in (
                (10.0, 10.0, .8, "lead before"),
                (20.0, 25.0, .01, "missing chorus"),
                (20.1, 20.1, .9, "lead response"),
                (40.0, 40.0, .9, "missing chorus")):
            line = LrcLine(source, text, "", source_timestamp=source)
            line.words = [{"word": word, "start": start + index * .2,
                           "end": start + (index + 1) * .2,
                           "easyaligner_score": score}
                          for index, word in enumerate(text.split())]
            lines.append(line)

        class RecoveryAligner:
            def align(self, _audio, text):
                if text == "missing chorus":
                    return [
                        {"word": "missing", "normalized": "missing",
                         "start": .5, "end": 1.0, "score": .8},
                        {"word": "chorus", "normalized": "chorus",
                         "start": 1.0, "end": 2.0, "score": .8},
                    ]
                return [
                    {"word": word, "normalized": word,
                     "start": 1.25 + index * .2,
                     "end": 1.45 + index * .2, "score": .1}
                    for index, word in enumerate(text.split())
                ]

        summary = recover_missing_backing_vocals(
            lines, np.zeros(45 * 16000, dtype=np.float32),
            RecoveryAligner(), [0, 1, 2, 3])

        self.assertEqual(1, summary["recovered_lines"])
        self.assertEqual(1, summary["projected_backing_lines"])
        self.assertEqual(1, lines[1].voice_lane)
        self.assertEqual("Recovered Backing Vocals", lines[1].voice_label)
        self.assertEqual("easyaligner-all-vocals-recovery",
                         lines[1].words[0]["timing_source"])

    def test_parenthesized_answer_becomes_independent_backing_lane(self):
        lines = [LrcLine(165.46,
                         "Highway to hell (I'm on the highway to hell)", "",
                         source_timestamp=165.46)]

        summary = _split_trailing_backing_lines(lines)

        self.assertEqual(1, summary["split_lines"])
        self.assertEqual(2, len(lines))
        self.assertEqual("Highway to hell", lines[0].text)
        self.assertEqual("(I'm on the highway to hell)", lines[1].text)
        self.assertEqual(1, lines[1].voice_lane)
        self.assertEqual("Backing Vocals", lines[1].voice_label)

    def test_repeated_parenthesized_sentence_suffix_is_a_late_backing_echo(self):
        lines = [LrcLine(172.0,
                         "Na klar ich werde nie wie sie (Nie wie sie)", "",
                         source_timestamp=172.0)]

        _split_trailing_backing_lines(lines)

        self.assertEqual("Backing Echo", lines[1].voice_label)

    def test_source_guardrail_repairs_one_global_repetition_collapse_locally(self):
        lines = []
        for index in range(7):
            source = 10.0 + index * 10.0
            start = 30.0 if index == 5 else source
            line = LrcLine(source, f"word{index}", "", source_timestamp=source)
            line.words = [{"word": f"word{index}", "start": start,
                           "end": start + (20.0 if index == 5 else .5),
                           "timing_source": "easyaligner-global-direct"}]
            lines.append(line)

        class LocalAligner:
            def align(self, _audio, text):
                return [{"word": text, "normalized": text,
                         "start": .75, "end": 1.25, "score": .8}]

        summary = apply_source_timing_guardrails(
            lines, np.zeros(90 * 16000, dtype=np.float32), LocalAligner(), {})

        self.assertTrue(summary["trusted"])
        self.assertEqual(1, summary["repaired_lines"])
        self.assertEqual(60.0, lines[5].words[0]["start"])
        self.assertEqual("easyaligner-source-guided-local",
                         lines[5].words[0]["timing_source"])

    def test_source_guardrail_locally_realigns_an_overlong_to(self):
        lines = []
        for index in range(6):
            source = 10.0 + index * 10.0
            line = LrcLine(source, "way to hell", "", source_timestamp=source)
            line.words = [
                {"word": "way", "start": source, "end": source + .5},
                {"word": "to", "start": source + .6,
                 "end": source + (1.5 if index == 5 else .8)},
                {"word": "hell", "start": source + 1.6, "end": source + 2.0},
            ]
            lines.append(line)

        class LocalAligner:
            def align(self, _audio, _text):
                return [
                    {"word": "way", "normalized": "way",
                     "start": .75, "end": 1.25, "score": .8},
                    {"word": "to", "normalized": "to",
                     "start": 1.35, "end": 1.55, "score": .8},
                    {"word": "hell", "normalized": "hell",
                     "start": 1.65, "end": 2.05, "score": .8},
                ]

        summary = apply_source_timing_guardrails(
            lines, np.zeros(80 * 16000, dtype=np.float32), LocalAligner(), {})

        self.assertEqual(1, summary["repaired_lines"])
        self.assertEqual(.2, round(lines[5].words[1]["end"]
                                   - lines[5].words[1]["start"], 2))

    def test_independent_backing_alignment_may_overlap_lead(self):
        lead = LrcLine(20.0, "lead words", "", source_timestamp=20.0)
        lead.words = [{"word": "lead", "start": 20.0, "end": 20.4},
                      {"word": "words", "start": 20.5, "end": 21.0}]
        backing = LrcLine(20.0, "(backing words)", "", source_timestamp=20.0,
                          voice_lane=1, voice_label="Backing Vocals")

        class BackingAligner:
            def align(self, _audio, _text):
                return [{"word": "backing", "normalized": "backing",
                         "start": 1.0, "end": 1.4, "score": .8},
                        {"word": "words", "normalized": "words",
                         "start": 1.5, "end": 2.0, "score": .8}]

        result = align_independent_backing_lines(
            [lead, backing], np.zeros(30 * 16000, dtype=np.float32),
            BackingAligner(), {"offset_seconds": 0.0})

        self.assertEqual(1, result["aligned_lines"])
        self.assertEqual(20.24, backing.words[0]["start"])
        self.assertEqual("easyaligner-independent-backing-local",
                         backing.words[0]["timing_source"])

    def test_unheard_source_suffix_remains_display_only_without_stealing_next_line(self):
        previous = LrcLine(10.0, "on the highway to hell", "", words=[
            {"word": "to", "start": 11.5, "end": 12.2, "frame_end": 610},
            {"word": "hell", "start": 12.1, "end": 12.8,
             "frame_start": 605, "frame_end": 640, "technical_text": "hell"},
        ])
        following = LrcLine(12.0, "highway to hell", "", words=[
            {"word": "highway", "start": 12.0, "end": 12.6},
        ])

        summary = reconcile_guardrail_lane_overlaps([previous, following])

        self.assertEqual(1, summary["display_only_words"])
        self.assertEqual(12.0, previous.words[0]["end"])
        self.assertEqual(12.0, previous.words[1]["start"])
        self.assertEqual(12.0, previous.words[1]["end"])
        self.assertEqual("", previous.words[1]["technical_text"])
        self.assertTrue(previous.words[1]["technical_omitted"])

    def test_direct_profile_preserves_text_and_emits_frame_candidates(self):
        lines = [
            LrcLine(99.0, '"Wir ham die Scheiße satt.', '', words=[
                {"word": "alte", "start": 99.0, "end": 100.0},
            ]),
            LrcLine(199.0, "(nie wie sie).", ""),
        ]
        aligned = [
            {"word": '"Wir', "start": 10.011, "end": 10.191, "score": .91},
            {"word": "ham", "start": 10.193, "end": 10.351, "score": .82},
            {"word": "die", "start": 10.354, "end": 10.471, "score": .73},
            {"word": "Scheiße", "start": 10.474, "end": 10.892, "score": .94},
            {"word": "satt.", "start": 10.895, "end": 11.191, "score": .88},
            {"word": "(nie", "start": 12.011, "end": 12.151, "score": .79},
            {"word": "wie", "start": 12.154, "end": 12.271, "score": .84},
            {"word": "sie).", "start": 12.274, "end": 12.611, "score": .90},
        ]

        summary = assign_easyaligner_timings(lines, aligned)

        self.assertEqual(['"Wir', "ham", "die", "Scheiße", "satt."],
                         [word["word"] for word in lines[0].words])
        self.assertEqual([10.02, 10.2, 10.36, 10.48, 10.9],
                         [word["start"] for word in lines[0].words])
        self.assertEqual("easyaligner-global-viterbi-direct-v1", summary["method"])
        self.assertTrue(all(
            word["timing_source"] == "easyaligner-global-direct"
            and word["frame_start"] == round(word["start"] / .02)
            and word["frame_end"] == round(word["end"] / .02)
            for line in lines for word in line.words))
        self.assertEqual(.91, lines[0].words[0]["confidence"])

    def test_incomplete_global_path_fails_instead_of_interpolating(self):
        lines = [LrcLine(0, "eins zwei drei", "")]

        with self.assertRaisesRegex(ValueError, "unvollständig"):
            assign_easyaligner_timings(lines, [
                {"word": "eins", "start": 1.0, "end": 1.2, "score": .8},
                {"word": "drei", "start": 1.4, "end": 1.6, "score": .8},
            ])

    def test_english_apostrophes_and_repeated_words_keep_display_text(self):
        lines = [LrcLine(0, "Don't stop, don't stop believin'.", "")]
        aligned = [
            {"word": "dont", "normalized": "dont", "start": 1.001, "end": 1.181, "score": .8},
            {"word": "stop", "normalized": "stop", "start": 1.201, "end": 1.381, "score": .8},
            {"word": "dont", "normalized": "dont", "start": 1.601, "end": 1.781, "score": .8},
            {"word": "stop", "normalized": "stop", "start": 1.801, "end": 1.981, "score": .8},
            {"word": "believin", "normalized": "believin", "start": 2.001, "end": 2.501, "score": .8},
        ]

        assign_easyaligner_timings(lines, aligned)

        self.assertEqual(
            ["Don't", "stop,", "don't", "stop", "believin'."],
            [word["word"] for word in lines[0].words])
        self.assertEqual(
            ["dont", "stop", "dont", "stop", "believin"],
            [word["technical_text"] for word in lines[0].words])
        self.assertEqual([1.0, 1.2, 1.6, 1.8, 2.0],
                         [word["start"] for word in lines[0].words])

    def test_single_ctc_frame_survives_presentation_grid_rounding(self):
        lines = [LrcLine(0, "There's a chance", "")]
        aligned = [
            {"word": "There's", "start": 8.65, "end": 8.87, "score": .9},
            {"word": "a", "start": 8.91, "end": 8.93, "score": .9},
            {"word": "chance", "start": 9.03, "end": 9.27, "score": .9},
        ]

        summary = assign_easyaligner_timings(lines, aligned)

        self.assertEqual(3, summary["display_words"])
        article = lines[0].words[1]
        self.assertEqual(8.92, article["start"])
        self.assertEqual(8.94, article["end"])
        self.assertEqual(1, article["frame_end"] - article["frame_start"])

    def test_phoneme_shadow_moves_only_weak_ctc_onset_with_acoustic_vote(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate * 3, dtype=np.float32)
        first, last = sample_rate, int(1.7 * sample_rate)
        time = np.arange(last - first) / sample_rate
        audio[first:last] = .15 * np.sin(2 * np.pi * 220 * time)
        lines = [LrcLine(0, "brennende Barrikaden", "")]
        assign_easyaligner_timings(lines, [
            {"word": "brennende", "start": .9, "end": 1.45, "score": .2},
            {"word": "Barrikaden", "start": 1.5, "end": 2.2, "score": .9},
        ])
        candidates = {
            "line_ranges": [(0, 2)],
            "words": [
                {"word": "brennende", "start": 1.0, "end": 1.44,
                 "confidence": .8, "phonemes": []},
                {"word": "Barrikaden", "start": 1.56, "end": 2.18,
                 "confidence": .9, "phonemes": []},
            ],
        }

        result = _apply_phoneme_onset_candidates(lines, candidates, audio)

        self.assertEqual(1, result["adjusted_words"])
        self.assertEqual(1.0, lines[0].words[0]["start"])
        self.assertEqual(1.5, lines[0].words[1]["start"])
        self.assertEqual("xlsr-espeak-local-shadow",
                         lines[0].words[0]["phoneme_source"])

    def test_phoneme_shadow_rejects_large_repetition_jump(self):
        audio = np.ones(3 * 16000, dtype=np.float32) * .02
        lines = [LrcLine(0, "träum weiter", "")]
        assign_easyaligner_timings(lines, [
            {"word": "träum", "start": 1.0, "end": 1.3, "score": .2},
            {"word": "weiter", "start": 1.4, "end": 1.8, "score": .2},
        ])
        candidates = {"line_ranges": [(0, 2)], "words": [
            {"word": "träum", "start": 2.0, "end": 2.2,
             "confidence": .9, "phonemes": []},
            {"word": "weiter", "start": 2.3, "end": 2.6,
             "confidence": .9, "phonemes": []},
        ]}

        result = _apply_phoneme_onset_candidates(lines, candidates, audio)

        self.assertEqual(0, result["adjusted_words"])
        self.assertEqual(1.0, lines[0].words[0]["start"])
        self.assertTrue(all(item["status"] == "outside-safe-shift"
                            for item in result["diagnostics"]))

    def test_held_final_vowel_extends_without_moving_easyaligner_onset(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate * 5, dtype=np.float32)
        first, last = int(.8 * sample_rate), int(3.18 * sample_rate)
        time = np.arange(last - first) / sample_rate
        envelope = np.linspace(.18, .012, last - first)
        audio[first:last] = envelope * np.sin(2 * np.pi * 215 * time)
        lines = [LrcLine(0, "Feuer", "")]
        assign_easyaligner_timings(lines, [
            {"word": "Feuer", "start": .82, "end": 1.24, "score": .93},
        ])
        original_start = lines[0].words[0]["start"]
        original_frame_end = lines[0].words[0]["frame_end"]

        result = refine_easyaligner_sustain_releases(lines, audio)

        word = lines[0].words[0]
        self.assertEqual(1, result["adjusted_words"])
        self.assertEqual(original_start, word["start"])
        self.assertGreater(word["end"], 3.0)
        self.assertEqual(original_frame_end, word["ctc_frame_end"])
        self.assertEqual(round(word["end"] / .02), word["sung_release_frame_end"])
        self.assertEqual("isolated-vocal-tonal-decay", word["release_timing_source"])

    def test_sustain_stops_before_the_next_easyaligner_word(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate * 4, dtype=np.float32)
        first, last = int(.5 * sample_rate), int(2.8 * sample_rate)
        time = np.arange(last - first) / sample_rate
        audio[first:last] = .1 * np.sin(2 * np.pi * 205 * time)
        lines = [LrcLine(0, "zieh weiter", "")]
        assign_easyaligner_timings(lines, [
            {"word": "zieh", "start": .52, "end": .9, "score": .9},
            {"word": "weiter", "start": 2.2, "end": 2.65, "score": .9},
        ])

        refine_easyaligner_sustain_releases(lines, audio)

        self.assertLessEqual(lines[0].words[0]["end"], 2.08)
        self.assertEqual(2.2, lines[0].words[1]["start"])

    def test_sustain_does_not_extend_an_internal_short_function_word(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate * 4, dtype=np.float32)
        first, last = int(.5 * sample_rate), int(1.7 * sample_rate)
        time = np.arange(last - first) / sample_rate
        audio[first:last] = .1 * np.sin(2 * np.pi * 205 * time)
        lines = [LrcLine(0, "to hell", "")]
        assign_easyaligner_timings(lines, [
            {"word": "to", "start": .52, "end": .9, "score": .9},
            {"word": "hell", "start": 2.2, "end": 2.65, "score": .9},
        ])

        result = refine_easyaligner_sustain_releases(lines, audio)

        self.assertEqual(.9, lines[0].words[0]["end"])
        self.assertEqual(1, result["protected_internal_function_words"])

    def test_short_tonal_tail_keeps_the_rhythmic_ctc_release(self):
        sample_rate = 16000
        audio = np.zeros(sample_rate * 3, dtype=np.float32)
        first, last = int(.8 * sample_rate), int(1.38 * sample_rate)
        time = np.arange(last - first) / sample_rate
        audio[first:last] = .12 * np.sin(2 * np.pi * 215 * time)
        lines = [LrcLine(0, "Edelsteinen", "")]
        assign_easyaligner_timings(lines, [
            {"word": "Edelsteinen", "start": .82, "end": 1.24, "score": .9},
        ])

        result = refine_easyaligner_sustain_releases(lines, audio)

        self.assertEqual(0, result["adjusted_words"])
        self.assertEqual(1.24, lines[0].words[0]["end"])
        self.assertEqual(180, result["minimum_sung_extension_ms"])


if __name__ == "__main__":
    unittest.main()
