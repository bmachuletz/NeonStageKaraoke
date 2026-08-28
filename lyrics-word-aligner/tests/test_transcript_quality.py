import unittest

from app.transcript_quality import (detect_hallucinated_runs,
                                    measure_transcript_coverage,
                                    score_transcript, selection_rank)

CANONICAL = ("Wollen wir noch ein bisschen zusammen rumhängen "
             "Wollen wir noch hier hin oder da hin gehen "
             "Auf den Dächern dieser Stadt vergessen wieder mal die Zeit")


def words(*items):
    return [{"word": text, "start": start, "end": end,
             "probability": probability}
            for text, start, end, probability in items]


class CoverageTests(unittest.TestCase):
    def test_a_transcript_reaching_every_island_is_fully_covered(self):
        recognized = words(("Wollen", 10.0, 10.5, 0.9), ("wir", 10.5, 11.0, 0.9),
                           ("noch", 20.0, 20.6, 0.9))

        coverage = measure_transcript_coverage(
            recognized, [(10.0, 11.0), (20.0, 20.6)])

        self.assertEqual(1.0, coverage["covered_share"])
        self.assertEqual([], coverage["gaps"])

    def test_a_missed_passage_is_reported_as_an_uncovered_gap(self):
        recognized = words(("Wollen", 10.0, 10.5, 0.9), ("wir", 10.5, 11.0, 0.9))

        coverage = measure_transcript_coverage(
            recognized, [(10.0, 11.0), (20.0, 26.0)])

        self.assertAlmostEqual(1.0 / 7.0, coverage["covered_share"], places=3)
        self.assertEqual(1, len(coverage["gaps"]))
        self.assertAlmostEqual(6.0, coverage["gaps"][0]["seconds"], places=3)

    def test_short_holes_inside_a_phrase_are_not_reported_as_gaps(self):
        recognized = words(("Wollen", 10.0, 10.4, 0.9), ("wir", 10.7, 11.0, 0.9))

        coverage = measure_transcript_coverage(recognized, [(10.0, 11.0)])

        self.assertEqual([], coverage["gaps"])
        self.assertLess(coverage["covered_share"], 1.0)

    def test_missing_activity_never_penalises_a_transcript(self):
        coverage = measure_transcript_coverage(words(("x", 1.0, 2.0, 0.5)), [])

        self.assertEqual(1.0, coverage["covered_share"])
        self.assertEqual("no-vocal-activity", coverage["reason"])


class HallucinationTests(unittest.TestCase):
    def test_a_run_of_alien_words_is_detected(self):
        recognized = words(
            ("Wollen", 10.0, 10.3, 0.9), ("wir", 10.3, 10.6, 0.9),
            ("Band", 11.0, 11.3, 0.21), ("denn", 11.3, 11.6, 0.24),
            ("lo", 11.6, 11.9, 0.19), ("shall", 11.9, 12.2, 0.30))

        result = detect_hallucinated_runs(recognized, CANONICAL)

        self.assertEqual(4, result["hallucinated_words"])
        self.assertEqual(1, len(result["runs"]))
        self.assertEqual("unknown-run-low-confidence", result["runs"][0]["reason"])
        self.assertAlmostEqual(11.0, result["runs"][0]["start"])

    def test_isolated_recognition_errors_are_not_hallucinations(self):
        recognized = words(
            ("Wollen", 10.0, 10.3, 0.9), ("dir", 10.3, 10.6, 0.7),
            ("noch", 10.6, 10.9, 0.9), ("ein", 10.9, 11.2, 0.9),
            ("bisschen", 11.2, 11.6, 0.9))

        result = detect_hallucinated_runs(recognized, CANONICAL)

        self.assertEqual(0, result["hallucinated_words"])
        self.assertEqual([], result["runs"])

    def test_a_clean_transcript_reports_nothing(self):
        recognized = words(("Wollen", 10.0, 10.3, 0.9), ("wir", 10.3, 10.6, 0.9),
                           ("noch", 10.6, 10.9, 0.9))

        result = detect_hallucinated_runs(recognized, CANONICAL)

        self.assertEqual(0, result["hallucinated_words"])
        self.assertEqual(0, result["repetition_loops"])


class SelectionTests(unittest.TestCase):
    def test_a_transcript_missing_a_passage_loses_its_raw_advantage(self):
        thorough = score_transcript(
            {"matching_words": 183, "similarity": 0.80},
            {"covered_share": 0.60}, {"hallucinated_words": 0})
        complete = score_transcript(
            {"matching_words": 170, "similarity": 0.78},
            {"covered_share": 0.95}, {"hallucinated_words": 0})

        self.assertGreater(selection_rank(complete), selection_rank(thorough))

    def test_invented_words_are_removed_before_ranking(self):
        clean = score_transcript(
            {"matching_words": 170, "similarity": 0.78},
            {"covered_share": 0.90}, {"hallucinated_words": 0})
        inventive = score_transcript(
            {"matching_words": 180, "similarity": 0.79},
            {"covered_share": 0.90}, {"hallucinated_words": 30})

        self.assertGreater(selection_rank(clean), selection_rank(inventive))
        self.assertEqual(30, inventive["hallucinated_words"])

    def test_equal_quality_keeps_the_higher_similarity(self):
        first = score_transcript({"matching_words": 100, "similarity": 0.70},
                                 {"covered_share": 0.90}, {"hallucinated_words": 0})
        second = score_transcript({"matching_words": 100, "similarity": 0.85},
                                  {"covered_share": 0.90}, {"hallucinated_words": 0})

        self.assertGreater(selection_rank(second), selection_rank(first))


if __name__ == "__main__":
    unittest.main()
