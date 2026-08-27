# GlamSpector

## M3.15.6 — Portable card-font fallback

- Removes the eager `Segoe UI` lookup from plugin construction, so GlamSpector
  can start even when SixLabors.Fonts cannot resolve that family.
- Card rendering resolves one font per operation, preferring Segoe UI, then
  Arial, Tahoma, Verdana, and the first other usable system family.
- If SixLabors.Fonts exposes no usable system family (as observed under
  NixOS/Wine), the renderer uses embedded static Noto Sans Regular and Bold
  faces. The bundled fallback is licensed under the SIL Open Font License 1.1.
- Font discovery remains lazy: plugin startup and unrelated Library/capture
  behavior do not depend on system-font or bundled-font resolution.
- Image clipboard publication is best-effort. Platforms where Dalamud does not
  implement image clipboard copy still save, index, and finalize captures; the
  first unsupported result is reported once per plugin session.

## Installation

GlamSpector is distributed through a third-party Dalamud custom plugin
repository. Friends do not need to compile the plugin themselves.

1. Open Dalamud/XIVLauncher settings and go to **Experimental** → **Custom
   Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/Totyh/GlamSpector/main/repo.json`
   and save the settings.
3. Open `/xlplugins`, search for **GlamSpector**, and choose **Install**.

Future released versions are offered through Dalamud's normal plugin update
flow. This custom repository is maintained by the GlamSpector author and is not
an official Dalamud listing. Local development can continue to load the Debug
DLL through **Dev Plugin Locations**; that workflow is unchanged.

## M3.15.5 — Distribution and custom repository

- Adds a stable custom-repository manifest for installing GlamSpector from a
  version-pinned GitHub Release package.
- Release packaging now contains the plugin, required managed dependencies, and
  only the Windows x64 SQLite native runtime rather than unrelated platform
  runtimes.
- Adds locked Windows CI builds, release-metadata/package validation, and a
  tag-only workflow that publishes the packager-generated `latest.zip`.
- Keeps plugin runtime behavior, local Library/media privacy, and local Dev
  Plugin DLL development unchanged.

### Maintainer release sequence

1. Bump `Version` in `GlamSpector.csproj`, update `repo.json` to the same
   version and `vX.Y.Z/latest.zip` URLs, and update manifest/changelog/docs.
2. Run `scripts/Validate-Release.ps1`, locked restore, and a Release build;
   inspect `bin/Release/GlamSpector/latest.zip`, then validate it with
   `scripts/Validate-Release.ps1 -PackagePath <zip>`.
3. Commit and merge the reviewed release metadata to `main`.
4. Create and push tag `vX.Y.Z` on that exact main commit. The tag workflow
   validates the tag, rebuilds, and publishes the generated ZIP as
   `latest.zip`; it never rewrites `repo.json`.
5. Verify the release asset and the permanent raw `repo.json` subscription URL
   are reachable before asking users to update.

## M3.15.4 — Library rendering performance

- Virtualizes the left Library list with a fixed-height manual visible range.
  It uses ordinary ImGui scroll/cursor layout only; no native clipper object is
  involved. Only visible/overscan rows resolve row presentation and request
  thumbnails, while scrolling, selection, search/filter order, and the existing
  118×88 row appearance are preserved.
- Builds a lightweight in-memory presentation snapshot during Library
  `Refresh()`. Primary media, media availability, row date/rating text, and the
  ordered personal-preview/share-card/source-image lists are reused instead of
  being rediscovered from disk every frame.
- Keeps Dalamud's shared file texture provider as the image cache. No persistent
  thumbnail files are introduced. A visible image that disappears after refresh
  falls back safely and is rediscovered on the next refresh.
- Extends `/glamspector debug` / `/glamspector diag` with concise Library
  counters: total/search/filter counts, rows actually drawn, visible thumbnail
  requests, media resolutions per refresh, and a rolling 120-frame draw-time
  average/maximum.
- Leaves the five-second best-effort ownership-progress refresh and all
  ownership semantics unchanged.

### Suggested M3.15.4 test

1. With roughly 800 entries and no search/filter, open the Library at a stable
   location and compare FPS/frame time against the previous build. Leave it open
   for at least ten seconds, scroll from top to middle/bottom, and confirm the
   loss now tracks only the visible rows.
2. Run `/glamspector debug` while the Library is open. Confirm `matching` is near
   the full Library size while `rows` and `thumbnails` remain near the number
   visible in the left pane, not hundreds.
3. Search, apply each filter, and exercise every sort mode including **File
   size**. Select entries before and after scrolling; highlighting, right-side
   details, and persisted selection must remain correct. Confirm a selected
   entry stays visible on the right when search or a category filter hides its
   left-row match.
4. For entries with Inspect Preview, Full Card, personal previews, Share Cards,
   source images, and Adventurer Plate media, verify thumbnails and right-side
   media remain unchanged. Exercise **Set primary**, capture/delete personal
   preview, create/delete Share Card, attach Plate, and delete media; the left
   thumbnail must update after the existing refresh path.
5. Remove or rename one thumbnail file outside GlamSpector while the Library is
   open. Scroll its row into view and confirm the UI does not throw; press
   **Refresh** and confirm normal no-image fallback/media availability.

## M3.15.3 — Inspect watchdog and stale-worker retirement

- Keeps the existing 10-second CharacterInspect viewport-texture timeout and
  adds a separate 30-second deadline for the complete Inspect capture attempt,
  including readback, portrait/card preparation, PNG encoding, file writes,
  clipboard work and Library publication.
- Continues checking valid, ready, nonzero CharacterInspect identity after the
  texture arrives. If Inspect moves from character A to character B, A loses
  lifecycle ownership immediately; a closed/unready/zero Inspect alone remains
  tolerated after acquisition for transient duty churn.
- Separates lifecycle availability from worker/resource lifetime. An abandoned
  worker may finish privately, but cannot keep Capture busy or publish later UI,
  Plate, focus, notification, file or Library side effects. Its CTS and local
  resources remain alive until both the worker and texture provider settle.
- Passes the attempt-linked plugin/capture cancellation token through readback,
  Full Card and prepared-portrait encoding, staged file writes and clipboard
  work. Synchronous image operations are guarded before and after rather than
  pretending they are preemptible.
- Writes automatic Inspect media to generation-specific staging files and
  promotes them only after a final ownership check. SQLite work no longer runs
  under the capture-lifecycle lock and rechecks ownership immediately before
  transaction commit.
- Expands `/glamspector debug` with the active generation/entity, current Inspect
  entity, exact processing stage, whole-attempt and stage elapsed time, texture
  and worker state, mismatch/retirement state, and any still-unwinding retired
  worker. The 10-second value is shown only for `wait-texture`.

### Suggested M3.15.3 torture test

1. Capture normally with automatic Library indexing, clipboard and diagnostics
   enabled. Confirm the Inspect Preview, Full Card, Library row, notification and
   configured Plate workflow remain unchanged.
2. During viewport loading, close CharacterInspect or switch from A to B. Confirm
   A retires promptly, no A media/Library row appears later, and B can be captured.
3. Rapidly switch from A to B during readback, card rendering and PNG/file work.
   Confirm Capture becomes available immediately when valid B appears and no late
   A clipboard, success notification, Plate request or focus restoration occurs.
4. Repeat inside a duty while actors enter/leave ObjectTable. Closing/unready/zero
   Inspect after texture acquisition must not alone prove replacement, but a valid
   nonzero B must retire A.
5. Run `/glamspector debug` throughout. Confirm stages such as `wait-texture`,
   `encode-readback`, `prepare-preview`, `render-card`, `encode-card`,
   `encode-portrait`, `write-*`, `clipboard`, `library-db` and `finalize` report
   separate total/stage timing. A deliberately stalled attempt must return the UI
   to idle by 30 seconds and may appear only under `retiredWorker` while unwinding.
6. Reload the plugin during texture acquisition and during post-texture processing.
   Confirm the old instance produces no later files, Library/UI updates, clipboard
   changes, Plate actions, notifications or focus changes.

## M3.15.2 — Preview/import/update polish

- Automatic CharacterInspect capture now prepares its portrait once through
  `GlamCardRenderer`: the Full Card and saved Inspect Preview consume the exact
  same cleaned, native-frame-free image. The existing item-level cleanup setting
  therefore affects both outputs consistently without a second masking path.
- Keeps personal **Capture my preview** framing unchanged. Its normal path now
  resolves the native Preview component directly; `bottomRatio: 0.879` remains
  the compatibility fallback when that component node is unavailable.
- When item-level cleanup is disabled, both the Full Card portrait and automatic
  Inspect Preview retain the native CharacterInspect stamp.
- Makes Eorzea Collection import strictly manual: open the supplied URL in the
  normal browser, copy the page source, and paste the HTML into GlamSpector.
  Parsing is local; GlamSpector performs no EC page fetch, remote image download,
  browser-cookie access, crawling, scraping, or anti-bot bypass.
- Preserves existing EC Library rows, source URLs, recipes, local DisplayTitles,
  and previously cached source images. Manual re-import refreshes parsed source
  metadata without deleting or replacing legacy media when no new media exists.
- Prints `GlamSpector updated to version X` once when the running assembly version
  genuinely increases. The last-seen version is stored in configuration;
  same-version reloads and downgrades remain quiet. Existing pre-M3.15.2 configs
  receive the M3.15.2 announcement once, while a first-ever install establishes
  its version baseline silently.

### Suggested M3.15.2 test

1. Perform a normal Inspect capture with item-level cleanup enabled. Confirm the
   automatic **Inspect Preview** matches the clean, frame-free portrait inside
   the **Full Card**, and that the Full Card itself renders as before. Disable
   cleanup and confirm both outputs consistently retain the native stamp.
2. On a Library entry, use **Try on glam**, adjust the Fitting Room, and choose
   **Capture my preview**. Confirm its established thin-frame crop is unchanged.
3. Open **Import… → Eorzea Collection…**, enter a glamour URL, and confirm **Open
   in browser** opens it. With only the URL supplied, confirm no import/fetch
   occurs and the UI instructs you to paste page source.
4. Copy the full browser page source and import it. Confirm equipment, both dye
   channels, Facewear (when present), source title, creator, and original URL are
   retained. Rename its Library DisplayTitle, repeat the manual import, and
   confirm that local title survives.
5. Open an older EC entry with cached source images and confirm those images
   still display before and after manual re-import. No EC images should be
   downloaded automatically.
6. Upgrade from M3.15.1 and confirm one `GlamSpector updated to version 0.3.15.2`
   chat message. Reload automatically or manually without changing the version
   and confirm no second message appears. A fresh install should remain quiet.

## M3.15.1 — Capture lifecycle stability

- Prevents automatic Adventurer Plate capture from leaving GlamSpector permanently busy if the Plate closes, loses data, or changes character during its render-settle period. A hard overall deadline remains active through the configured settle delay.
- Binds Inspect capture preparation to the current inspected entity. Closing Inspect or changing characters before the viewport request completes cancels that attempt and requires a fresh capture.
- Gives the Inspect viewport texture request a 10-second cancellation deadline and guarantees capture/focus/pending-request cleanup on timeout or failure. A cancelled request is discarded even if the underlying texture operation completes late.
- Clears transient Facewear and Free Company observation caches when CharacterInspect disappears while preserving short-lived caching during one valid Inspect session.
- Extends `/glamspector debug` and `/glamspector diag` with a concise second line describing GlamSpector's capture phase, entity, preparation, timeout and automatic Plate state.

### Suggested M3.15.1 test

1. Enable automatic Plate capture and set the Plate settle delay to 3 seconds. Capture character A, then close the Plate after its portrait appears but before settling finishes. Confirm GlamSpector reports the cancelled Plate attempt and the Inspect capture button becomes usable again without reloading.
2. Repeat while opening another character's Plate or changing inspected targets during the settle period. Confirm the old attempt clears and never captures the replacement Plate.
3. Start an Inspect capture and immediately close Inspect or switch from character A to character B. Confirm the attempt fails cleanly, does not later save A or B unexpectedly, and a fresh capture of B succeeds.
4. Rapidly alternate two inspected characters with different gear/Facewear/Free Company data. Confirm saved recipes and metadata always match the entity captured after preparation.
5. Run `/glamspector debug` while idle, preparing, capturing and during automatic Plate loading/settling. Confirm the native diagnostic is followed by the matching GlamSpector lifecycle state and elapsed/deadline information.

## M3.15.0 — Library identity & memory

- Adds a user-editable **Library title** to every entry. Use the compact **Edit title** action beside the selected title, then Save or Cancel. Empty titles are rejected.
- Keeps the local Library title separate from captured/imported identity. Renaming does not change media paths, saved recipes, Glam Codes, package/share metadata, or duplicate detection.
- Shows Eorzea Collection source title, creator (when available), and source identity separately from the editable local title. Re-importing the same EC glamour refreshes its source metadata without overwriting a user-renamed Library title.
- Remembers the Library sort, rating/ownership/Wanted/Plate filters, filter-bar visibility, draggable left-column width, selected entry when it still exists, and the open/closed state of **Tags & notes**, **Files & sharing**, and **Library entry**.
- Keeps search text, import/edit dialogs, confirmations, and other transient state session-only. Invalid saved UI values fall back to safe defaults.
- Makes **Tags & notes** compact/collapsed by default while showing tag and note presence in its header.

### M3.15 migration behavior

- Existing SQLite libraries migrate in place by adding a nullable `display_title` column and backfilling it once. Existing non-EC entries retain their previous `Character @ World` visible label; EC entries use their saved source title without the synthetic `@ Eorzea Collection` suffix.
- Existing source title/creator/URL, ratings, tags, notes, Wanted state, recipes, and all media paths/files are preserved. No re-import or file move is required.

### Suggested M3.15 test

1. Open an existing capture and confirm its familiar `Character @ World` title remains. Choose **Edit title**, save a custom title, reload the plugin, and confirm it persists. Verify Cancel discards an edit and whitespace-only text cannot be saved.
2. Open an Eorzea Collection entry. Confirm its editable Library title does not include `@ Eorzea Collection`, while source title/creator attribution remains visible separately. Re-import the same page and confirm a custom Library title survives.
3. Rename a structured entry, then verify Try On, Glam Code copy, media/open-folder actions, Share Cards, ratings/tags/notes/Wanted state, and duplicate discovery still behave independently of the title.
4. Change sort and filters, resize the left column by dragging the divider, select an entry, and choose open/closed states for **Tags & notes**, **Files & sharing**, and **Library entry**. Reload the plugin and confirm those states return.
5. Enter Library search text and open a rename/delete/import confirmation, then reload. Confirm search and transient edit/confirmation state are not restored.

## M3.14.0 — Preview-first Library

- New managed captures **always keep the Inspect character preview** and use it as the Library image before the full Glam Card. The full card is retained as secondary/share media. The old **Save raw preview** setting now applies only when automatic Library indexing is disabled.
- Personal **Capture my preview** shots are now first-class Library visuals. Every fresh Fitting Room preview automatically becomes the entry's Primary image; older M3.12/M3.13 entries with personal previews are promoted preview-first automatically while preserving an existing user-selected Primary preview.
- Replaces the one-at-a-time personal-preview viewer with a **gallery of up to three previews per row** (wrapping to additional rows). Each tile can be set Primary, opened, deleted, or used to create a share card.
- Adds **Create share card** for personal previews. GlamSpector combines that preview with the entry's saved gear, dyes and Facewear using the existing card renderer, then stores the result as separate **Share Cards** media. Generated cards can be copied to the clipboard for Discord, opened, located on disk, or deleted independently.
- Adds an **Inspect Preview** media tab and clearer separation between **Primary**, **My Previews**, **Share Cards**, **Full Card**, source images, and the actual FFXIV **Adventurer Plate**.
- Generated share cards are tracked in SQLite, counted in per-entry media size, cleaned up with full-entry deletion/duplicate cleanup, and remain independent if their source personal preview is later deleted.
- Keeps the M3.13 Fitting Room crop (`bottomRatio: 0.879`) that restored the thin bottom frame without bringing back the circular action strip.

### Suggested M3.14 test

1. Capture a brand-new inspected glamour with automatic Library indexing enabled. Its Library thumbnail/Primary image should be the **Inspect Preview**, while **Full Card** remains available as extra media.
2. Press **Try on glam**, compose three different Fitting Room shots and press **Capture my preview** after each. The newest shot should become Primary automatically.
3. Open **My Previews** and confirm all three appear side by side at a wide Library window; a fourth should wrap onto the next row. Test **Set primary**, **Open PNG**, **Folder**, and **Delete…** independently.
4. On one personal preview press **Create share card**. GlamSpector should switch to **Share Cards** and show a generated card containing the preview plus the saved item/dye recipe. Test **Copy** by pasting into Discord or an image-capable app, then test Open/Folder/Delete.
5. Delete the source personal preview after generating its share card. The generated card should remain available.
6. Sort by **File size** and confirm generated share-card files contribute to the entry total. Full **Delete entry & files…** should remove previews and generated share cards too.

## M3.13.0 — Library UI cleanup

- Reworks the Library toolbar so the high-frequency controls are easier to scan: **Search**, **Refresh**, **Import…**, **Wanted**, **Filters**, **Sort**, **Library tools…**, and Settings. The four import paths now live under one **Import…** popup; duplicate cleanup moved under **Library tools…**.
- Reorganizes a selected glam into clearer sections. **Try on glam** and **Capture my preview** are the primary actions, while **Copy Glam Code** and the Wanted helper are secondary actions beneath them.
- Gives the media viewer its own heading and keeps personal-preview controls with the preview itself: select preview, set primary, open PNG/folder, or delete only that preview.
- Moves original-card/file/export/Plate/source-link actions into a quieter collapsed **Files & sharing** section. **Open entry folder** remains available for image-less Glam Code/EC recipe entries.
- Moves **Remove from library** and **Delete entry & files…** to a separate collapsed **Library entry** section at the very bottom, with clearer confirmations describing whether disk files are kept or permanently deleted.
- Nudges the Fitting Room personal-preview crop bottom edge from `0.872` to `0.879`, restoring the few missing pixels of the native preview frame without bringing back the bottom action-button strip.

### Suggested M3.13 test

1. Open the Library and verify **Import…** contains Existing captures, `.glamspector.zip`, Glam Code, and Eorzea Collection; **Library tools…** should contain duplicate cleanup.
2. Select a structured glam and verify **Try on glam** / **Capture my preview** are the obvious first actions.
3. Open **My Previews**, test **Open PNG**, **Open folder**, **Set as primary**, and **Delete preview…**.
4. Expand **Files & sharing** and verify original-card/file/export/Plate actions still work.
5. Scroll to the bottom, expand **Library entry**, and confirm the two destructive choices are clearly separated from preview deletion.
6. Capture a fresh Fitting Room preview and confirm the full thin bottom frame is present while the circular action buttons remain excluded.

## M3.12.1 — Preview UI polish

- Renames the whole-entry destructive action from **Delete capture…** to **Delete entry & files…**, so it cannot be confused with **Delete preview…** in the personal-preview area.
- Fixes the personal-preview **Open PNG** and **Open folder** buttons by giving them unique ImGui IDs; previously they collided with the same-labelled entry-level buttons in the same window.
- Tightens the bottom edge of Fitting Room personal-preview capture so the native bottom action-button strip is excluded while the character preview frame remains.

## M3.12.0 — Personal Fitting Room previews + media folders

- Every structured Library entry can now save **personal previews** from FFXIV's native Fitting Room, including normal captures, Glam Code imports, and Eorzea Collection imports.
- Workflow is intentionally manual: **Try on glam**, rotate/zoom the native Fitting Room until the shot looks right, then press **Capture my preview**. GlamSpector captures the current view without re-running Try On.
- Multiple personal previews can be kept per entry. The **My Previews** gallery can set one as the primary Library thumbnail, open its PNG/folder, or delete only that individual preview. Entries with an original card/source image can switch the primary image back to the original.
- For image-less Glam Code / Eorzea Collection entries, the first personal preview becomes the primary thumbnail automatically.
- Added **File size** Library sort. It sorts by the total size of files associated with each entry (card/recipe, raw preview, diagnostic JSON, Plate, source images, and personal previews), with duplicate file paths counted once.
- Newly auto-indexed native captures are stored together under `OutputDirectory/LibraryMedia/Captures/<entry>/` (`glam-card.png`, optional `raw-preview.png`, `diagnostic.json`, `adventurer-plate.png`, and `previews/`). New Glam Codes/imported packages likewise use managed per-entry folders.
- Existing flat captures and their old paths remain supported; M3.12 does **not** force a bulk migration or move old files. Personal previews added to a legacy entry are stored safely under `LibraryMedia/Legacy/Entry-.../`.
- Deleting a full capture now also removes its tracked personal/source media. **Remove from library only** continues to leave files on disk.

### Suggested M3.12 test

1. Select an imported Glam Code or EC entry, press **Try on glam**, adjust the Fitting Room camera, then press **Capture my preview**. The first image-less-entry preview should become its thumbnail.
2. Capture two or three different angles, open **My Previews**, set a different one primary, delete one, and verify the others remain.
3. On a normal captured entry, set a personal preview primary and then use **Use original as primary** to return to the Glam Card.
4. Reload the plugin / relog and verify the previews still exist.
5. Sort by **File size** and confirm the largest media-heavy entries appear first.


## M3.11.1 - Eorzea Collection browser fallback (historical)

This milestone introduced pasted-page-source import after EC returned HTTP 403
to plugin HTTP clients. M3.15.2 supersedes its automatic request and image-
download behavior: pasted HTML is now the only import input and is parsed locally.

## M3.11.0 — Eorzea Collection import (historical)

The network behavior below describes that historical release and is superseded
by M3.15.2's strict manual-only policy.

- Library toolbar: **Import EC**. Paste one URL like `https://ffxiv.eorzeacollection.com/glamour/350011/petals-and-lace`.
- GlamSpector fetches only that single page (no catalogue crawl/bulk scraping), parses the visible equipment/dyes/Facewear when present, resolves names against local FFXIV sheets, and saves the result as a normal Library entry.
- Up to 8 large source pictures are cached locally under the plugin config directory in `EorzeaCollection/<glamour id>/`. The Library shows a **Source Images** viewer and retains creator/source attribution plus the original URL.
- Imported EC entries work with Try On, item actions, ownership/Wanted, ratings, tags/notes, filters, and **Copy Glam Code**.
- If the website returns HTTP 403 to the plugin request, GlamSpector stops and reports it; it does not attempt to bypass anti-bot protection.
- Fixes the M3.10.0 compile error in `GlamCodeService` where both dye lookups used the same `out var stain` local name.

