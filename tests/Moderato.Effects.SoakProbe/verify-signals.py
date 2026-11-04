"""Verify graceful SIGINT/SIGTERM handling in isolated, already-running probes."""

import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time


def run(command, requested_signal, scenario):
    # Given
    with tempfile.TemporaryDirectory() as directory:
        log = Path(directory) / "probe.log"
        with log.open("w") as output:
            process = subprocess.Popen(
                [*command, scenario, "5", "0.1"],
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
                    raise AssertionError(f"{scenario} did not reach its first observation window:\n{text}")
                time.sleep(0.05)

            # When
            process.send_signal(requested_signal)
            try:
                result = process.wait(timeout=15)
            except subprocess.TimeoutExpired as error:
                raise AssertionError(f"{scenario}: {requested_signal.name} did not stop the probe") from error
            # Then
            text = log.read_text()
            assert result == 130, f"Expected interrupted-probe exit 130, got {result}:\n{text}"
            if scenario == "host":
                assert "STOPPED Host lifetime request; timed run incomplete" in text, text
                assert text.count("PASS host-a ") == text.count("PASS host-b ") == 1, text
                assert text.count("invalid=0 diagnosticDisposals=1") == 2, text
            else:
                stopped = re.findall(r"^STOPPED actualSeconds=[\d.]+ operations=(\d+) acquired=(\d+) released=(\d+) errors=(\d+)$", text, re.MULTILINE)
                assert len(stopped) == 1, text
                assert ",Infinity," not in text and ",NaN," not in text, text
                operations, acquired, released, _ = stopped[0]
                assert int(operations) > 0 and operations == acquired == released, text
                assert not re.search(r"^PASS actualSeconds=", text, re.MULTILINE), text
            print(f"PASS {scenario} {requested_signal.name}: stopped after cleanup and final checks; exit=130")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


if len(sys.argv) < 2:
    raise SystemExit("Usage: verify-signals.py <probe executable command ...>")
if os.name != "posix":
    raise SystemExit("Signal verification requires a POSIX platform.")
for scenario in ("gen", "resource", "provide", "bind", "cancel", "fiber", "die", "host"):
    for requested_signal in (signal.SIGINT, signal.SIGTERM):
        run(sys.argv[1:], requested_signal, scenario)
