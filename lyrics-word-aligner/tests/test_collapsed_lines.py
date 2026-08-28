import unittest

import numpy as np

from app.collapsed_lines import (
    repair_collapsed_lines,
    repair_compressed_word_runs,
    vocal_onsets,
)
from app.models import LrcLine


def synthetic_vocal(attacks, *, duration=30.0, sample_rate=16000):
    """A stem with a sharp harmonic attack at each requested time."""
    samples = np.zeros(int(duration * sample_rate), dtype=np.float32)
    time = np.arange(int(0.30 * sample_rate)) / sample_rate
    envelope = np.exp(-time * 9.0).astype(np.float32)
    tone = sum(np.sin(2 * np.pi * frequency * time)
               for frequency in (180.0, 360.0, 720.0)).astype(np.float32)
    burst = (tone * envelope / 3.0).astype(np.float32)
    for attack in attacks:
        first = int(attack * sample_rate)
        last = min(len(samples), first + len(burst))
        samples[first:last] += burst[:last - first]
    return samples


def line(text, start, end, *, lane=0):
    words = text.split()
    step = (end - start) / len(words)
    return LrcLine(start, text, "", words=[
        {"word": word, "start": round(start + index * step, 3),
         "end": round(start + (index + 1) * step, 3)}
        for index, word in enumerate(words)], voice_lane=lane)


