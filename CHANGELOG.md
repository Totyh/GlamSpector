# Changelog

User-facing changes are listed newest first. Detailed implementation decisions
and development context remain in [PROJECT_HISTORY.md](PROJECT_HISTORY.md).

## 0.3.16.0

- Added optional, local-only Allagan Tools IPC supplementation for positive
  ownership evidence from the active character's cached personal storage. It is
  an explicit, disabled-by-default opt-in under the new Integrations settings tab.
- Improved ownership tooltips and diagnostics while preserving `?` for every
  zero, unavailable, or otherwise unverified result. Wanted flags remain under
  user control.
- Reorganized public documentation so the README focuses on installation and
  everyday use, with release history moved here.

## 0.3.15.6

- Fixed startup and Share Card rendering on systems where SixLabors.Fonts cannot
  resolve Windows system fonts by adding lazy selection and a bundled
  OFL-licensed Noto Sans fallback.
- Made image clipboard publication best-effort, so unsupported platform/Dalamud
  clipboard implementations no longer fail an otherwise saved capture.

## 0.3.15.5

- Added the version-pinned custom Dalamud repository, validated release package,
  and CI/tagged-release workflows used for normal installation and updates.

## 0.3.15.4

- Removed the major Library-open FPS cost for large collections with safe manual
  row virtualization and a refresh-scoped media presentation cache.

## 0.3.15.3

- Added a whole-attempt Inspect capture watchdog, all-phase target monitoring,
  generation-safe stale-worker retirement, staged file publication and clearer
  lifecycle diagnostics.

## 0.3.15.2

- Unified automatic Inspect Preview preparation with the clean Full Card
  portrait path.
- Made Eorzea Collection import strictly manual from user-pasted HTML, with no
  GlamSpector page or image downloads.
- Added one-time chat notification for genuine version upgrades.

## 0.3.15.1

- Hardened Inspect and automatic Adventurer Plate capture against target changes,
  closures, timeouts, late callbacks and plugin reloads.

## 0.3.15.0

- Added editable local Library titles while preserving source identity and
  attribution.
- Remembered useful Library sorting, filters, split width, section expansion and
  valid selection state; compacted Tags & notes.

## 0.3.14.0

- Made the Library preview-first, introduced the wrapped My Previews gallery and
  kept Full Cards/source media as secondary references.

## 0.3.13.0

- Reorganized accumulated Library actions into a clearer hierarchy with grouped
  import, maintenance, sharing and destructive actions.

## 0.3.12.0

- Added personal Fitting Room previews, per-entry managed media folders and Share
  Card generation from saved recipes.

## 0.3.11.0

- Added Eorzea Collection single-glamour import. The current supported workflow
  is the later manual-only pasted-HTML path described above.

## 0.3.10.0

- Added compact Glam Codes for sharing appearance recipes without private
  Library metadata or screenshots.

## 0.3.9.0

- Added Library filters, private tags and notes.

## 0.3.8.0

- Added private Wanted tracking, a Wanted window and verified ownership progress.

## 0.3.7.0

- Added best-effort ownership evidence, ratings and interactive item actions.
