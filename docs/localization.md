# Localization (stage 1)

How player-facing text works in Chibi Clash, how to add a language, and what the current fonts cannot show.
Progress and open items: `docs/localization-progress.md`.

## Rules in one screen

- Every player-facing string is looked up with `Loc.T("key")` (Assets/ChibiClash/Scripts/Core/Loc.cs). English lives in
  `Assets/ChibiClash/Resources/Loc/en.json`, the source of truth and the fallback.
- **Stays English everywhere** (user rule, 2026-11-24): character names (Kaia, Bruna, Ren, Lyra, Taro), skill names,
  passive names and ultimate names, the comic fight words drawn in matches ("SMASH!", "Pew!", "PARRY", "Woom!",
  "FIGHT!", "KO", "FIRE!", "CRIT!", "REFLECT!", "RING OUT" ...), the game title CHIBI CLASH, keyboard and gamepad key
  names (SPACE, ESC, D-PAD, LB / RB), and anything a player typed (player names, room names, default name suggestions).
- **Not localized on purpose:** debug / dev logs, the dev automation (`-devName` ...), network protocol strings (lobby
  names, handshake data, save keys) and the Unity Cloud setup messages (`NotLinkedMessage`, the "not linked" text in
  `NetworkRoomManager.Explain`), which only a developer sees. Weapon item names (Twin Curved Swords, Shark Sword ...) are
  kept English as proper item names; change that by adding keys if the producer wants them translated.
- **The language is per device.** It is saved in `SaveData.language` and never sent over the network: each player in
  an online match sees their own language. Statuses such as "Room X is ready" are built on the device that shows them.

## The language window

- First launch order (user decision 2026-11-24): **age -> language -> name**. The window opens by itself once, when
  `SaveData.language` is empty and the age is known. Before the choice the game already uses the device language
  (`Loc.DeviceLanguage()`: `Application.systemLanguage`, else the OS culture captured before
  `NetworkRoomManager.UseInvariantCulture` switches the process to the invariant culture), so a Thai PC shows the age
  question in Thai.
- Reopen it from **SETTINGS > LANGUAGE** or the **globe button** at the top right of every page with the top bar
  (under the profile chip). Choosing a language switches the whole UI at once (`Loc.Version` changes, caches rebuild).
- Editor shortcut: `GameManager.ShowScreen("language")` opens it over SETTINGS.
- Every ISO 639-1 language is listed (183 codes in `Resources/Loc/languages.json`). A language without a table can be
  chosen; its text is English until a table ships (the list says "English text for now").
- Controls: type to search (English name, native name or code); Up / Down / Page Up / Page Down or the D-pad move;
  Enter or pad A confirms; a click picks, a second click on the pick confirms; Esc or pad B closes (not on first launch).

## File format

`Resources/Loc/<code>.json`, one file per language, a flat JSON object of string values:

```json
{
  "common.back": "BACK",
  "arcade.round": "ROUND {0}"
}
```

