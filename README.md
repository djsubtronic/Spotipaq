# Spotipaq — skeleton (v0.1)

.NET 2.0 WinForms, x86. Runs today on **mock data** (fake library, procedural artwork, a silent
"player" clock), so the whole UI can be tried on the Compaq before the real engine exists.

## Files (Spotipaq\)
| File | What |
|---|---|
| Model.cs | Track / Album / Artist / Playlist / ArtistPage / SearchResults / NavTarget |
| Backend.cs | `ISpotifyBackend` (data) + `IPlayback` (audio) — the only things the UI talks to; `Async.Run` helper |
| MockBackend.cs | `MockBackend` + `MockPlayback` — replace these with the real engine |
| Theme.cs | colours (accent #1DB954), fonts (Segoe UI Variable Text / Display), pill buttons, artwork drawing, `ImageCache` (1 loader thread, pre-scaled bitmaps) |
| PageView.cs | the content area: ONE owner-drawn control holding blocks (header, artist banner header, section, track list, tile grid, text) |
| Chrome.cs | Sidebar, TopBar (back/forward, search, user), NowPlayingBar |
| MainForm.cs | layout, back/forward history, page builders, play queue (shuffle / repeat / prev-restart) |

## Works now (mock)
Browse, search (Enter), album, playlist, artist (banner + Popular + albums/singles + related), Your Music
(Songs / Albums / Artists), back/forward, double-click a track to play its list, click a row's number/play glyph,
artist/album links in rows and in the now-playing bar, shuffle, repeat, seek, volume.
Keys: Space play/pause · Ctrl+←/→ prev/next · Alt+←/→ back/forward · Ctrl+L / Ctrl+F search.

## Stage 1 (this version): real login
- SpotipaqNative.dll (C, VS2017 XP toolchain, static OpenSSL 1.0.2u): `spq_http` (HTTPS/TLS 1.2), `spq_modexp` (DH).
- Engine\: Proto (protobuf), Shannon (verified byte-for-byte against librespot-java), ApSession (apresolve,
  DH handshake, Shannon packets, login, ping/pong), OAuthLogin (PKCE, 127.0.0.1:5588 or paste-the-address),
  SpotifyEngine (DPAPI-encrypted stored credentials in %APPDATA%\Spotipaq\credentials.dat, device id),
  SpotifyBackend (real username now; everything else still MockBackend until stage 2).
- Log: %APPDATA%\Spotipaq\spotipaq.log (no secrets).

## Real engine plan
NTify's approach, ported: librespot-java (NTifyApp fork) for session/login/audio keys/audio files, NTify-SpotAPI
for spclient + Pathfinder (search, playlists, artist, library). Port the needed parts to C#; put what .NET 2.0
can't do on XP (TLS 1.2 https, DH/AES/Shannon fast paths, Vorbis decode → waveOut) in a small native DLL built
with the same VS2017 XP toolchain + our OpenSSL 1.0.2u. Login: OAuth via Serpent on the Compaq (local
loopback redirect) → stored reusable credentials; Zeroconf later.
