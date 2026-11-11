# Casino object sounds

Source: [Kenney Casino Audio 1.1](https://kenney.nl/assets/casino-audio), by Kenney Vleugels (Kenney.nl).

License: Creative Commons Zero (CC0). Original license text is preserved in `LICENSE.txt`. Personal and commercial use permitted; attribution is welcome but not required.

Retrieved 2026-11-12 using the download endpoint linked by the official page:
`https://kenney.nl/download-link/6173736574732f636173696e6f2d617564696f`.
The endpoint returns a temporary signed URL; retrieve a fresh one to download again.

Original ZIP: 876,839 bytes; SHA256 `f36250766ac5bc378c13708ddf12a23a8e54a3251f8d482c7536e51b5dbafa18`.

| Game asset | Original file in Audio/ | Event |
| --- | --- | --- |
| pick.mp3 | chips-collide-2.ogg | Local selection changed |
| lock.mp3 | chip-lay-1.ogg | Successful own submission |
| reveal.mp3 | card-slide-1.ogg | Newly received public round result |

Converted to mono MP3, 44.1 kHz, 96 kbps, with 3 dB attenuation for encoding headroom; no pitch/time modification or artificial reverb. Combined game audio is about 14 KB. Playback gain is fixed per event and never uses a bid, balance, score, or margin.

Button hover reuses the decoded `pick.mp3` buffer at lower fixed gain, with a 70 ms duration and fade to silence. It adds no asset or network request.

Reproduce each conversion with FFmpeg:

```powershell
ffmpeg -i input.ogg -af volume=-3dB -ac 1 -ar 44100 -codec:a libmp3lame -b:a 96k -map_metadata -1 output.mp3
```

The official page labels the pack “50×”; the downloaded 1.1 archive contains 54 `Audio/*.ogg` files, plus a preview. Only these three assets are included here.
