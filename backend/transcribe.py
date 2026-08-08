"""Whisper speech-to-text for the Meeting Transcriber backend.

Emits newline-delimited JSON (NDJSON) progress/result events to stdout so the
WPF app can stream them. Events:
  {"type":"progress","message":str}
  {"type":"segment","index":int,"text":str,"start":float,"end":float}
  {"type":"transcript","text":str}        # full plain-text transcript
  {"type":"done"}
  {"type":"error","message":str}
"""

from __future__ import annotations

import sys
from pathlib import Path


def emit(event: dict) -> None:
    import json

    sys.stdout.write(json.dumps(event, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _load_whisper():
    # Imported lazily so `--help`/config errors don't force torch to load.
    import whisper

    return whisper


def run_transcribe(settings, wav_path: str | Path) -> None:
    """Transcribe a WAV and stream NDJSON results to stdout."""
    import torch  # from whisper's dependency; fine to import here

    whisper = _load_whisper()
    variant = settings.stt.variant
    device = settings.stt.device
    cache_dir = Path(settings.stt.cache_dir)

    if device == "cuda" and not torch.cuda.is_available():
        device = "cpu"
        emit({"type": "progress", "message": "CUDA unavailable; falling back to CPU."})

    # Respect auto_download: only download a missing model if enabled.
    model_file = cache_dir / f"{variant}.pt"
    if not model_file.exists() and not settings.stt.auto_download:
        emit(
            {
                "type": "error",
                "message": (
                    f"Model '{variant}' not found in '{cache_dir}' and auto-download is "
                    "disabled. Enable auto-download or run it once elsewhere."
                ),
            }
        )
        return

    emit({"type": "progress", "message": f"Loading model '{variant}' ({device == 'cuda' and 'CUDA' or 'CPU'})…"})
    model = whisper.load_model(variant, device=device, download_root=str(cache_dir))

    emit({"type": "progress", "message": "Transcribing…"})
    # verbose=False keeps console output off stdout (we emit our own NDJSON).
    result = model.transcribe(str(wav_path), fp16=(device == "cuda"), verbose=False)

    lines: list[str] = []
    for i, seg in enumerate(result.get("segments") or []):
        text = (seg.get("text") or "").strip()
        if text:
            lines.append(text)
            emit(
                {
                    "type": "segment",
                    "index": i,
                    "text": text,
                    "start": seg.get("start"),
                    "end": seg.get("end"),
                }
            )

    emit({"type": "transcript", "text": "\n".join(lines)})
    emit({"type": "done"})
