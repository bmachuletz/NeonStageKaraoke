import json
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

from app import main


class _FakeProcess:
    def __init__(self):
        self.terminated = False
        self.killed = False

    def poll(self):
        return None

    def terminate(self):
        self.terminated = True

    def kill(self):
        self.killed = True

    def wait(self, timeout=None):
        return -15


class JobCancellationTests(unittest.TestCase):
    def setUp(self):
        with main._JOB_LOCK:
            main._JOB_CANCELLATIONS.clear()
            main._JOB_PROCESSES.clear()

    def tearDown(self):
        with main._JOB_LOCK:
            main._JOB_CANCELLATIONS.clear()
            main._JOB_PROCESSES.clear()

    @staticmethod
    def _write_status(root: Path, job_id: str, state: str) -> Path:
        job_dir = root / job_id
        job_dir.mkdir()
        (job_dir / "status.json").write_text(json.dumps({
            "job_id": job_id, "state": state, "percent": 12,
        }), encoding="utf-8")
        return job_dir

    def test_cancel_queued_job_sets_event_and_terminal_status(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_status(root, "queued-job", "queued")
            main._register_job("queued-job")

            with patch("app.main.OUTPUT_ROOT", root):
                status = main._cancel_job("queued-job")

            self.assertEqual("cancelled", status["state"])
            self.assertEqual(100, status["percent"])
            self.assertTrue(main._is_cancelled("queued-job"))

    def test_cancel_running_job_terminates_worker(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_status(root, "running-job", "processing")
            cancellation = threading.Event()
            process = _FakeProcess()
            with main._JOB_LOCK:
                main._JOB_CANCELLATIONS["running-job"] = cancellation
                main._JOB_PROCESSES["running-job"] = process

            with patch("app.main.OUTPUT_ROOT", root):
                status = main._cancel_job("running-job")

            self.assertEqual("cancelled", status["state"])
            self.assertTrue(cancellation.is_set())
            self.assertTrue(process.terminated)
            self.assertFalse(process.killed)

    def test_finished_job_is_not_changed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            job_dir = self._write_status(root, "done-job", "completed")

            with patch("app.main.OUTPUT_ROOT", root):
                status = main._cancel_job("done-job")

            self.assertEqual("completed", status["state"])
            persisted = json.loads((job_dir / "status.json").read_text(encoding="utf-8"))
            self.assertEqual("completed", persisted["state"])


if __name__ == "__main__":
    unittest.main()
