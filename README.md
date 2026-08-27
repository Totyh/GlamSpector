# GlamSpector

GlamSpector is a Dalamud plugin for capturing inspected FINAL FANTASY XIV
glamours and keeping them in a searchable, local-first Glamour Library. It saves
the visible equipment, dyes and Facewear with previews, then helps you try the
look on, track ownership and Wanted pieces, and create privacy-conscious sharing
formats.

## Installation

GlamSpector is distributed through a third-party Dalamud custom repository. It
is not an official Dalamud listing.

1. Open `/xlsettings`.
2. Select **Experimental** and open **Custom Plugin Repositories**.
3. Add this URL:

   `https://raw.githubusercontent.com/Totyh/GlamSpector/main/repo.json`

4. Save/refresh repositories.
5. Open `/xlplugins`, search for **GlamSpector**, and choose **Install**.

No compilation is required. Released updates are delivered through Dalamud.

## Getting started

1. Inspect another character in game.
2. Use the **Capture** action on CharacterInspect, or run
   `/glamspector capture`.
3. Open `/glamspector library` to browse the saved outfit.
4. Use **Try on glam**, **Capture my preview**, Wanted controls, ratings, tags,
   notes, or the media/share actions as needed.

Useful commands:

- `/glamspector` or `/glamspector library` — open the Library;
- `/glamspector capture` — capture the current CharacterInspect outfit;
- `/glamspector config` — open settings;
- `/glamspector debug` — print concise capture, Library, and optional-integration diagnostics.

## Main features

- Preview-first Library with fast search, sorting, filters and remembered layout.
- Inspect previews, personal Fitting Room previews, Full Cards, Share Cards and
  optional Adventurer Plate attachment.
- Saved equipment, both dye channels and Facewear, with Try On and native item
  actions.
- Best-effort ownership evidence and private Wanted tracking. Optional Allagan
  Tools IPC can verify additional positives from cached personal storage.
- Local ratings, tags, notes and editable display titles.
- Manual Eorzea Collection import from page source copied by the user.
- Glam Codes and managed package export/import for sharing recipes.

## Ownership and Wanted

Ownership is deliberately conservative. A check mark means GlamSpector found
positive evidence. `?` means unverified—not “you do not own this.” Unloaded or
unavailable storage can hide real ownership.

The optional **Enable Allagan Tools integration** setting under **Settings →
Integrations → Allagan Tools** uses
local Dalamud IPC only. It can supplement positive evidence from the active
character's personal cached storage; zero results remain `?`. It does not upload
or share data, and GlamSpector works normally without Allagan Tools.

## Local data and sharing

The Library database and media stay in GlamSpector's local Dalamud configuration
area/output folder. Ratings, tags, notes and Wanted state are private local
metadata unless a future sharing format explicitly says otherwise.

Glam Codes intentionally omit screenshots, Adventurer Plates,
character/world/Free Company identity, ratings, tags, notes and Wanted state.
Eorzea Collection import performs no HTTP requests: GlamSpector only parses HTML
that you paste after viewing the page source in your own browser.

## Troubleshooting

- If an item remains `?`, open relevant storage (especially the Glamour Dresser)
  and use **Refresh ownership**. A negative lookup is still not definitive.
- If Allagan Tools supplementation is enabled but unavailable, confirm Allagan
  Tools is loaded and initialized. GlamSpector will keep working with native
  checks and retry locally without repeated errors.
- On platforms where Dalamud image clipboard support is unavailable, captures
  still save normally; GlamSpector reports the clipboard limitation once per
  session.
- Use `/glamspector debug` when reporting capture, Library-performance, or
  Allagan Tools availability issues.

## More information

- [CHANGELOG.md](CHANGELOG.md) — concise user-facing release history.
- [PROJECT_HISTORY.md](PROJECT_HISTORY.md) — detailed engineering and product decisions.
- [ROADMAP.md](ROADMAP.md) — current direction and candidate future work.
- [Source repository](https://github.com/Totyh/GlamSpector).

Local DLL loading through Dalamud **Dev Plugin Locations** remains available for
development/testing builds only. Normal users should install from the custom
repository above.