- UTF-8. Standard JSON escapes (`\n`, `\"`, `\\`, `\uXXXX`). Keys starting with `//` are comments and are ignored.
- `{0}`, `{1}`, `{2}` are arguments (C# `string.Format`, invariant culture). A translation may move them but must keep
  the same set; a broken pattern shows the raw pattern instead of throwing.
- IMGUI rich text (`<color=#9AA6BC>...</color>`, `<size=12>`) is allowed and must be kept.
- `languages.json` is generated: `python -P tools/loc/gen_languages.py` (code, English name, endonym, ISO 15924 script,
  RTL flag, `translated` = a table exists).

## Key naming

`area.item` or `area.screen.item`, lower case, `_` between words: `nav.home`, `settings.reset_keys`,
`net.searching_in`, `class.kaia.skill1_desc`, `stage.3.tagline`. The area says where it appears (`hud`, `results`,
`select`, `rooms`, `room`, `quick`, `invite`, `friends`, `net`, `name`, `age`, `lang`, `settings`, `training`,
`collection`, `arcade`, `play`, `online`, `precheck`, `loading`, `status`, `end`, `rewards`, `quest`, `bind`, `cv`,
`cam`, `pose`, `region`, `char`, `class`, `stage`, `skin`, `rarity`, `gesture`, `common`, `hint`). `common.*` is for
words reused across screens (BACK, CANCEL, OK).

A few keys are built at run time from data (`class.<hero>.passive1_desc`, `char.<id>.role`, `stage.<i>.name`,
`skin.<id>`, `rarity.<name>`, `quest.<id>`, `region.<english_name>`); the English values are written next to the data
and the accessors are `ClassDef.PassiveDesc/SkillDesc/UltDesc`, `CharacterInfo.RoleText/StyleText`,
`StageBuilder.NameText/ShortNameText/TaglineText`, `Quest.DescText`.

## Fallback rules

1. The chosen language's table, if it has the key.
2. English (`en.json`).
3. The key itself. This is a bug, never a design: the EditMode test `EveryKeyUsedInCode_ExistsInEnglish` fails on it.

A missing or unreadable table logs one warning and the language falls back to English as a whole.

## Adding or changing text (developers)

1. Write `Loc.T("area.item")` (or `Loc.T("area.item", value)`) instead of the literal. Never build a sentence from
   pieces: put the whole sentence with `{0}` in one key so translators can reorder it.
2. Add the key and the English text to `en.json` (or run `tools/loc/apply.py <file.cs> <spec.txt>` to move literals).
3. Text drawn every frame: `Loc.T("key")` does not allocate. A formatted value that changes rarely should be cached
   like the HUD does (`LocCache`, or a cache cleared when `Loc.Version` changes, see `Hud.UltPct`).
4. `python -P tools/loc/list_literals.py <file.cs>` lists literals that still look player-facing.

## Adding a language (translators)

1. Copy the keys of `en.json` into `tools/loc/<code>/*.tsv` sheets, one `key<TAB>text` per line (`\n` = line break).
   Thai is the worked example: `tools/loc/th/part1.tsv` .. `part3.tsv`.
2. `python -P tools/loc/build_table.py <code>` writes `Resources/Loc/<code>.json` in English key order. It fails on
   unknown keys and on `{n}` mismatches, and lists missing keys (they show English in game).
3. `python -P tools/loc/gen_languages.py` flips `translated` to true for the new code.
4. Run the EditMode tests (`LocTests`) and look at every screen in the language (pseudo-long text, line breaks).
5. Check the font section below for the language's script before promising it.

## Fonts: what the current approach cannot render (stage 3 work)

`Gui` draws with OS fonts through `Font.CreateDynamicFontFromOSFont` (display: Anton / Bebas Neue / Oswald / Impact ...,
body: Barlow Condensed / Oswald / Bahnschrift ...), with `Segoe UI Symbol`, `Arial`, and (stage 1 stop-gap)
`Leelawadee UI` and `Tahoma` as glyph fallbacks so Thai renders on Windows. Unity's legacy IMGUI text has **no complex
text shaping and no bidirectional layout**.

| Situation | Renders | Does not render correctly |
|---|---|---|
| **WebGL build** | Only the built-in default font: Latin, Greek, Cyrillic basics. `GetOSInstalledFontNames` and OS font loading are not available in the browser. | **Every other script, including Thai**: blank boxes or nothing. Thai, CJK, Arabic, Hebrew, Indic, etc. need a bundled font asset before they can ship on WebGL. |
| Windows PC | Latin incl. extended (Vietnamese diacritics stack imperfectly in condensed fonts), Greek, Cyrillic, Armenian / Georgian (if Sylfaen / Segoe UI fall back), Thai (Leelawadee UI / Tahoma). | Arabic-script languages (ar, fa, ur, ps, sd, ug, ks): letters are drawn isolated and left-to-right, unreadable. Hebrew, Yiddish, Divehi: drawn left-to-right (no bidi), no layout mirroring. Indic (hi, mr, ne, sa, pi, bn, as, pa, gu, or, ta, te, kn, ml, si): conjuncts and vowel signs are not shaped. Khmer, Lao, Myanmar, Tibetan, Dzongkha: not shaped. CJK (zh, ja, ko) and Yi: only if a CJK font is picked up by OS fallback; not in the font lists, so expect tofu. Ethiopic (am, ti), Canadian syllabics (cr, iu, oj): only via OS fallback. |
| macOS / Linux | Font names in the lists are Windows-centric; results depend on installed families. | Same shaping limits as Windows. |
| Thai specifics | Readable. | Stacked tone marks over upper vowels can overlap (no mark positioning); no word-break dictionary, so wrapping breaks only at spaces. Thai strings here are written with spaces between phrases for that reason. |

Stage 3 (fonts) needs: bundled font assets per script group (TextMesh Pro / UI Toolkit text with HarfBuzz-style
shaping or pre-shaped text), RTL layout mirroring for ar / fa / ur / he, line breaking for th / lo / km / my / CJK, and a
font matrix in this doc. Until then, ship only Latin / Greek / Cyrillic / Thai tables on desktop, and Latin / Greek /
Cyrillic on WebGL.

## Tests

`Assets/Tests/EditMode/Editor/LocTests.cs`: every key used in code exists in `en.json`; `th.json` (and every table)
has no keys outside English; `{n}` placeholders match per key; `languages.json` has unique two-letter codes, endonyms,
and `translated` flags that match the tables; the parser, the English fallback, the device-language mapping and the
search. The table checks read the shipped files on purpose (content validation).
