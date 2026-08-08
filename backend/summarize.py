"""Summarization via any OpenAI-compatible chat completions API.

Reads the transcript from stdin, calls the API configured in settings.json
(api.base_url / api_key / model / max_tokens), and emits NDJSON events:
  {"type":"progress","message":str}
  {"type":"summary","text":str}
  {"type":"done"}
  {"type":"error","message":str}
"""

from __future__ import annotations

import sys

from prompt_templates import SYSTEM_PROMPT, USER_PROMPT
from transcribe import emit


def run_summarize(settings, transcript: str) -> None:
    import requests

    base_url = settings.api.base_url
    model = settings.api.model

    if not model:
        emit(
            {
                "type": "error",
                "message": (
                    "No summarization model configured. Open Settings, enter the API base "
                    "URL and pick a model, then try again."
                ),
            }
        )
        return

    emit({"type": "progress", "message": f"Sending {len(transcript)} chars to '{model}'…"})

    payload = {
        "model": model,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": USER_PROMPT.format(transcript=transcript)},
        ],
        "max_tokens": settings.api.max_tokens,
    }
    headers = {}
    if settings.api.api_key:
        headers["Authorization"] = f"Bearer {settings.api.api_key}"

    try:
        response = requests.post(
            f"{base_url}/chat/completions",
            json=payload,
            headers=headers,
            timeout=(30, 600),  # connect / read
        )
        response.raise_for_status()
    except requests.RequestException as e:
        emit(
            {
                "type": "error",
                "message": (
                    f"API request to {base_url} failed — check that the server is running "
                    f"and Settings was saved. ({e})"
                ),
            }
        )
        return

    try:
        data = response.json()
        content = data["choices"][0]["message"]["content"]
    except (KeyError, IndexError, ValueError) as e:
        emit({"type": "error", "message": f"Unexpected API response: {e}"})
        return

    emit({"type": "summary", "text": content.strip()})
    emit({"type": "done"})