## M3.7.7 — interactive Library items

- Library item names now expose a right-click context menu.
- **Try On** loads just that visible glamour item with its captured dye(s) into FFXIV's native Fitting Room.
- **Link in chat** uses FFXIV's native item-link path to insert the item into the chat input; GlamSpector never sends the message automatically.
- **Copy item name** is included as a small convenience.
- Native Try On/chat operations are queued onto `Framework.Update`, keeping them out of the ImGui draw callback.

# GlamSpector M3.7.1 — Ratings + ownership polish

M3.7 expands the Library's **Owned** hints while keeping the important rule that a negative result is not presented as proof that an item is missing.

## Ownership sources

For the current character GlamSpector now checks:

- Inventory and equipped gear;
- Armoury Chest;
- Chocobo / Premium Saddlebags;
- currently loaded retainer containers;
- **FFXIV's own cached Glamour Dresser item list** via `ItemFinderModule`;
- **Armoire**, when the server-side Cabinet data is loaded;
- **Facewear unlock state** for the Facewear stored with a capture.

The game's ItemFinder module is the same subsystem behind `/isearch`; it retains a cached Glamour Dresser list and saddlebag data when available. The Armoire itself reports whether its data has been loaded before `IsItemInCabinet` can be used.

A missing item displays `?`, not `No`, because an unopened/unloaded retainer can still contain it. Hovering the status explains which storage sources are unavailable. A coverage line under the table shows whether Dresser/Armoire/Saddlebag caches are currently usable, and **Refresh ownership** forces an immediate rescan.