class CollapsedLineRepairTests(unittest.TestCase):
    def test_ordinary_delivery_is_never_touched(self):
        lines = [line("Wollen wir noch ein bisschen", 10.0, 11.5),
                 line("Wollen wir noch hier hin gehen", 12.0, 13.6)]
        before = [[dict(word) for word in item.words] for item in lines]

        report = repair_collapsed_lines(lines, [(9.8, 14.0)])

        self.assertEqual(0, report["collapsed_runs"])
        self.assertEqual(0, report["repaired_lines"])
        for item, original in zip(lines, before):
            self.assertEqual([word["start"] for word in original],
                             [word["start"] for word in item.words])

    def test_impossible_run_is_redistributed_over_measured_activity(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 12.0, 12.28),
            line("Wollen wir noch hier hin oder da hin gehen", 12.28, 12.64),
            line("Letzte Zeile mit normalem Tempo", 18.0, 20.0),
        ]

        report = repair_collapsed_lines(lines, [(9.8, 20.5)])

        self.assertEqual(1, report["collapsed_runs"])
        self.assertEqual(1, report["repaired_runs"])
        self.assertEqual(2, report["repaired_lines"])
        collapsed = lines[1:3]
        # Every repaired line must clear the physical floor.
        for item in collapsed:
            syllables = sum(1 for _ in item.words)
            duration = item.words[-1]["end"] - item.words[0]["start"]
            self.assertGreater(duration / syllables, 0.07)
        # The run stays inside the envelope of its untouched neighbours.
        self.assertGreaterEqual(collapsed[0].words[0]["start"], 12.0)
        self.assertLessEqual(collapsed[-1].words[-1]["end"], 18.0)
        # Monotonic and free of internal overlap.
        edges = [value for item in collapsed for word in item.words
                 for value in (word["start"], word["end"])]
        self.assertEqual(edges, sorted(edges))

    def test_untouched_neighbours_keep_their_geometry(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 12.0, 12.28),
            line("Letzte Zeile mit normalem Tempo", 18.0, 20.0),
        ]

        repair_collapsed_lines(lines, [(9.8, 20.5)])

        self.assertEqual(10.0, lines[0].words[0]["start"])
        self.assertEqual(12.0, lines[0].words[-1]["end"])
        self.assertEqual(18.0, lines[2].words[0]["start"])
        self.assertEqual(20.0, lines[2].words[-1]["end"])

    def test_run_is_rejected_when_no_reachable_activity_can_ever_suffice(self):
        # Twelve syllables need at least 840 ms even at the impossibility
        # bound; the whole track offers 500 ms of singing.
        lines = [line("Wollen wir noch ein bisschen zusamm rumhängen",
                      12.0, 12.28)]
        before = [dict(word) for word in lines[0].words]

        report = repair_collapsed_lines(lines, [(11.9, 12.4)])

        self.assertEqual(1, report["collapsed_runs"])
        self.assertEqual(0, report["repaired_runs"])
        rejection = report["rejections"][0]
        self.assertEqual("insufficient-reachable-vocal-activity",
                         rejection["reason"])
        self.assertGreater(rejection["deficit_ms"], 0)
        self.assertEqual([word["start"] for word in before],
                         [word["start"] for word in lines[0].words])

    def test_block_grows_across_a_neighbour_holding_unjustified_time(self):
        # The collapsed line cannot expand between its direct neighbours, but
        # the following line runs far slower than this song ever delivers.
        lines = [
            line("Erste Zeile mit ganz normalem Tempo hier", 10.0, 12.4),
            line("Zweite Zeile mit ganz normalem Tempo hier", 12.5, 14.9),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 15.0, 15.28),
            line("Warte", 15.3, 24.0),
            line("Letzte Zeile mit ganz normalem Tempo hier", 24.1, 26.5),
        ]

        report = repair_collapsed_lines(lines, [(9.8, 27.0)])

        self.assertEqual(1, report["repaired_runs"])
        repair = report["repairs"][0]
        self.assertEqual([3], repair["lines"])
        self.assertEqual([4], repair["absorbed_neighbours"])
        self.assertGreater(repair["resulting_ms_per_syllable"], 70)
        # Untouched neighbours keep their geometry.
        self.assertEqual(14.9, lines[1].words[-1]["end"])
        self.assertEqual(24.1, lines[4].words[0]["start"])
        # The collapsed phrase now has a possible duration.
        duration = lines[2].words[-1]["end"] - lines[2].words[0]["start"]
        self.assertGreater(duration, 0.84)

    def test_only_measured_singing_is_filled_and_pauses_stay_empty(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 11.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 11.0, 11.28),
            line("Letzte Zeile mit normalem Tempo", 18.0, 19.0),
        ]

        report = repair_collapsed_lines(lines, [(9.8, 11.4), (13.0, 18.2)])

        self.assertEqual(1, report["repaired_runs"])
        pauses = [(11.4, 13.0)]
        for word in lines[1].words:
            for pause_start, pause_end in pauses:
                overlap = (min(word["end"], pause_end)
                           - max(word["start"], pause_start))
                self.assertLessEqual(overlap, 0.001,
                                     f"{word['word']} liegt in einer Pause")

    def test_a_line_in_another_lane_does_not_bound_the_envelope(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 12.0, 12.28),
            line("Wohohohohoh", 12.4, 15.0, lane=1),
            line("Letzte Zeile mit normalem Tempo", 18.0, 20.0),
        ]

        report = repair_collapsed_lines(lines, [(9.8, 20.5)])

        self.assertEqual(1, report["repaired_runs"])
        # The backing phrase in lane 1 must not cap the lead line at 12.4 s.
        self.assertGreater(lines[1].words[-1]["end"], 12.4)
        self.assertEqual(12.4, lines[2].words[0]["start"])

    def test_lane_decided_but_not_yet_applied_is_respected(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 12.0, 12.28),
            line("Wohohohohoh", 12.4, 15.0, lane=0),
            line("Letzte Zeile mit normalem Tempo", 18.0, 20.0),
        ]

        report = repair_collapsed_lines(lines, [(9.8, 20.5)],
                                        pending_voice_lanes={2: 1})

        self.assertEqual(1, report["repaired_runs"])
        self.assertGreater(lines[1].words[-1]["end"], 12.4)

    def test_song_local_rate_is_reported_for_diagnostics(self):
        lines = [line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
                 line("Zweite Zeile mit normalem Tempo", 12.5, 14.5),
                 line("Dritte Zeile mit normalem Tempo", 15.0, 17.0)]

        report = repair_collapsed_lines(lines, [(9.8, 17.5)])

        self.assertIsNotNone(report["song_syllable_ms"])
        self.assertGreater(report["song_syllable_ms"], 90)

    def test_a_restored_manual_line_is_never_redistributed(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 12.0, 12.28),
            line("Letzte Zeile mit normalem Tempo", 18.0, 20.0),
        ]
        lines[1].manual_adjusted = True

        report = repair_collapsed_lines(lines, [(9.8, 20.5)])

        self.assertEqual(0, report["collapsed_runs"])
        self.assertEqual(12.0, lines[1].words[0]["start"])

    def test_repair_never_creates_an_overlap_inside_a_lane(self):
        lines = [
            line("Erste Zeile mit normalem Tempo", 10.0, 12.0),
            line("Wollen wir noch ein bisschen zusamm rumhängen", 12.0, 12.28),
            line("Wohohohohoh gesungen lang", 12.3, 16.0, lane=1),
            line("Wollen wir noch hier hin oder da hin gehen", 16.1, 16.4),
            line("Letzte Zeile mit normalem Tempo", 22.0, 24.0),
        ]

        repair_collapsed_lines(lines, [(9.8, 24.5)])

        for lane in (0, 1):
            spans = [(item.words[0]["start"], item.words[-1]["end"])
                     for item in lines if item.voice_lane == lane]
            for (_, earlier_end), (later_start, _) in zip(spans, spans[1:]):
                self.assertLessEqual(earlier_end, later_start + 1e-9,
                                     f"Überlappung in Lane {lane}")

    def test_missing_activity_disables_the_repair_instead_of_guessing(self):
        lines = [line("Wollen wir noch ein bisschen", 12.0, 12.2)]

        report = repair_collapsed_lines(lines, [])

        self.assertFalse(report["enabled"])
        self.assertEqual("no-vocal-activity", report["reason"])
        self.assertEqual(12.0, lines[0].words[0]["start"])


