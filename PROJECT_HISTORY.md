# PROJECT_HISTORY.md — GlamSpector durable context

This is a **handoff summary**, not a transcript. It records product decisions and lessons that should survive across Codex threads. `README.md` remains the detailed milestone log.

## What GlamSpector is becoming

GlamSpector began around capturing inspected glamours, but evolved into a personal **Glamour Library and acquisition workflow**.

The important product direction is:

> The Library should be centered on the outfit and the user's own preview shots. Cards and generated layouts are secondary/shareable derivatives.

The plugin should help answer:
- What does this glam look like?
- Which items/dyes are in it?
- What do I already own?
- What is still unverified / worth adding to Wanted?
- Can I try it on?
- Can I keep several good screenshots?
- Can I share the recipe or a nice card without exposing private Library metadata?

## Key durable decisions

### Local-first, private by default

The Library is local. Ratings, tags, notes, and Wanted state are personal metadata.

Sharing formats are intentionally narrower:
- `.glamspector.zip` is the richer package format.
- Glam Codes are compact recipe text and deliberately omit personal identity/private Library state.
- Share Cards are visual derivatives for easy sharing.

Do not casually add private metadata to exports.

### Ownership is evidence-based, not absolute

The project explicitly rejected treating "not found in currently available storage" as proof of not owning an item.

Unloaded retainers and unavailable caches can hide real ownership. The UI therefore distinguishes verified owned from unverified/unknown.

The ownership implementation grew to use:
- inventory/equipped
- Armoury
- saddlebags
- loaded retainers
- cached Glamour Dresser
- live/cached expanded Outfit Glamour pieces
- Armoire when loaded
- Facewear unlocks

Expanded Glamour Dresser/Outfit ownership is persisted per character so the user can seed it by opening the Dresser and later retain useful knowledge across reloads.

### Native FFXIV operations must respect threading/timing

Several features touch native FFXIV UI/game state. A lesson from development is to avoid mutating native addon structures during ImGui drawing.

Native actions such as Try On/chat operations and Inspect focusing are intentionally queued to `Framework.Update` where appropriate.

### Eorzea Collection import must stay respectful

EC import is single-page, user-directed import only. No site crawl.

When direct HTTP access gets a 403:
- stop;
- do not bypass anti-bot protections;
- allow the user to paste page source from their own browser;
- parse that supplied source.

### Preserve old Library data

The project has repeatedly favored in-place migration and backward compatibility over forced rewrites.

For example, when media moved toward per-entry folders, existing flat captures remained supported rather than being bulk-moved automatically.

This should remain the default migration philosophy.

## Milestone evolution

### M3.7 — ownership, ratings, interactive item actions

The Library gained stronger ownership hints and local ratings.

Important behaviors:
- item context menu supports single-item Try On, native chat-link insertion, and copying item names;
- native operations are queued safely;
- missing ownership evidence displays as unknown, not "No";
- ratings are private and sortable.

Outfit Glamour diagnostics led to reading the live Glamour Dresser's expanded PrismBox item list and persisting expanded ownership per character.

### M3.8 — Wanted items and ownership progress

Wanted became personal Library metadata.

Features included:
- mark/remove Wanted per item;
- Wanted window;
- clear currently verified-owned Wanted items;
- per-glam verified ownership progress;
- helper to mark unverified pieces wanted.

The longer-term intent is to make "what do I still need for this glam?" a first-class workflow.

### M3.9 — filters, tags, notes

The Library gained:
- rating/ownership/Wanted/Plate filters;
- private tags;
- private notes;
- search across tags/notes.

These remain local and excluded from sharing.

### M3.10 — Glam Codes

A compact `GS1:...` text format was added.

It carries the visible glamour recipe:
- gear
- dye channels
- Facewear

It deliberately excludes:
- screenshots
- Adventurer Plates
- character/world/FC identity
- ratings
- tags
- notes
- Wanted state

A checksum rejects damaged/truncated codes.

### M3.11 — Eorzea Collection single-page import

The Library can import one EC glamour at a time.

Imported entries participate in normal Library functionality such as:
- Try On
- ownership/Wanted
- ratings
- tags/notes
- Glam Codes

The browser-source fallback was added after direct plugin HTTP could receive 403 even though the page opened normally in a browser.

### M3.12 — personal Fitting Room previews and managed media

This was a major shift toward keeping personal views of a saved glam.

Workflow:
1. select a structured Library entry;
2. `Try on glam`;
3. pose/rotate/zoom in FFXIV's native Fitting Room;
4. `Capture my preview`;
5. save multiple personal previews.

The project also moved new media toward managed per-entry folders while retaining compatibility with existing legacy paths.

`File size` sorting was added.

A critical wording problem was discovered: a whole-entry destructive action named "Delete capture" could be mistaken for deleting a preview. It was changed to **Delete entry & files…**.

An ImGui ID collision also caused personal-preview `Open PNG` / `Open folder` buttons to malfunction because identical visible labels were reused elsewhere. Hidden unique IDs fixed this. When adding repeated controls, always consider ImGui ID uniqueness.

