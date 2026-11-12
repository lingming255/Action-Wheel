# Relay opening cue

The wheel's opening cue uses **Relay 2** by **JoontheFloof**:
https://freesound.org/people/JoontheFloof/sounds/740252/

The original page marks the recording **Creative Commons 0 (CC0 1.0)**.
The public high-quality preview is preserved as `relay-original.mp3`; its source URL and SHA256
are in `manifest.json`. The source description is "A relay switch being turned on and off quickly".

`prepare.py` reproduces `open.wav` from that source. It averages stereo channels without a summing
boost, removes the quiet first 30ms and last 15ms, applies mild 1.6:1 compression, fades only 1/3ms at
the boundaries, then normalizes peak to 0.85 and writes 205ms mono 48kHz PCM16. Both mechanical
transients and their 144ms spacing are retained; pitch and speed are unchanged.

The original MP3 can decode above full scale, so peak normalization happens **after** mixing and
processing, before PCM16 conversion. At the 45% reference volume, the player uses 0.45 gain:
peak is approximately 0.3825 (-8.35dBFS), about 3.6dB above the earlier audition's -12dBFS ceiling.
At 100%, the gain is 1 and the peak remains 0.85. System mixer settings still affect listening level.

Only the opening resource and its reference gain are replaced. The accepted 70ms Casino hover,
confirmation, cancellation and their gains remain unchanged. The old card-slide opening file and
its provenance remain under `../Casino/`, excluded from active embedding.

Rollback before this change: `archive/wheel-before-relay-open-20261112` (`98fb3c9`).
