# AGENTS.md — GlamSpector

## Purpose

GlamSpector is a private-development Dalamud plugin for FINAL FANTASY XIV that captures inspected glamours and keeps a searchable local Glamour Library.

This repository is developed iteratively with the user. Product/design decisions are normally discussed in the user's ChatGPT FFXIV project; Codex is the implementation/build workspace.

## Current baseline

- Current milestone at handoff: **M3.15.2**
- Project SDK: `Dalamud.NET.Sdk/15.0.0`
- Project version: `0.3.15.2`
- Current resolved target in `packages.lock.json`: `net10.0-windows7.0`
- Important packages include `Microsoft.Data.Sqlite`, `SQLitePCLRaw.bundle_e_sqlite3`, `SixLabors.ImageSharp`, and `SixLabors.ImageSharp.Drawing`.

Do not assume the local machine is correctly configured. Inspect the project and run restore/build to establish the actual environment.

## First task in a fresh environment

Before modifying code:

1. Inspect `GlamSpector.csproj`, `packages.lock.json`, and repository status.
2. Determine the required .NET SDK and Dalamud development environment.
3. Run the appropriate restore/build command.
4. Report exact missing dependencies or compile errors.
5. Do **not** make opportunistic code changes merely to silence errors until the cause is understood.

Once the environment is healthy, builds should be part of normal validation for every implementation change.

## Working style

- Prefer **small, reviewable changes** over broad rewrites.
- Preserve existing behavior unless the requested milestone explicitly changes it.
- Do not redesign user-facing workflows without a product decision.
- If requirements are ambiguous, explain the ambiguity rather than guessing a new product rule.
- Keep version/manifest/README changes synchronized when making a milestone release.
- Preserve compatibility with existing Library data whenever practical.
- Database/schema changes must include safe migration behavior.
- Do not silently delete or relocate users' existing media/data.
- Do not commit local runtime data, screenshots, personal Library databases, machine-specific configuration, secrets, or build output.
- Never commit `bin/`, `obj/`, `.vs/`, or IDE-local files.

## Safety and FFXIV/Dalamud constraints

### Native UI/game actions

Some FFXIV native UI operations are unsafe from ImGui draw callbacks.

Existing code deliberately queues native operations through `Framework.Update` where needed. In particular:

- native Try On/chat-link operations are queued;
- `AtkUnitBase.Focus()` for Inspect capture preparation is performed on `Framework.Update`, not during ImGui `Draw`.

Do not move such native mutations back into ImGui draw code without strong evidence that it is safe.

### Ownership semantics

Ownership is intentionally **best effort**.

GlamSpector checks sources such as:

- Inventory/equipped gear
- Armoury Chest
- Saddlebags
- currently loaded retainer containers
- FFXIV's cached Glamour Dresser data
- expanded Outfit Glamour pieces when available/cached
- Armoire when Cabinet data is loaded
- Facewear unlock state

A negative lookup is **not proof that an item is missing**, because some storage may be unloaded or unavailable. The UI therefore uses unverified/unknown semantics (`?`) rather than confidently saying "No".

Do not change this into a definitive missing/not-owned claim unless all relevant storage sources can actually be proven.

### Eorzea Collection import

Eorzea Collection import is deliberately **manual-only**. GlamSpector may open
one supplied glamour URL in the user's normal browser, but it must make no HTTP
requests to EC and must not download EC images, read browser cookies, bypass
anti-bot protection, crawl, or scrape. The only supported import input is page
source that the user copied from their browser and pasted into GlamSpector for
local parsing. Do not "improve" this policy back into automated fetching.

### Sharing/privacy

Personal Library metadata is local/private unless a sharing format explicitly includes it.

Examples of local/private metadata:
- ratings
- tags
- notes
- Wanted state

Glam Codes intentionally exclude screenshots, Adventurer Plates, character/world/FC identity, ratings, tags, notes, and Wanted state.

Do not broaden exported/shared data without an explicit product decision.

## Current product model: preview-first Library

M3.14 changed the Library from a card-first model to a **preview-first** model.

Important rules:

- Managed captures keep an Inspect character preview.
- Inspect/personal preview imagery is preferred as the Library visual over a full Glam Card.
- Every fresh personal Fitting Room preview becomes Primary automatically.
- Existing user-selected personal Primary previews should be preserved where migration logic already handles them.
- `My Previews` is a gallery of up to **3 previews per row**, wrapping to additional rows.
- Full cards/source images are secondary/reference/share media.
- Personal previews can generate **Share Cards** from the saved gear/dye/Facewear recipe.
- Generated Share Cards are independent media files; deleting the source preview must not implicitly delete an already-generated Share Card.
- CharacterInspect's raw CharaView contains the native cyan item-level stamp.
  `GlamCardRenderer.PreparePreview` is the single preparation path for both the
  Full Card portrait and saved automatic Inspect Preview; do not add a second
  standalone cleanup/crop algorithm.
- Automatic Inspect previews are frame-free prepared portraits. Personal
  Fitting Room previews remain native framed captures and use component node 31;
  `bottomRatio: 0.879` remains their fallback only.

Terminology matters:
- "preview" means character/appearance imagery;
- "Full Card" is the captured card;
- "Share Card" is a GlamSpector-generated shareable card;
- "Adventurer Plate" means the actual FFXIV Adventurer Plate, not a generated share card.

## Destructive-action semantics

Keep these concepts visibly and behaviorally distinct:

- **Delete preview**: removes only that individual personal preview.
- **Remove from library**: removes the Library record while leaving disk files.
- **Delete entry & files**: permanently removes the Library entry and its tracked files.

Do not reintroduce ambiguous wording such as "Delete capture" for whole-entry deletion.

## Library UI direction

The UI was reworked in M3.13 to reduce accumulated button clutter.

Current hierarchy:
- high-frequency library controls at the top;
- Import actions grouped under `Import…`;
- maintenance under `Library tools…`;
- `Try on glam` and `Capture my preview` are primary selected-entry actions;
- media controls live with the media they affect;
- quieter file/share actions live under `Files & sharing`;
- destructive entry actions are isolated under `Library entry`.

Preserve this visual hierarchy when adding new actions.

## Validation expectations

For meaningful code changes, aim to perform:

1. `git diff` review
2. restore/build
3. relevant focused checks/tests available in the repo
4. schema/migration review if storage changed
5. manual in-game test instructions for behavior that cannot be validated outside FFXIV

A successful compile does not replace in-game testing for:
- native addon timing/focus
- screenshots/cropping
- Try On
- ownership cache behavior
- ImGui layout
- FFXIV clipboard/media interaction

## Repository documentation

Read these before broad work:
- `README.md` — milestone/change history and user test notes
- `PROJECT_HISTORY.md` — durable product/design decisions
- `ROADMAP.md` — current direction and candidate next work

If these disagree with current code, report the discrepancy rather than silently choosing one.