### M3.13 — Library UI cleanup

The Library had accumulated features organically and became button-heavy. Rather than continue adding buttons at equal priority, the UI was reorganized by workflow.

High-frequency toolbar:
- Search
- Refresh
- Settings
- Import…
- Wanted
- Filters
- Sort
- Library tools…

Selected glam:
- `Try on glam`
- `Capture my preview`
- secondary recipe/Wanted actions

Media:
- controls associated with the media itself

Secondary:
- `Files & sharing`

Destructive:
- isolated `Library entry` section

This hierarchy was well received and should not regress into a flat button wall.

### M3.14 — preview-first Library

This milestone formalized the product direction.

The full card no longer needs to be the default visual because the Library itself already shows the item list.

Decisions:
- character preview imagery should be the default visual;
- full cards remain useful as secondary/shareable reference;
- personal Fitting Room previews are first-class Library media;
- newest fresh personal preview becomes Primary automatically;
- gallery shows up to 3 previews per row;
- a personal preview can generate a **Share Card** from the saved item/dye/Facewear recipe;
- generated Share Cards remain independently stored even if their source preview is deleted;
- the actual FFXIV Adventurer Plate remains a separate concept/media type.

The 3-across preview gallery was specifically chosen because tall character previews use horizontal Library space much more efficiently this way.

The Fitting Room crop was iteratively tuned to preserve the thin native frame while excluding the bottom circular controls; current value is `bottomRatio: 0.879`.

### M3.15 — Library identity and memory

Library entries gained a local, editable display title without replacing captured/imported identity. Existing non-EC entries retain their prior `Character @ World` label during migration; EC entries use their saved source title while source creator/title/URL remain separately preserved and displayed. Renaming is intentionally local and does not affect recipe identity, media paths, Glam Codes, sharing metadata, or duplicate detection.

Useful navigation state now persists across reloads: sort, filters, filter-bar visibility, left-column width, selected entry when still available, and secondary-section expansion. Search text and transient edit/confirmation/dialog state remain session-only. Tags & notes are collapsed by default to protect the M3.13 visual hierarchy.

### M3.15.1 — Capture lifecycle stability

Native capture state must always have a bounded exit path. Automatic Adventurer Plate capture keeps an overall deadline active through its render-settle phase, and a Plate that closes or changes identity while settling terminates the attempt instead of leaving the plugin globally busy. Inspect preview requests are cancellation-bound, preparation is tied to the inspected entity, and abandoned texture requests are never allowed to complete later against a different target.

CharacterInspect addon readiness is not content identity: the native addon/agent can remain allocated while its inspected entity and Examine data change. Future native capture work should validate the entity across preparation and sampling, clear observation caches when Inspect disappears, and include plugin-owned lifecycle state in diagnostics rather than reporting only addon visibility/readiness.

### M3.15.2 — Preview/import/update polish

CharacterInspect's raw CharaView contains the native cyan item-level stamp. The
automatic Inspect Preview therefore saves the exact frame-free portrait produced
once by `GlamCardRenderer.PreparePreview` for the Full Card, including the same
configured cleanup behavior. Separate automatic-preview crop, masking and
background-reconstruction experiments were removed. Personal Fitting Room
previews remain independent native framed captures; their component-node path
retains `bottomRatio: 0.879` as fallback.

Eorzea Collection import is intentionally manual-only. GlamSpector may open a
user-supplied page in the normal browser, but performs no EC HTTP requests or
remote image downloads. It parses only HTML the user pastes and preserves any
legacy cached source-image paths/files. This is a product/privacy policy, not a
temporary fallback to be automated later.

Plugin update messages use the running assembly version and a persisted
last-seen version. A saved pre-M3.15.2 configuration reliably identifies an
existing installation for the initial announcement; a genuinely new config is
bootstrapped silently. Same-version reloads and downgrades do not announce.

## Product/usability lessons so far

1. **Feature hierarchy matters.** New features should be grouped by workflow rather than appended as another equal-priority button.
2. **Wording must describe scope.** "Delete preview" and "Delete entry & files" are intentionally explicit.
3. **The user's own character previews are more valuable for browsing than large source cards.**
4. **Cards are still useful**, especially for Discord/sharing, but are not the Library's main visual truth.
5. **User-controlled workflows beat aggressive automation** when working with third-party sites or native FFXIV state.
6. **Do not overstate ownership certainty.**
7. **Backward-compatible storage changes are preferred** to destructive migrations.
8. **In-game screenshots are the final UI truth.** Layout/crop/native addon behavior must be tested in FFXIV even after a clean build.

## Collaboration model

The user's ChatGPT **FFXIV project** remains the product/design conversation space.

Codex is the repository/build implementation space.

When a Codex task involves a meaningful product choice rather than an implementation detail:
- do not invent the decision;
- surface alternatives/tradeoffs;
- return the question to the product conversation when appropriate.

The goal is for the repository documents to carry durable context, while the FFXIV project retains the richer conversational history.
