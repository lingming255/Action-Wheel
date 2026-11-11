# Casino cues borrowed from Point Duel

Source project: the owner's Point Duel, commit `a1472325a8906bd53c200dc6b7e0a16402d9f070`.
Read-only source: `public/casino-sfx.js` and `public/audio/casino/`.
The source audio/script files matched that commit when copied on 2026-11-12; the game project was not modified.

Upstream pack: [Kenney Casino Audio 1.1](https://kenney.nl/assets/casino-audio), by Kenney Vleugels (Kenney.nl).
CC0 1.0 permits personal/commercial use and modification. The original `LICENSE.txt` is preserved and ships with the application.
The previous Interface Sounds preview and its license remain preserved under `Doc/prototypes/wheel-sounds/`.

## Mapping

| Wheel event | Point Duel event / file | Upstream original | Effective reference gain |
| --- | --- | --- | --- |
| Open | reveal / reveal.mp3 | card-slide-1.ogg | master 0.5 × event 0.6 = 0.30 |
| Direction entry | hover / first 70 ms of pick.mp3 | chips-collide-2.ogg | master 0.5 × event 0.16 = 0.08 |
| Confirm | lock / lock.mp3 | chip-lay-1.ogg | master 0.5 × event 0.75 = 0.375 |
| Cancel | same hover slice as direction entry | chips-collide-2.ogg | 0.08 |

The game has no dedicated cancellation event. Cancel is a new wheel mapping of the existing short hover cue.
The 70 ms slice uses the game's linear gain envelope: `e(t) = 1 - t / 0.07` for `0 <= t < 0.07`, then silence.
Its fade is baked into PCM; the 0.08 reference gain is applied at playback.

Game master 0.5 and per-event gains are preserved at the wheel's existing 45% reference setting:
`playbackGain = wheelVolumePercent / 45 * effectiveReferenceGain`.
This matches the digital gain calculation; browser/native output devices can have different system mixer levels.
The existing wheel enabled/disabled state and volume are not changed by installing these assets.

The game's unlimited hover scheduling is retained: every new direction entry is eligible; at most three cues overlap,
and a new cue replaces the oldest when full. Confirmation/cancellation also stop the wheel's unfinished opening/switch cues.
The wheel's existing unchanged-direction suppression remains with its input handling.

## Conversion and verification

Original MP3s, source attribution, and the exact source script snapshot are preserved in `originals/`.
All copied source and resulting PCM SHA256 values are in `manifest.json`.

The game's files were already mono 44.1 kHz MP3, attenuated 3 dB during their original encoding.
Decode/resample to the native player's mono 48 kHz PCM16 format; no peak normalization, pitch changes, added reverb or extra gain:

```powershell
ffmpeg -i reveal.mp3 -ac 1 -ar 48000 -map_metadata -1 -c:a pcm_s16le open.wav
ffmpeg -i lock.mp3 -ac 1 -ar 48000 -map_metadata -1 -c:a pcm_s16le confirm.wav
ffmpeg -i pick.mp3 -ac 1 -ar 48000 -map_metadata -1 -af "aresample=48000,atrim=end=0.07,afade=t=out:st=0:d=0.07:curve=tri" -c:a pcm_s16le switch.wav
```

`cancel.wav` uses the same conversion as `switch.wav`. Only offline signal/format checks were performed; no speaker playback.
`validation.json` records 23 checks, including all source hashes, mono 48 kHz PCM16 decoding, finite samples,
70 ms length, and an independent linear-envelope comparison within two PCM16 units (maximum 1.434 units).
