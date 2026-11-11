# Wheel preview sound sources

- Original pack: [Kenney Interface Sounds 1.0](https://kenney.nl/assets/interface-sounds)
- Creator: Kenney (www.kenney.nl)
- License: CC0 1.0; license text is included as License-Kenney.txt.
- Official archive downloaded through the page on 2026-11-12.
- Four selected original OGG files are preserved in originals/.
- WAV derivatives: mono 48 kHz PCM16, each peak normalized to 0.85. No filtering, trimming, pitch or timing changes.
- Selection uses original semantic names and offline signal inspection, with no speaker playback. User audition decides the final mapping.

| Event | Original | Suggested event gain |
| --- | --- | --- |
| Open | Audio/open_001.ogg | 0.25 |
| Direction switch | Audio/click_003.ogg | 0.10 |
| Confirm | Audio/confirmation_001.ogg | 0.22 |
| Cancel | Audio/back_002.ogg | 0.20 |

Direction changes should be rate-limited (about 45-60 ms) and produce no sound while staying in the same sector. Volume and mute should be exposed in the preview.
