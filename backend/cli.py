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
    t.add_argument("wav", help="Path to the mixed WAV file")
    t.add_argument("--config", default=None, help="Path to settings.json (defaults to repo root)")
    t.add_argument("--json", default=None, help="Optional path to also write plain JSON transcript")
    t.add_argument("--loopback", default=None, help="Loopback-only track WAV (dual-track mode)")
    t.add_argument("--mic", default=None, help="Mic-only track WAV (dual-track mode)")

    s = sub.add_parser("summarize", help="Summarize a transcript read from stdin")
    s.add_argument("--config", default=None, help="Path to settings.json (defaults to repo root)")

    args = parser.parse_args()

    if args.command == "transcribe":
        from config import load_settings
        from transcribe import run_transcribe, run_transcribe_tracks

        try:
            settings = load_settings(args.config)
            if args.loopback and args.mic:
                run_transcribe_tracks(settings, args.loopback, args.mic)
            else:
                run_transcribe(settings, args.wav, json_out=args.json)
        except FileNotFoundError as e:
            from transcribe import emit

            emit({"type": "error", "message": str(e)})
            return 1
        except Exception as e:  # noqa: BLE001 - surface any backend error to the app
            from transcribe import emit

            emit({"type": "error", "message": f"{type(e).__name__}: {e}"})
            return 1
    elif args.command == "summarize":
        from config import load_settings
        from summarize import run_summarize

        try:
            settings = load_settings(args.config)
            transcript = sys.stdin.read()
            run_summarize(settings, transcript)
        except Exception as e:  # noqa: BLE001
            from transcribe import emit

            emit({"type": "error", "message": f"{type(e).__name__}: {e}"})
            return 1
    else:
        parser.error(f"unknown command: {args.command}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