## Testing suggestions

1. Pick an item you know is in normal Inventory/Armoury; it should show its location.
2. Pick an item known to be in the Glamour Dresser. If Dresser says `not cached`, open the dresser once, then return to the Library / press **Refresh ownership**.
3. At an inn, open the Armoire once and confirm a stored item shows `✓ Armoire` while the coverage line says `Armoire ✓`.
4. Check a captured Facewear entry that your current character has unlocked; it should show `✓ Unlocked` beside Facewear.

Unloaded retainers are still the main missing piece. A later pass can either consume FFXIV's retainer item-search cache directly or add optional Allagan Tools IPC for persistent/account-wide ownership.


## M3.7.1 additions

- Local 1–5 star ratings for Library captures. Ratings are personal Library metadata and are not added to shared GlamSpector export packages. Click the active star again to clear a rating.
- `Rating` sort option (highest-rated first, newest first within the same rating).
- Ownership rescans remain local-only: the button does **not** issue `/isearch` and does not request server data. Manual refresh has a short two-second UI cooldown, while the automatic local cache is reused for ten seconds to avoid needless repeated scans.
- A future purge/cleanup action can safely build on the rating column; M3.7.1 intentionally does not auto-delete low-rated captures.


## M3.7.4 Outfit ownership diagnostic