class OnsetAnchoringTests(unittest.TestCase):
    """A redistributed block must follow the voice, not arithmetic."""

    def test_onsets_are_detected_on_the_exact_stem(self):
        attacks = [11.0, 11.5, 12.1, 12.9]
        found, strength = vocal_onsets(synthetic_vocal(attacks), 10.0, 14.0)

        self.assertEqual(len(found), len(strength))
        for attack in attacks:
            self.assertTrue(np.any(np.abs(found - attack) <= 0.03),
                            f"Kein Onset nahe {attack}")

    def test_word_starts_are_snapped_onto_measured_attacks(self):
        # Irregular attacks against a regular proportional layout: exactly the
        # situation the anchoring exists for.
        attacks = [11.02, 11.75, 12.28, 12.90, 13.35]
        audio = synthetic_vocal(attacks)
        lines = [
            line("Erste Zeile mit ganz normalem Tempo hier", 8.0, 10.4),
            line("Wollen wir noch hier hin", 11.0, 11.14),
            line("Letzte Zeile mit ganz normalem Tempo hier", 14.5, 16.9),
        ]

        report = repair_collapsed_lines(lines, [(10.95, 13.75)], audio=audio)

        self.assertTrue(report["onset_evidence"])
        self.assertEqual(1, report["repaired_runs"])
        repair = report["repairs"][0]
        self.assertGreater(repair["onset_candidates"], 0)
        self.assertGreater(repair["onset_snapped_words"], 0)
        starts = [word["start"] for word in lines[1].words]
        matched = sum(1 for start in starts
                      if min(abs(start - attack) for attack in attacks) <= 0.04)
        self.assertGreaterEqual(matched, 3,
                                f"Zu wenige Wortanfänge auf Attacken: {starts}")

    def test_durations_are_not_arithmetically_uniform(self):
        attacks = [11.02, 11.75, 12.28, 12.90, 13.35]
        lines = [
            line("Erste Zeile mit ganz normalem Tempo hier", 8.0, 10.4),
            line("Wollen wir noch hier hin", 11.0, 11.14),
            line("Letzte Zeile mit ganz normalem Tempo hier", 14.5, 16.9),
        ]

        repair_collapsed_lines(lines, [(10.95, 13.75)],
                               audio=synthetic_vocal(attacks))

        durations = [round(word["end"] - word["start"], 3)
                     for word in lines[1].words]
        self.assertGreater(len(set(durations)), 2,
                           f"Wortdauern wirken arithmetisch: {durations}")

    def test_without_audio_the_layout_stays_proportional(self):
        lines = [
            line("Erste Zeile mit ganz normalem Tempo hier", 8.0, 10.4),
            line("Wollen wir noch hier hin", 11.0, 11.14),
            line("Letzte Zeile mit ganz normalem Tempo hier", 14.5, 16.9),
        ]

        report = repair_collapsed_lines(lines, [(10.95, 13.75)])

        self.assertFalse(report["onset_evidence"])
        self.assertEqual(1, report["repaired_runs"])


class CompressedWordRepairTests(unittest.TestCase):
    def test_isolated_collapsed_words_are_repaired_before_syllables(self):
        item = line("I got a lot of toys", 80.10, 80.79)
        # Reproduce the late overlap fallback: the final phrase is packed into
        # a few milliseconds although the whole line still looks plausible.
        starts = [(80.10, 80.28), (80.28, 80.50), (80.50, 80.592),
                  (80.592, 80.632), (80.632, 80.688), (80.688, 80.79)]
        for word, (start, end) in zip(item.words, starts):
            word["start"], word["end"] = start, end

        report = repair_compressed_word_runs([item], [(80.10, 80.79)],
                                             language="en")

        self.assertGreaterEqual(report["compressed_words"], 1)
        self.assertGreaterEqual(report["repaired_runs"], 1)
        for word in item.words:
            self.assertGreaterEqual(word["end"] - word["start"], 0.054)

    def test_long_neighbour_and_gap_no_longer_hide_a_short_word(self):
        item = line("Tried to tell you about no control", 91.786, 95.628)
        timings = [(91.786, 91.838), (91.838, 91.861), (91.861, 93.185),
                   (94.105, 94.441), (94.610, 94.644), (94.644, 94.888),
                   (95.588, 95.628)]
        for word, (start, end) in zip(item.words, timings):
            word["start"], word["end"] = start, end

        report = repair_compressed_word_runs(
            [item], [(91.78, 93.20), (94.08, 94.95), (95.50, 95.75)],
            language="en")

        self.assertGreaterEqual(report["repaired_runs"], 2)
        for word in item.words:
            self.assertGreaterEqual(word["end"] - word["start"], 0.054)

    def test_ordinary_and_manual_geometry_is_untouched(self):
        ordinary = line("ordinary words remain exactly here", 10.0, 12.0)
        manual = line("manual tiny word", 13.0, 13.12)
        manual.manual_adjusted = True
        before = [[dict(word) for word in item.words]
                  for item in (ordinary, manual)]

        report = repair_compressed_word_runs(
            [ordinary, manual], [(9.5, 14.0)], language="en")

        self.assertEqual(0, report["repaired_runs"])
        self.assertEqual(before[0], ordinary.words)
        self.assertEqual(before[1], manual.words)


if __name__ == "__main__":
    unittest.main()
