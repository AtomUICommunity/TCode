#!/usr/bin/env python3
"""Measure the packaged AOT app's headless first frame, including process launch.

Usage: python3 tools/startup-bench.py artifacts/startup-bench/TKCode.app
The first sample uses a fresh TKCODE_HOME; subsequent samples reuse it.
This does not measure the macOS compositor, Finder, or Gatekeeper.
"""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess
import tempfile
import time
import threading


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("--runs", type=int, default=10)
    args = parser.parse_args()
    if args.runs < 2:
        parser.error("--runs must be at least 2")
    app = args.app.resolve()
    exe = app / "Contents/MacOS/TKCode"
    out = app.parent
    samples = []
    with tempfile.TemporaryDirectory(prefix="tkcode-startup-") as home:
        env = dict(os.environ, TKCODE_HOME=home, TKCODE_EXTERNAL_RUNTIMES="0")
        for index in range(args.runs):
            command = [str(exe), "--startup-bench"]
            # Save a separate screenshot below so PNG encoding does not affect measurements.
            started = time.perf_counter()
            with subprocess.Popen(command, env=env, stdout=subprocess.PIPE,
                                  stderr=subprocess.STDOUT, text=True) as process:
                marker = None
                lines = []
                deadline = threading.Timer(60, process.kill)
                deadline.start()
                try:
                    for line in process.stdout:
                        lines.append(line)
                        if line.startswith("STARTUP_FRAME_MS="):
                            marker = {
                                "main_to_frame_ms": float(line.split("=", 1)[1]),
                                "launch_to_frame_signal_ms": (time.perf_counter() - started) * 1000,
                            }
                    code = process.wait(timeout=60)
                finally:
                    deadline.cancel()
            if code or marker is None:
                raise RuntimeError(f"Startup failed ({code}): {''.join(lines)}")
            samples.append(marker)
            print(f"Run {index + 1}: {marker}", flush=True)
        subprocess.run([str(exe), "--startup-bench", f"--out={out / 'startup-frame.png'}"],
                       env=env, check=True, capture_output=True, timeout=60)
    repeated = [row["launch_to_frame_signal_ms"] for row in samples[1:]]
    report = {
        "executable": str(exe),
        "method": "headless Skia; process launch to flushed first-frame signal; no PNG encoding",
        "limitations": "Excludes macOS compositor, Finder and Gatekeeper; OS caches are not cleared",
        "samples": samples,
        "repeat_launch_ms": {"median": statistics.median(repeated),
                             "min": min(repeated), "max": max(repeated)},
    }
    (out / "startup-results.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report["repeat_launch_ms"], indent=2))


if __name__ == "__main__":
    main()
