"""Verify graceful SIGINT/SIGTERM handling in isolated, already-running Hosts."""

import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time


def run(command, requested_signal):
    # Given
    with tempfile.TemporaryDirectory() as directory:
        log = Path(directory) / "host.log"
        with log.open("w") as output:
            process = subprocess.Popen(
                [*command, "host", "5", "0.1"],
                stdout=output,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
        try:
            deadline = time.monotonic() + 15
            while True:
                text = log.read_text()
                if re.search(r"^\d+\.\d+,\d+,", text, re.MULTILINE):
                    break
                if process.poll() is not None or time.monotonic() >= deadline:
                    raise AssertionError(f"Host did not reach its first observation window:\n{text}")
                time.sleep(0.05)

            # When
            process.send_signal(requested_signal)
            try:
                result = process.wait(timeout=15)
            except subprocess.TimeoutExpired as error:
                raise AssertionError(f"{requested_signal.name} did not stop and join the Hosts") from error
            # Then
            text = log.read_text()
            assert result == 130, f"Expected interrupted-probe exit 130, got {result}:\n{text}"
            assert "STOPPED Host lifetime request; timed run incomplete" in text, text
            assert text.count("PASS host-a ") == text.count("PASS host-b ") == 1, text
            assert text.count("invalid=0 diagnosticDisposals=1") == 2, text
            print(f"PASS {requested_signal.name}: both Hosts stopped, joined, disposed and checked; exit=130")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


if len(sys.argv) < 2:
    raise SystemExit("Usage: verify-signals.py <probe executable command ...>")
if os.name != "posix":
    raise SystemExit("Signal verification requires a POSIX platform.")
for requested_signal in (signal.SIGINT, signal.SIGTERM):
    run(sys.argv[1:], requested_signal)
