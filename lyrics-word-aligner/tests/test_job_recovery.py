import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from app.main import _recover_interrupted_jobs


class JobRecoveryTests(unittest.TestCase):
    def _write_status(self, directory: Path, status: dict) -> None:
        directory.mkdir(parents=True)
        (directory / "status.json").write_text(
            json.dumps(status, ensure_ascii=False), encoding="utf-8")

    def test_interrupted_jobs_are_failed_with_previous_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_status(root / "processing-job", {
                "job_id": "processing-job", "state": "processing",
                "percent": 49, "message": "All-Vocals-Kandidaten",
            })
            self._write_status(root / "queued-job", {
                "job_id": "queued-job", "state": "queued", "percent": 1,
            })

            with patch("app.main.OUTPUT_ROOT", root):
                result = _recover_interrupted_jobs()

            self.assertEqual(2, result["recovered"])
            self.assertEqual({"processing-job", "queued-job"}, set(result["jobs"]))
            for job_id in ("processing-job", "queued-job"):
                status = json.loads(
                    (root / job_id / "status.json").read_text(encoding="utf-8"))
                self.assertEqual("failed", status["state"])
                self.assertEqual(100, status["percent"])
                self.assertEqual("aligner-service-restart", status["error"])
                self.assertEqual("startup-recovery-v1", status["recovery"])
                self.assertIn(status["interrupted_state"], {"queued", "processing"})
                self.assertIn("Neustart", status["message"])

    def test_finished_and_invalid_jobs_are_untouched(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_status(root / "completed-job", {
                "job_id": "completed-job", "state": "completed", "percent": 100,
            })
            self._write_status(root / "failed-job", {
                "job_id": "failed-job", "state": "failed", "percent": 100,
            })
            (root / "broken-job").mkdir()
            (root / "broken-job" / "status.json").write_text("{", encoding="utf-8")

            with patch("app.main.OUTPUT_ROOT", root):
                result = _recover_interrupted_jobs()

            self.assertEqual(0, result["recovered"])
            self.assertEqual(
                "completed",
                json.loads((root / "completed-job" / "status.json").read_text())["state"])
            self.assertEqual(
                "failed",
                json.loads((root / "failed-job" / "status.json").read_text())["state"])
            self.assertEqual(
                "{", (root / "broken-job" / "status.json").read_text())


if __name__ == "__main__":
    unittest.main()
