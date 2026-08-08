# Meeting Transcriber

A Windows desktop app that records **speaker output (system audio) + microphone as a single
audio stream**, then optionally:

1. **Transcribes** the recording to text with a local [Whisper](https://github.com/openai/whisper) model (GPU/CUDA),
2. **Summarizes** the transcript with **any OpenAI-compatible API** you configure
   (local llama.cpp/Ollama/vLLM/LM Studio server, or hosted OpenAI/OpenRouter, …).

## Stack

| Piece        | Tech                                                                |
| ------------ | ------------------------------------------------------------------- |
| UI + capture | C# / .NET 10 WPF, [NAudio](https://github.com/naudio/NAudio) (WASAPI loopback + mic) |
| STT          | Python 3.12, OpenAI Whisper on CUDA (cu128 torch, RTX 5090-ready)   |
| Summarizer   | Any OpenAI-compatible `chat/completions` endpoint                   |
| Config       | Single unified `settings.json` (also read by the Python backend)    |

## Prerequisites

- Windows with .NET SDK 10 and Python 3.12 on `PATH`
- (Recommended) An NVIDIA GPU with recent drivers for fast transcription

## Setup

```powershell
# 1. Bootstrap the Python backend (creates backend\.venv, installs CUDA torch + whisper)
powershell -ExecutionPolicy Bypass -File scripts\setup_backend.ps1

# 2. Configure the app
Copy-Item settings.example.json settings.json   # then edit, or use the app's Settings window
```

Key `settings.json` fields:

```jsonc
{
  "stt":  { "variant": "medium", "cache_dir": "models/stt", "auto_download": true, "device": "cuda" },
  "api":  { "base_url": "http://localhost:1234/v1", "api_key": "", "model": "", "max_tokens": 4096 },
  "output_dir": "output"
}
```

- `stt.variant` — `tiny` / `base` / `small` / `medium` / `large` / `large-v3`. Missing models are
  downloaded into `cache_dir` on first use (when `auto_download` is on).
- `api.base_url` — the OpenAI-compatible endpoint, e.g. `http://localhost:1234/v1` (a local
  llama.cpp/Ollama server) or `https://api.openai.com/v1`. `api_key` can be left blank for
  local servers. `api.model` is picked via **Settings → Test Connection / List Models**.

## Run

```powershell
dotnet run --project src\MeetingTranscriber.App
```

1. Choose the **speaker output** (loopback) and **microphone**, adjust mic gain.
2. Press **Start Recording**. Both streams are mixed into one 48 kHz stereo WAV in `recordings\`.
3. Press **Stop Recording**.
4. Press **Transcribe & Summarize** — Whisper transcribes locally on the GPU, then the
   configured API summarizes into Key Points / Decisions / Action Items.
5. Save or copy the transcript / summary from the result tabs.

> Use headphones (or don't monitor) while previewing — otherwise the loopback can feed back
> into the microphone.

## Verified

- End-to-end transcribe: SAPI-generated speech → Whisper `tiny` on CUDA → accurate transcript.
- End-to-end summarize: `PipelineClient` → `cli.py summarize` → mock OpenAI-compatible API →
  structured summary.
- Mixed output is a single valid WAV (resampling + trailing-silence trimmed).

Notable catch: the WDL resampler emits an endless tail of zeros once its input drains, so the
recorder drains a short bounded window after stop and trims trailing silence.

## Project layout

```
src/MeetingTranscriber.App/   WPF app (Services/, ViewModels/)
backend/                      Python backend (cli.py, transcribe.py, summarize.py, config.py)
scripts/setup_backend.ps1     venv bootstrap (CUDA torch)
recordings/                   captured WAVs (git-ignored)
models/stt/                   Whisper model cache (git-ignored)
settings.json                 local config, git-ignored
```

## Recovering a recording after a restart

Recordings are written incrementally to `recordings\` and survive app restarts.
To transcribe/summarize one from a fresh session instead of re-recording:

- **In the app:** press **Open Recording…**, pick the WAV, then **Transcribe & Summarize**.
- **CLI fallback (manual):**
  ```powershell
  backend\.venv\Scripts\python.exe backend\cli.py transcribe recordings\<name>.wav --json output\transcript.json
  ```

## Development notes

- Backend contract: newline-delimited JSON (NDJSON) on stdout —
  `progress` / `segment` / `transcript` / `summary` / `error` / `done`.
- Format C# with `dotnet csharpier format src`.