FFXIV stores an Outfit Glamour as one Glamour Dresser slot plus a parallel unlock-bit field, so the ordinary cached item-ID list does not enumerate every constituent piece. Run `/glamspector ownership-debug` after opening the Glamour Dresser once. GlamSpector writes `GlamSpector-ownership-debug.txt` in its Dalamud config directory. This command only reads local client memory/game data; it does not run `/isearch` or query the server.


## M3.7.4 diagnostic

`/glamspector ownership-debug` now also inspects the live Glamour Dresser agent while the dresser is open. It compares the expanded `PrismBoxItems` list against the Scion Traveler set and reports `NumOutfitPiecesAdded`. This is local client-memory diagnostics only; it does not run `/isearch` or query the server.


## M3.7.4
- Reads the live Glamour Dresser PrismBox expanded item list when the Dresser is open.
- Caches that expanded list for the current character for the rest of the session.
- Pieces stored inside Outfit Glamours can now show `✓ Glamour Dresser (Outfit)`.
- The cache is cleared when the logged-in character changes.
- No `/isearch` command or server request is triggered by ownership refresh.


## M3.7.7

Expanded Glamour Dresser/Outfit ownership is persisted per character in `glamspector-ownership-cache.json`. Open the Dresser once and refresh ownership to seed/update the snapshot; future plugin reloads and game restarts restore it automatically. Re-open the Dresser after adding/removing Outfit items to refresh the saved snapshot.


