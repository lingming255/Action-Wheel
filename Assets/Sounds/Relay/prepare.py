"""Reproduce the relay opening cue; requires offline ffmpeg and numpy, not app runtime dependencies."""
from pathlib import Path
import hashlib
import json
import subprocess
import wave
import numpy as np

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "relay-original.mp3"
EXPECTED_SOURCE_SHA256 = "14ebc3c4f65e6c558c0aad8890a567c7453cca4449dad9914b8ae75b1a0fc2df"
RATE = 48000


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


if __name__ == "__main__":
    if sha256(SOURCE) != EXPECTED_SOURCE_SHA256:
        raise ValueError("Relay source differs from the verified CC0 preview.")
    audio_filter = (
        "pan=mono|c0=0.5*c0+0.5*c1,aresample=48000,"
        "atrim=start=0.030:end=0.235,asetpts=PTS-STARTPTS,"
        "acompressor=threshold=0.12589254:ratio=1.6:attack=0.5:release=18:knee=2:makeup=1:detection=peak"
    )
    decoded = subprocess.run([
        "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", str(SOURCE),
        "-af", audio_filter, "-ar", str(RATE), "-ac", "1", "-f", "f32le", "pipe:1"
    ], capture_output=True, check=True).stdout
    samples = np.frombuffer(decoded, dtype="<f4").astype(np.float64)
    if not len(samples) or not np.isfinite(samples).all():
        raise ValueError("Invalid decoded relay audio.")
    # Only the quiet boundaries are faded. Preserve both mechanical transients and their spacing.
    samples[:48] *= np.linspace(0, 1, 48)
    samples[-144:] *= np.linspace(1, 0, 144)
    normalization_gain = .85 / float(np.max(np.abs(samples)))
    samples *= normalization_gain
    pcm = np.round(samples * 32767).astype("<i2")
    target = ROOT / "open.wav"
    with wave.open(str(target), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(RATE)
        output.writeframes(pcm.tobytes())
    peak = float(np.max(np.abs(pcm.astype(np.float64) / 32768)))
    manifest = {
        "title": "Relay 2", "author": "JoontheFloof",
        "sourcePage": "https://freesound.org/people/JoontheFloof/sounds/740252/",
        "sourcePreview": "https://cdn.freesound.org/previews/740/740252_16085440-hq.mp3",
        "license": "CC0-1.0", "licenseURL": "https://creativecommons.org/publicdomain/zero/1.0/",
        "sourceFile": SOURCE.name, "sourceSHA256": sha256(SOURCE),
        "file": target.name, "sha256": sha256(target),
        "filter": audio_filter, "normalizationGain": normalization_gain,
        "processing": "Equal-weight stereo mean; trim30-235ms; mild1.6:1 compression;1ms/3ms boundary fades; peak0.85; mono48kPCM16. No pitch/time shift or added sounds.",
        "durationMs": len(pcm) * 1000 / RATE, "sampleRate": RATE, "channels": 1, "bits": 16,
        "peak": peak, "rms": float(np.sqrt(np.mean((pcm.astype(np.float64)/32768)**2))),
        "clippedSamples": int(np.sum(np.abs(pcm.astype(np.int32)) >= 32767)),
        "referenceVolumePercent": 45, "referenceGain": .45,
        "peakAtReference45": peak*.45, "peakAtMaximum100": peak,
        "gainFormula": "wheelVolumePercent / 45 * 0.45",
        "sourceLicenseEvidence": "Original source page explicitly lists Creative Commons 0.",
    }
    if len(pcm) != 9840 or peak >= .851 or manifest["clippedSamples"]:
        raise ValueError("Relay cue failed duration/headroom validation.")
    (ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
