"""Whisper speech-to-text for the Meeting Transcriber backend.

Emits newline-delimited JSON (NDJSON) progress/result events to stdout so the
WPF app can stream them. Events:
  {"type":"progress","message":str}
  {"type":"segment","index":int,"label":str?,"text":str,"start":float,"end":float}
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


def _resolve_device(settings) -> str:
    import torch

    device = settings.stt.device
    if device == "cuda" and not torch.cuda.is_available():
        device = "cpu"
        emit({"type": "progress", "message": "CUDA unavailable; falling back to CPU."})
    return device


def _load_model(settings, device: str):
    whisper = _load_whisper()
    variant = settings.stt.variant
    cache_dir = Path(settings.stt.cache_dir)

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
        return None

    emit(
        {
            "type": "progress",
            "message": f"Loading model '{variant}' ({'CUDA' if device == 'cuda' else 'CPU'})…",
        }
    )
    return whisper.load_model(variant, device=device, download_root=str(cache_dir))


def _transcribe_audio(model, path: str | Path, device: str):
    """Runs Whisper on one WAV, returning (start, end, text) tuples with text."""
    result = model.transcribe(str(path), fp16=(device == "cuda"), verbose=False)
    segments = []
    for seg in result.get("segments") or []:
        text = (seg.get("text") or "").strip()
        if text:
            segments.append((seg.get("start", 0.0), seg.get("end", 0.0), text))
    return segments


def run_transcribe(settings, wav_path: str | Path, json_out: str | Path | None = None) -> None:
    """Transcribe a single mixed WAV and stream NDJSON results to stdout."""
    device = _resolve_device(settings)
    model = _load_model(settings, device)
    if model is None:
        return

    emit({"type": "progress", "message": "Transcribing…"})
    segments = _transcribe_audio(model, wav_path, device)

    lines: list[str] = []
    for i, (start, end, text) in enumerate(segments):
        lines.append(text)
        emit(
            {
                "type": "segment",
                "index": i,
                "text": text,
                "start": start,
                "end": end,
            }
        )

    emit({"type": "transcript", "text": "\n".join(lines)})
    emit({"type": "done"})

    # Optional plain output for manual/failed-UI recovery.
    if json_out is not None:
        import json as _json

        Path(json_out).parent.mkdir(parents=True, exist_ok=True)
        with open(json_out, "w", encoding="utf-8") as fh:
            _json.dump({"text": "\n".join(lines)}, fh, ensure_ascii=False, indent=2)


def run_transcribe_tracks(settings, loopback_path: str | Path, mic_path: str | Path) -> None:
    """Transcribe the loopback and mic tracks separately, then merge labelled by time.

    The loopback track is treated as the authoritative "Speaker" source. Mic segments that
    overlap a speaker segment in time *and* largely repeat its words are acoustic echoes and
    are dropped, so genuine local speech is kept while the remote voice isn't duplicated
    under the Mic label.
    """
    device = _resolve_device(settings)
    model = _load_model(settings, device)
    if model is None:
        return

    emit({"type": "progress", "message": "Transcribing Speaker track…"})
    speaker_segs = _transcribe_audio(model, loopback_path, device)
    emit({"type": "progress", "message": "Transcribing Mic track…"})
    mic_segs = _transcribe_audio(model, mic_path, device)

    merged = _merge_segments(speaker_segs, mic_segs)

    def fmt_time(t: float) -> str:
        m = int(t // 60)
        s = int(t % 60)
        return f"{m}:{s:02d}"

    lines = [f"[{label} {fmt_time(start)}] {text}" for start, label, text in merged]
    for idx, (start, label, text) in enumerate(merged):
        emit(
            {
                "type": "segment",
                "index": idx,
                "label": label,
                "text": text,
                "start": start,
            }
        )
    emit({"type": "transcript", "text": "\n".join(lines)})
    emit({"type": "done"})


import re


def _words(text: str):
    return set(re.findall(r"[a-z']+", text.lower()))


def _similarity(a: str, b: str) -> float:
    wa, wb = _words(a), _words(b)
    if not wa or not wb:
        return 0.0
    return len(wa & wb) / min(len(wa), len(wb))


def _merge_segments(speaker_segs, mic_segs):
    """Returns (start, label, text) tuples; drops mic segments that echo the speaker."""
    speaker_segs = sorted(speaker_segs, key=lambda s: s[0])
    mic_segs = sorted(mic_segs, key=lambda s: s[0])

    kept_mic = []
    for ms, me, mtext in mic_segs:
        is_echo = False
        for ss, se, stext in speaker_segs:
            if me < ss or ms > se:
                continue  # no time overlap
            if _similarity(mtext, stext) >= 0.5:
                is_echo = True
                break
        if not is_echo:
            kept_mic.append((ms, "Mic", mtext))

    merged = [(s, "Speaker", txt) for s, t, txt in speaker_segs] + kept_mic
    merged.sort(key=lambda x: x[0])
    return merged
