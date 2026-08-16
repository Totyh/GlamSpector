# ROADMAP.md — GlamSpector

Current baseline: **M3.15.0, Library identity & memory**

Version labels below are planning candidates and can change after discussion. Do not treat an unimplemented roadmap item as an approved behavior unless the user explicitly requests it.

## Engineering baseline

Completed before M3.15:

- established a working local Codex build environment;
- confirmed .NET 10 and `Dalamud.NET.Sdk/15.0.0` against the XIVLauncher Dalamud development assemblies;
- verified locked restore and Debug builds;
- retained clean builds as part of milestone handoff.

## Completed in M3.15

- Editable local Library titles are stored separately from captured/imported source identity.
- Eorzea Collection source title, creator and URL remain source attribution and are displayed separately from the local title.
- Existing Library rows migrate in place without moving media or requiring re-import.
- Sort, filters, left-column width, selected entry and secondary-section expansion state are remembered with invalid-value fallbacks.
- Search and transient dialogs/edit/confirmation state remain session-only.
- Tags & notes are compact/collapsed by default.

## Short-term polish candidates

### Filesystem resilience

Audit all media/file operations together.

Candidate work:
- gracefully detect missing/moved files;
- avoid exceptions when folders have been manually changed;
- cleanup orphaned generated media where safe;
- make duplicate detection media-aware;
- regression-test `Open PNG`, `Folder`, export, source links, Share Cards, and full deletion.

No cleanup action should delete files merely because GlamSpector is uncertain about ownership/reference.

## Acquisition workflow

This is the next major functional direction.

Goal:

> Make a saved glam immediately answer "what do I still need to obtain?"

### Per-item ownership/Wanted clarity

Candidate table states should visually distinguish:
- verified owned;
- unverified/unknown;
- Wanted;
- combinations such as Wanted + currently unverified.

Do **not** invent a definitive "missing" state while ownership coverage remains incomplete.

### Direct Wanted controls in the item table

Make Wanted a natural per-item action instead of relying mainly on entry-level helpers.

Potential behaviors:
- compact toggle/action directly from the table;
- keep right-click action;
- retain `Mark unverified pieces wanted` as a bulk helper if useful.

### Missing/unverified-focused views

Possible filters:
- show only entries with Wanted pieces;
- show only entries not fully verified owned;
- within a glam, emphasize unresolved pieces.

Terminology must remain consistent with best-effort ownership semantics.

### Acquisition/source information

Longer-term candidate:
- show where an item comes from when reliable source data is available;
- help turn Wanted into an actionable shopping/farming list.

This should be designed around reliable FFXIV data rather than scraped third-party assumptions.

## Library intelligence

After the acquisition workflow is solid, add library-wide insights that use existing local data.

Candidate insights:
- glams where the user already owns most pieces;
- glams with only 1–2 unverified/Wanted pieces;
- items shared across many saved glams;
- duplicate/near-duplicate recipes;
- largest entries by disk usage;
- media cleanup opportunities;
- glams that are easy to complete based on verified ownership.

Avoid pseudo-certainty: "you own 6/7 verified" is acceptable; "you only need one item" is not unless the final item is truly proven absent and all coverage is complete.

## Preview/media future ideas

M3.14 established the basic model, but possible refinements remain.

Candidate:
- clearer primary-image indicator;
- optional preview reordering if a real workflow need emerges;
- responsive gallery sizing beyond the current 3-per-row target;
- additional Share Card layouts/templates;
- richer generated cards using title/source/notes only where privacy rules are explicit;
- remember preferred media view.

Do not let Share Cards replace the independent saved recipe; they are derivatives.

## Out of scope / intentionally constrained

Unless explicitly reconsidered:

- no bulk Eorzea Collection scraping;
- no anti-bot bypass/cookie borrowing;
- no automatic chat-message sending;
- no definitive "not owned" claims from incomplete storage coverage;
- no forced migration that unnecessarily moves old user media;
- no silent export of ratings/tags/notes/Wanted/private identity;
- no destructive cleanup without clear scope and confirmation.

## Definition of a good milestone

A milestone is ready to hand back for in-game testing when:

- requested behavior is implemented without unrelated redesign;
- project version/manifest/docs are consistent;
- repository diff is reviewed;
- restore/build succeeds;
- migrations are safe and reviewed if applicable;
- known limitations are stated;
- a concise manual FFXIV test plan is supplied.

The user performs the final in-game validation for layout, native UI timing, screenshot/crop behavior, and game-state-dependent features.
