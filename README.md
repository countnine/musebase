<div align="center">

<img src="docs/img/icon.png" width="88" alt="Musebase logo">

<h1>Musebase for Windows</h1>

<b>Real-time, bilingual lyrics on your Windows desktop.</b>

<i>Formerly known as <b>LyricsX for Windows</b>.</i>

<b>English</b> · <a href="README.ko.md">한국어</a>

<img src="docs/img/demo.gif" width="480" alt="Musebase demo">

🌐 <a href="https://countnine.github.io/lyricsx-home/"><b>Homepage &amp; demo</b></a> · ⬇️ <a href="https://github.com/countnine/musebase/releases/latest"><b>Download</b></a>

</div>

Musebase automatically finds the lyrics of the song you're playing, syncs them line-by-line
(and word-by-word), and shows the **original text with its translation** as a transparent
desktop overlay. It's a Windows-native rewrite of the macOS app
[LyricsX](https://github.com/ddddxxx/LyricsX).

## Features

- **Automatic playback detection** — via Windows SMTC. Works with Spotify, Apple Music,
  YouTube Music, and any player that supports media keys. Auto mode ignores browser sessions;
  you can also lock onto a specific player.
- **Multi-source lyrics search** — LRCLIB, NetEase, Kugou (酷狗), and QQ Music (QQ音乐),
  merged and auto-ranked by quality. Sources can be toggled in Settings.
- **Word-level karaoke** — character-by-character fill for sources that provide inline timing
  (Kugou / QQ / NetEase), with line-level fallback otherwise.
- **Bilingual display** — original and translated lines stacked together. Uses the source's own
  translation first, and falls back to machine translation — **LibreTranslate** (free, no key)
  or **DeepL** (your API key).
- **Desktop overlay** — transparent, click-through, always-on-top. Move/resize, fade in/out,
  optional background panel, auto-hide on fullscreen apps / pause / mouse-over. Hover to show
  media controls (previous / play-pause / next).
- **Edit & export** — fix the current lyrics in-app (lossless), export to `.lrc`, or mark wrong
  lyrics to suppress them.
- **Offline cache** — found lyrics (with translations) are stored in SQLite for instant, offline replay.
- **Localized UI — 19 languages** — the interface follows your system language (English fallback),
  selectable in Settings. [Help translate »](TRANSLATING.md)
- **Privacy** — your DeepL API key is stored **encrypted (Windows DPAPI)** and masked in the UI.
- **Automatic updates** — Velopack delta updates from GitHub Releases.

## Download & install

Get the latest build from **[Releases](https://github.com/countnine/musebase/releases/latest)**:

- **`Musebase-win-Setup.exe`** — installs with automatic updates (recommended).
- **`Musebase-win-Portable.zip`** — no installation; unzip and run.

> The app isn't code-signed yet, so Windows SmartScreen may warn on first launch — choose
> *More info → Run anyway*.

> **Upgrading from LyricsX for Windows (≤ 0.9.x):** the rename breaks the auto-update chain —
> please install Musebase manually once. Your settings, cache, and encrypted API key are
> migrated automatically on first launch.

### Requirements

- Windows 10 version 2004 (20H1) or later, or Windows 11
- No .NET install required (builds are self-contained)

## Usage

1. Run `Musebase.exe` → a green **M** icon appears in the tray (the hidden-icons `^` area).
2. Play music → lyrics appear automatically near the bottom of the screen.
3. Right-click the tray icon:
   - **Search lyrics…** — search and replace when auto-matching is wrong
   - **Edit current lyrics… / Export (.lrc)…**
   - **Playback source** — auto (ignores browsers) or a specific player
   - **Move/resize overlay** — drag to reposition, then toggle off
   - **Settings…** — display language, translation engine, lyrics sources, overlay style
4. Overlay only (no playback): `Musebase.exe --demo`

### Machine translation (optional)

Out of the box, lines without a translation in your target language are machine-translated via
**LibreTranslate** (free, no key required). For higher quality, get a free
[DeepL API](https://www.deepl.com/pro-api) key (500k characters/month) and enter it in
**Settings** — Musebase then switches to DeepL automatically. Translations are cached per line
(each song is translated only once). The key is stored encrypted.

## Translating the UI

The interface ships in 19 languages (English + Korean hand-translated, plus DeepL seeds). Anyone can
improve translations directly on GitHub — see **[TRANSLATING.md](TRANSLATING.md)**
([English](TRANSLATING.en.md)).

## Build

```powershell
dotnet build src/Musebase.Windows          # dev build
dotnet test  tests/Musebase.Core.Tests     # unit tests
```

Releasing (Velopack + GitHub Releases) is documented in
**[RELEASING.md](RELEASING.md)**.

## Project structure

```
src/Musebase.Core/     # UI-agnostic domain: LRC parsing, providers, ranking, translation, cache
src/Musebase.Engine/   # UI-agnostic orchestration: playback contracts, coordinator, view state
src/Musebase.Windows/  # WPF app: SMTC detection, sync, overlay, tray, settings, i18n
contracts/             # serialized contracts shared across platforms (PlaybackViewState)
tools/                 # mt-bootstrap.ps1 (DeepL translation seeds)
spikes/                # technical spikes (SMTC / overlay / search)
```

## License

Musebase for Windows is licensed under the **[Mozilla Public License 2.0](LICENSE)** (MPL-2.0).

It is based on [LyricsX](https://github.com/ddddxxx/LyricsX) and
[LyricsKit](https://github.com/ddddxxx/LyricsKit) by **ddddxxx** (both MPL-2.0) — the
lyrics-parsing and lyrics-search logic in `src/Musebase.Core` is ported from LyricsKit.
Files ported from LyricsKit remain governed by the MPL-2.0, and their provenance is noted
in each file's header comment.
