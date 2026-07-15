# Translating LyricsX

**English** · [한국어](TRANSLATING.md)

LyricsX's UI is translated via per-locale JSON catalogs. You can contribute **directly on GitHub** —
edit a file and open a pull request, or suggest a fix via an issue. No translation platform needed.

- Catalogs: **[`src/LyricsX.App/i18n/`](https://github.com/countnine/LyricsX-Windows/tree/master/src/LyricsX.App/i18n)** (e.g. `ko.json`, `ja.json`, `de.json`)
- Reference (source) language: **English (`en.json`)** — the basis for all other languages
- Format: flat `key → string` JSON. Arguments/plurals use **ICU MessageFormat** (`{value}`, `{count, plural, ...}`)

## Option 1) Edit on GitHub → PR (recommended)

1. Open the language file (e.g. `ko.json`) in the `i18n/` folder above. If it doesn't exist yet, copy `en.json` to create it.
2. Click the **pencil (✏️ Edit) icon** at the top right. You only need a GitHub account.
3. Translate/fix the values. **Keep the keys (left side) and curly-brace placeholders unchanged.**
4. Click **"Propose changes"** → **"Create pull request"**; a fork and PR are created automatically.
5. Once a maintainer reviews and merges, it ships to users via the next release's auto-update.

## Option 2) Suggest via an issue (if you're not comfortable with Git)

[**Open a translation suggestion issue**](https://github.com/countnine/LyricsX-Windows/issues/new?template=translation.yml) —
just fill in the language, key, and suggested text. A maintainer will apply it.

## Translation notes

- Keep **curly-brace placeholders** like `{value}`, `{version}`, `{track}` unchanged (do not translate them).
- For plural syntax `{count, plural, one {...} other {...}}`, keep the structure and translate only the inner text.
- Leave markers like `[mm:ss.xx]`, `[tr]`, `[tt]`, `【 】` as-is.
- Languages other than en/ko are **machine-translation (DeepL) seeds** and may read awkwardly — polishing is very welcome.

## Adding a new language

The supported-language list is in [`Services/Localization.cs`](https://github.com/countnine/LyricsX-Windows/blob/master/src/LyricsX.App/Services/Localization.cs)
(`SupportedLanguages`). To add one, add a `LanguageOption(code, nativeName)` entry there and create
`src/LyricsX.App/i18n/<code>.json` (a missing file falls back to English).

## Maintainer notes

- Regenerate MT seeds: `tools/mt-bootstrap.ps1` (DeepL). ICU placeholders are protected with XML tags and validated.
- If it grows, you can migrate to a self-hosted open-source TMS (**Tolgee** / **Weblate**) or Hosted Weblate
  (libre, needs approval). The current JSON + git structure is reused as-is.
