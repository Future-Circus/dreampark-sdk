# DreamPark brand fonts (Unity / TextMesh Pro)

The DreamPark Web brand faces are used here so that headsets, the phone's AR and titles share one type voice
(PLAYLISTS.md § 11; first used by the playlist bookends):

| Role | Face | File | TMP asset (Resources) |
|---|---|---|---|
| Display / headings | **Zalando Sans Expanded ExtraBold (800)**: the Web's `--lp-display` (landing page, DreamJam) | `ZalandoSansExpanded-ExtraBold.ttf` | `DreamParkFonts/DreamPark Display SDF` |
| Body / UI | **DM Sans SemiBold (600)**: the Web's `--lp-body` | `DMSans-SemiBold.ttf` | `DreamParkFonts/DreamPark Body SDF` |

The iOS app uses SF Pro (heavy/black). SF cannot ship on Quest or in Unity builds; Zalando Sans Expanded is the
brand's display face on the Web and the closest licensed match to the DREAMPARK wordmark's wide, heavy letterforms.

**Source:** DreamPark-Web `public/fonts/files/*.woff2` (self-hosted from @fontsource, i.e. the Google Fonts
releases), converted to TTF with fontTools and merged latin + latin-ext.

**Licence:** both faces are licensed under the **SIL Open Font License, Version 1.1** (https://openfontlicense.org).
- Zalando Sans: Copyright Zalando SE and the Zalando Sans project authors.
- DM Sans: Copyright The DM Sans Project Authors (Colophon Foundry / Google).

The OFL allows bundling and embedding them in software, including commercial apps, provided the fonts are
not sold by themselves. They must keep this copyright and licence notice and must not be renamed if modified.
The full licence text is at https://openfontlicense.org/open-font-license-official-text/.

**TMP atlases:** SDF (SDFAA), sampling 90 pt, padding 9, 1024², static, printable ASCII + Latin-1 + common
punctuation (… — – ’ “ ” • ·). Characters outside the atlas fall back to TMP's default font.
