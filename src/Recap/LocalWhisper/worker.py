"""Recap-owned WhisperX worker. Requests arrive on stdin; never print audio/text/tokens.
Only setup may access the network. All caches are rooted in Recap's LocalWhisper directory.
"""
import contextlib
import gc
import hashlib
import json
import math
import os
from pathlib import Path
import socket
import sys
import wave

OUTPUT = sys.stdout
ENGINE = 1
ALIGN_MODELS = {"en": "WAV2VEC2_ASR_BASE_960H", "da": "saattrupdan/wav2vec2-xls-r-300m-ftspeech"}
MODELS = {"tiny", "base", "small", "medium", "large-v3"}


def emit(kind, **values):
    OUTPUT.write("RECAP|" + json.dumps({"kind": kind, **values}, ensure_ascii=True) + "\n")
    OUTPUT.flush()


def configure(root, offline):
    root = Path(root)
    for name, path in {"HF_HOME": root / "models/huggingface", "HF_HUB_CACHE": root / "models/huggingface/hub",
                       "TORCH_HOME": root / "models/torch", "NLTK_DATA": root / "models/nltk",
                       "XDG_CACHE_HOME": root / "cache", "TEMP": root / "tmp", "TMP": root / "tmp"}.items():
        path.mkdir(parents=True, exist_ok=True)
        os.environ[name] = str(path)
    for name in ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE"):
        os.environ.pop(name, None)
    os.environ.update(HF_HUB_DISABLE_TELEMETRY="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1",
                      PYANNOTE_METRICS_ENABLED="0", DO_NOT_TRACK="1", HF_HUB_DISABLE_SYMLINKS_WARNING="1",
                      TOKENIZERS_PARALLELISM="false")
    if offline:
        os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
        def denied(*args, **kwargs):
            raise RuntimeError("Network is disabled in the local recognition worker")
        socket.socket.connect = denied
        socket.socket.connect_ex = denied
        socket.create_connection = denied
    # PyTorch's Windows wheels carry CUDA DLLs; never modify the system PATH/install a toolkit.
    dll_dirs = [Path(sys.prefix) / "Lib/site-packages/torch/lib"]
    dll_dirs += list((Path(sys.prefix) / "Lib/site-packages/nvidia").glob("*/bin"))
    handles = []
    for directory in dll_dirs:
        if directory.is_dir():
            os.environ["PATH"] = str(directory) + os.pathsep + os.environ.get("PATH", "")
            if hasattr(os, "add_dll_directory"):
                handles.append(os.add_dll_directory(str(directory)))
    return handles


def json_safe(value):
    if isinstance(value, dict):
        return {key: json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_safe(item) for item in value]
    if hasattr(value, "item"):
        return json_safe(value.item())
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def atomic_json(path, data):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(json_safe(data), ensure_ascii=False, allow_nan=False), encoding="utf-8")
    temporary.replace(path)


def checkpoint(path, fingerprint, work):
    path = Path(path)
    if path.exists():
        try:
            saved = json.loads(path.read_text(encoding="utf-8"))
            if saved.get("fingerprint") == fingerprint:
                return saved["data"]
        except (ValueError, KeyError, OSError):
            pass
    data = json_safe(work())
    atomic_json(path, {"fingerprint": fingerprint, "data": data})
    return data


def normalized_segments(result, remote=True):
    """Split on word speaker changes only when doing so preserves all recognized text."""
    labels = {}
    segments = []
    def label(raw):
        if not remote:
            return "Me"
        if not raw:
            return "Others"
        if raw not in labels:
            labels[raw] = "Speaker " + str(len(labels) + 1)
        return labels[raw]
    for segment in result.get("segments", []):
        text = segment.get("text", "").strip()
        if not text:
            continue
        words = segment.get("words", [])
        original = "".join(text.split())
        reconstructed = "".join("".join(word.get("word", "").split()) for word in words)
        if not words or original != reconstructed:
            segments.append(dict(start=segment["start"], end=segment["end"], text=text, speaker=label(segment.get("speaker"))))
            continue
        current = None
        for word in words:
            speaker = label(word.get("speaker") or segment.get("speaker"))
            start = word.get("start")
            if start is None:
                start = current["end"] if current else segment["start"]
            end = max(start, word.get("end") if word.get("end") is not None else start)
            if current is None or current["speaker"] != speaker:
                current = dict(start=start, end=end, speaker=speaker, text=word["word"])
                segments.append(current)
            else:
                current["text"] += " " + word["word"]
                current["end"] = end
        if current is not None:
            current["end"] = max(current["end"], segment["end"])
    return segments


