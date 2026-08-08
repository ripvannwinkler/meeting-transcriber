"""Config loader for the Meeting Transcriber backend.

Reads a single unified `settings.json` (JSON is parsed natively by both
C# System.Text.Json and Python's stdlib json). The WPF app owns the schema
and settings UI; the backend consumes the same file as read-only.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from pathlib import Path

# Defaults mirror the committed settings.example.json.
_DEFAULTS: dict = {
    "stt": {
        "engine": "whisper",
        "variant": "medium",
        "cache_dir": "models/stt",
        "auto_download": True,
        "device": "cuda",
    },
    "api": {
        "base_url": "http://localhost:1234/v1",
        "api_key": "",
        "model": "",
        "max_tokens": 4096,
    },
    "output_dir": "output",
}

# Allowed STT variants for validation.
VALID_VARIANTS = {"tiny", "base", "small", "medium", "large", "large-v3"}


@dataclass
class SttSettings:
    engine: str = "whisper"
    variant: str = "medium"
    cache_dir: Path = Path("models/stt")
    auto_download: bool = True
    device: str = "cuda"


@dataclass
class ApiSettings:
    base_url: str = "http://localhost:1234/v1"
    api_key: str = ""
    model: str = ""
    max_tokens: int = 4096


@dataclass
class Settings:
    stt: SttSettings = field(default_factory=SttSettings)
    api: ApiSettings = field(default_factory=ApiSettings)
    output_dir: Path = Path("output")


def _deep_merge(dst: dict, src: dict) -> None:
    for key, value in src.items():
        if isinstance(value, dict) and isinstance(dst.get(key), dict):
            _deep_merge(dst[key], value)
        else:
            dst[key] = value


def load_settings(path: str | Path | None = None) -> Settings:
    """Load settings from a settings.json file, merging over defaults.

    Missing keys fall back to defaults. Fails with a clear message if a
    required section is present but of the wrong type. Relative path settings
    (stt.cache_dir, output_dir) resolve against the repository root
    (parent of the backend/ directory), matching the C# Paths helper.
    """
    repo_root = Path(__file__).resolve().parent.parent

    cfg = json.loads(json.dumps(_DEFAULTS))  # deep copy of defaults

    if path is None:
        # Search: explicit path > cwd > project root (parent of backend/)
        candidates = [
            Path.cwd() / "settings.json",
            Path(__file__).resolve().parent.parent / "settings.json",
        ]
        for cand in candidates:
            if cand.exists():
                path = cand
                break
        if path is None:
            raise FileNotFoundError(
                "settings.json not found. Copy settings.example.json -> settings.json "
                "and edit it."
            )

    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(
            f"settings file not found: {path}. Copy settings.example.json and edit it."
        )

    with open(path, "r", encoding="utf-8") as fh:
        user_cfg = json.load(fh)

    _deep_merge(cfg, user_cfg)

    stt = cfg["stt"]
    if not stt["variant"] or stt["variant"] not in VALID_VARIANTS:
        raise ValueError(
            f"Unknown STT variant '{stt['variant']}'. "
            f"Allowed: {', '.join(sorted(VALID_VARIANTS))}"
        )

    cache_dir = Path(str(stt["cache_dir"]))
    output_dir = Path(str(cfg["output_dir"]))

    def _abs(p: Path) -> Path:
        return p if p.is_absolute() else repo_root / p

    settings = Settings(
        stt=SttSettings(
            engine=str(stt["engine"]),
            variant=str(stt["variant"]),
            cache_dir=_abs(cache_dir),
            auto_download=bool(stt["auto_download"]),
            device=str(stt["device"]),
        ),
        api=ApiSettings(
            base_url=str(cfg["api"]["base_url"]).rstrip("/"),
            api_key=str(cfg["api"]["api_key"]),
            model=str(cfg["api"]["model"]),
            max_tokens=int(cfg["api"]["max_tokens"]),
        ),
        output_dir=_abs(output_dir),
    )
    return settings
