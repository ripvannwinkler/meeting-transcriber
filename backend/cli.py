"""Meeting Transcriber backend CLI.

Run from the backend venv:
    python cli.py transcribe <wav> [--json out.json] [--config settings.json]

Emits newline-delimited JSON events on stdout (see transcribe.py).
"""

from __future__ import annotations

import argparse
import sys


def _make_stdout_utf8() -> None:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass


def main() -> int:
    _make_stdout_utf8()

    parser = argparse.ArgumentParser(prog="mt-backend", description="Meeting Transcriber backend")
    sub = parser.add_subparsers(dest="command", required=True)

    t = sub.add_parser("transcribe", help="Transcribe a WAV to text")
    t.add_argument("wav", help="Path to the WAV file")
    t.add_argument("--config", default=None, help="Path to settings.json (defaults to repo root)")
    t.add_argument("--json", default=None, help="Optional path to also write plain JSON transcript")

    args = parser.parse_args()

    if args.command == "transcribe":
        from config import load_settings
        from transcribe import run_transcribe

        try:
            settings = load_settings(args.config)
            run_transcribe(settings, args.wav)
        except FileNotFoundError as e:
            from transcribe import emit

            emit({"type": "error", "message": str(e)})
            return 1
        except Exception as e:  # noqa: BLE001 - surface any backend error to the app
            from transcribe import emit

            emit({"type": "error", "message": f"{type(e).__name__}: {e}"})
            return 1
    else:
        parser.error(f"unknown command: {args.command}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