def run(request):
    mode = request["mode"]
    if mode not in ("setup", "verify", "transcribe"):
        raise ValueError("Invalid worker mode")
    cfg = request["settings"]
    if cfg["model"] not in MODELS or cfg["device"] not in ("cpu", "cuda"):
        raise ValueError("Unsupported model or device")
    offline = mode != "setup"
    root = Path(request["root"])
    dll_handles = configure(root, offline)
    import logging
    logging.disable(logging.CRITICAL)
    import torch
    import ctranslate2
    import whisperx
    import nltk
    import numpy as np
    from whisperx.diarize import DiarizationPipeline
    from whisperx.audio import SAMPLE_RATE
    torch.set_num_threads(cfg["cpuThreads"])
    device = cfg["device"]
    torch_device = "cuda:" + str(cfg["gpuIndex"]) if device == "cuda" else "cpu"
    if device == "cuda" and (not torch.cuda.is_available() or cfg["gpuIndex"] >= torch.cuda.device_count()):
        emit("error", code="cuda_unavailable")
        return False
    if cfg["computeType"] not in ctranslate2.get_supported_compute_types(device, cfg["gpuIndex"]):
        emit("error", code="compute_unsupported")
        return False

    def release():
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def load_asr(language):
        return whisperx.load_model(cfg["model"], device, device_index=cfg["gpuIndex"],
            compute_type=cfg["computeType"], language=language, threads=cfg["cpuThreads"],
            download_root=str(root / "models/asr"), local_files_only=offline, vad_method="pyannote",
            asr_options={"beam_size": 5, "condition_on_previous_text": False})

    def load_alignment(language):
        return whisperx.load_align_model(language, torch_device, model_name=ALIGN_MODELS[language],
            model_dir=str(root / "models/alignment"), model_cache_only=offline)

    def load_diarization():
        folder = root / "models/diarization/community-1"
        # Recap fetches the pinned, hash-checked mirror before starting setup.
        # This pipeline's config refers to its bundled local component directories.
        return DiarizationPipeline(model_name=str(folder), device=torch_device,
            cache_dir=str(root / "models/huggingface/hub"))

    if mode in ("setup", "verify"):
        emit("progress", phase="Checking transcription model", percent=10)
        model = load_asr("en")
        del model
        release()
        if not offline:
            if not nltk.download("punkt_tab", download_dir=os.environ["NLTK_DATA"], quiet=True, raise_on_error=True):
                raise RuntimeError("Sentence tokenizer download failed")
        for language in ("english", "danish"):
            nltk.data.find("tokenizers/punkt_tab/" + language)
        for language, percent in (("en", 40), ("da", 60)):
            emit("progress", phase="Checking " + language + " alignment model", percent=percent)
            aligner, metadata = load_alignment(language)
            del aligner, metadata
            release()
        if cfg["diarization"]:
            emit("progress", phase="Checking speaker diarization model", percent=80)
            diarizer = load_diarization()
            del diarizer
            release()
        if offline:
            from faster_whisper.utils import download_model
            asr_snapshot = Path(download_model(cfg["model"], cache_dir=str(root / "models/asr"), local_files_only=True))
            # Restrict the receipt to this model and shared assets, not unrelated models/test downloads.
            required_roots = [asr_snapshot.parents[1], root / "models/alignment", root / "models/torch",
                              root / "models/nltk/tokenizers/punkt_tab/english",
                              root / "models/nltk/tokenizers/punkt_tab/danish"]
            if cfg["diarization"]:
                required_roots += [root / "models/diarization/community-1", root / "models/huggingface/hub"]
            files = [{"path": str(path.relative_to(root)), "size": path.stat().st_size}
                     for directory in required_roots for path in directory.rglob("*")
                     if path.is_file() and not any(part in (".cache", ".locks") for part in path.parts)
                     and not path.name.endswith((".lock", ".tmp", ".incomplete"))]
            atomic_json(root / "worker/verified-files.json", {"files": files})
        emit("done", ok=True)
        return True

    language = request["language"]
    if language not in ALIGN_MODELS:
        raise ValueError("Unsupported language")
    path = Path(request["audio"])
    # Recap prepares mono PCM16 WAVs. Passing arrays avoids ffmpeg/torchcodec/system executables.
    with wave.open(str(path), "rb") as source:
        if source.getnchannels() != 1 or source.getsampwidth() != 2 or source.getframerate() != SAMPLE_RATE:
            raise ValueError("Expected prepared mono 16 kHz PCM16 WAV")
        audio = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    digest.update(json.dumps({"engine": request["engineKey"], "settings": cfg, "language": language}, sort_keys=True).encode())
    fingerprint = digest.hexdigest()
    prefix = Path(request["outputPrefix"])

    def transcribe():
        emit("progress", phase="Recognizing speech", percent=5)
        model = load_asr(language)
        try:
            return model.transcribe(audio, batch_size=cfg["batchSize"], language=language,
                progress_callback=lambda percent: emit("progress", phase="Recognizing speech", percent=5 + percent * .45))
        finally:
            del model
            release()
    asr = checkpoint(str(prefix) + "-asr.json", fingerprint, transcribe)

    def align():
        if not asr.get("segments"):
            return {"segments": [], "word_segments": []}
        emit("progress", phase="Aligning words", percent=50)
        aligner, metadata = load_alignment(language)
        try:
            return whisperx.align(asr["segments"], aligner, metadata, audio, torch_device,
                return_char_alignments=False,
                progress_callback=lambda percent: emit("progress", phase="Aligning words", percent=50 + percent * .25))
        finally:
            del aligner, metadata
            release()
    aligned = checkpoint(str(prefix) + "-aligned.json", fingerprint, align)
    remote = request["track"] == "remote"

    def diarize():
        if not aligned.get("segments") or not cfg["diarization"] or not remote:
            return []
        emit("progress", phase="Identifying speakers", percent=75)
        diarizer = load_diarization()
        try:
            frame = diarizer(audio, num_speakers=cfg["speakerCount"] or None,
                progress_callback=lambda percent: emit("progress", phase="Identifying speakers", percent=75 + percent * .24))
            return frame[["start", "end", "speaker"]].to_dict(orient="records")
        finally:
            del diarizer
            release()
    diarization = checkpoint(str(prefix) + "-diarization.json", fingerprint, diarize)
    if diarization:
        import pandas as pd
        aligned = whisperx.assign_word_speakers(pd.DataFrame(diarization), aligned, fill_nearest=False)
    segments = normalized_segments(aligned, remote)
    atomic_json(request["result"], {"segments": segments, "language": language, "model": cfg["model"], "engine": ENGINE})
    emit("done", ok=True)
    return True


def main():
    try:
        request = json.load(sys.stdin)
        # Libraries can print recognized text, file paths or access failures. Parent discards stderr.
        with contextlib.redirect_stdout(sys.stderr):
            ok = run(request)
        return 0 if ok else 1
    except Exception as error:
        # Never serialize exception messages, requests or tracebacks (which may contain tokens/text).
        status = getattr(getattr(error, "response", None), "status_code", None)
        name = type(error).__name__
        code = "access_denied" if status in (401, 403) else "out_of_memory" if "OutOfMemory" in name or isinstance(error, MemoryError) else "runtime_failed"
        emit("error", code=code)
        return 1


if __name__ == "__main__":
    sys.exit(main())