## M3.7.7

- Added a small gear button in the Glamour Library toolbar that opens the existing GlamSpector Settings window.

## M3.8.0 — Wanted items + ownership progress

- Right-click any Library item and choose **Mark as wanted** / **Remove from wanted**.
- Wanted status is personal Library metadata stored in SQLite and is never included in shared `.glamspector.zip` exports.
- A **Wanted** window lists all wanted item appearances, current best-effort ownership status, and how many saved glams use each item.
- The Wanted window can remove individual entries or clear items that GlamSpector can currently verify as owned.
- Saved glams show a **verified ownership progress** count. Because unloaded retainers and other unavailable storage can still contain an item, unverified pieces are not labelled definitively missing.
- The gear table shows a compact Wanted marker and provides a one-click **Mark unverified pieces wanted** helper for the selected glam.


## M3.9.0 — Library filters, tags and notes

- Added a collapsible **Filters** bar to the Glamour Library.
- Filter by rating (Unrated or 1★+ through 5★), verified ownership completion, whether the glam contains Wanted items, and whether an Adventurer Plate is attached.
- Added private per-glam **tags** (comma-separated, up to 30) and a private **note**.
- Tags and notes are included in the live Library search-as-you-type.
- Tags, notes, ratings and Wanted state remain personal Library metadata and are **not** included in shared `.glamspector.zip` exports.
- Existing M3.x SQLite libraries migrate in place; no re-import is required.

Suggested test: tag a glam `gothic, healer`, add a short note, search for a word that exists only in the tag/note, then try the Rating / Ownership / Wanted / Plate filters.

## Glam Codes (M3.10)

GlamSpector can share the visible outfit as a compact text string without sending a PNG/ZIP.

- Select a structured Library glam and click **Copy Glam Code**.
- Send the resulting `GS1:...` string through Discord/chat/etc.
- The recipient opens **Import code**, pastes the string and chooses **Import to Library**.
- A text-only Library entry is created with the visible gear, both dye channels and Facewear. It can be tried on, searched, ownership-checked, rated/tagged and added to Wanted just like a normal capture.
- Glam Codes deliberately exclude screenshots, Adventurer Plates, character/world/FC identity, ratings, tags, notes and Wanted state. Those remain local/private.
- The code includes a checksum so truncated or mistyped strings are rejected instead of silently importing the wrong outfit.

The existing `.glamspector.zip` export remains the richer sharing format when you also want the Glam Card/Plate images and captured source metadata.
