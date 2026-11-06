# Tagium backlog

Living checklist for follow-up work. `[x]` = shipped, `[ ]` = open, `[~]` = in progress.
Each item names the evidence that motivated it so it can be re-verified later.

---

## 1. UI leftovers & theme guardrails — shipped

- [x] **`--font-size-lg` was undefined.** `Files/StatsModal.razor.css` asked for it, so `font-size` silently inherited. Now declared in `app.css` and documented in `design.md`.
- [x] **`--breakpoint-sm` was a dead token.** Custom properties are not valid inside `@media`, so the token could never be used. Removed; the real breakpoints (shell 900px, tag manager 760px) are documented instead.
- [x] **`TagIcon` colour-dot ring was invisible on light themes.** `rgba(255, 255, 255, 0.1)` disappeared on Classic Light, so white tag dots had no edge. Now a neutral `--text-muted` ring drawn inside the dot.
- [x] **Contrast math extracted.** `Tagium.Core/Theming/ColorContrast.cs` (WCAG luminance + ratio + readable-ink picker); `TagColorContrast` is now a thin tag-specific wrapper over it.
- [x] **Theme guardrail tests** (`Tagium.Core.Tests/Theming/ShippedThemeTests.cs`):
  - `app.css :root` defines every contract token;
  - each shipped theme defines *exactly* the contract token set (catches missing **and** typo'd names);
  - text pairs clear WCAG AA per theme and for the `:root` fallback palette.
  These are the regressions that slipped through before: the missing `--surface-hover` and Monokai's 3.79:1 muted text.

---

## 2. Keyboard & screen-reader pass — shipped (first slice)

Evidence: file cards were `<div @onclick>`, list rows `<tr @onclick>`, sidebar tag items `<div @onclick>` — no `tabindex`, no `role`, no `aria-selected`, and the app had no `:focus-visible` rule at all.

- [x] `:focus-visible` ring in `app.css` (`2px var(--primary-color)`, offset 2px) + documented in `design.md` §5.
- [x] `FileGrid`: `role="listbox"`/`option`, `aria-selected`, `aria-label`, roving tabindex, Enter/Space/Escape/arrows.
- [x] `FileListView`: table `role="grid"`, rows `role="row"` + `aria-selected`, cells `role="gridcell"`, same key contract.
- [x] `TagSidebar` + `FolderSidebar`: filters are `role="button"`, focusable, `aria-current`, Enter/Space activate.
- [x] `wwwroot/js/libraryNav.js`: geometry-based arrow/Home/End focus movement shared by both views (works for auto-fill grid and single-column list).
- [x] `grid.js` no longer owns arrows/Enter — that moved into Blazor, so keys are not handled twice. Marquee colours now come from the theme instead of hardcoded blue.
- [x] `ModalHost`: `role="dialog"` + `aria-modal` + `aria-labelledby`, focus moves into the dialog on open, Escape closes, close button labelled; the component now actually implements `IDisposable` so its event handlers unsubscribe.
- [x] `aria-label` added to the remaining icon-only buttons (toolbar view/view-options/settings, files-view toolbar, folder expand, pagination prev/next, rating stars, modal close) plus `aria-expanded`/`aria-current` on toggles.

Still open in this area:

- [x] Focus **trap** inside `ModalHost` and restore focus to the opener on close — `wwwroot/js/modalFocus.js` captures the opener before the dialog re-renders, traps `Tab`/`Shift+Tab` at the first/last focusable element (capture-phase listener, so Blazor keeps owning Escape), and restores focus after the dialog leaves the DOM. `ModalHost` is now `IAsyncDisposable` so the listener cannot outlive the host.
- [ ] Verify the tab order end-to-end in the running app — CI can only prove it compiles.
- [ ] Screen-reader pass with a real AT (Narrator/NVDA) over the grid, list and tag sidebar.
- [x] Announce selection changes and filtered counts with a polite live region — `AppShell/LibraryAnnouncer` keeps a single mounted `role="status"` region, debounced 400 ms so arrow-key repeats collapse into one sentence, silent when only paging or a re-render changed. Wording lives in `Tagium.Core/Files/LibraryAnnouncement` with tests in `Tagium.Core.Tests/Files/LibraryAnnouncementTests.cs`.
- [ ] Folder tree could use `role="tree"`/`treeitem` with `aria-expanded`/`aria-level` instead of flat buttons.

## 3. Multi-select model — shipped

Evidence: `AppState.SelectFile(id, multiSelect, rangeSelect)` accepted `rangeSelect` and ignored it. Both views were already passing `e.ShiftKey` into it, so Shift+click silently behaved like a plain click and the gesture simply did nothing.

- [x] `Tagium.Core/Files/SelectionRange.Between` — inclusive span between anchor and target in display order, direction-agnostic, empty when either endpoint is not in the list. Tested in `Tagium.Core.Tests/Files/SelectionRangeTests.cs`.
- [x] `AppState` keeps a `_selectionAnchor`: plain click, Ctrl+click and plain arrow movement move it; Shift+click and Shift+arrow extend from it and leave it where it is, so repeated shift gestures grow from the original row.
- [x] The span is scoped to the current page in display order — exactly what the grid and list render — so a stale anchor cannot select rows the user cannot see. An anchor that left the page falls back to a plain click.
- [x] The anchor is dropped by search/folder/missing-only changes, `SetSelection` (Ctrl+A) and `ClearSelection`, so a range never starts from a filtered-away row.
- [x] Keyboard parity: `Shift`+arrows / `Home` / `End` reach the same code path as Shift+click.

Still open:

- [ ] Ctrl+Shift+click unions with the existing selection but cannot subtract it — there is no "deselect this range" gesture.
- [ ] Marquee drag-select and Ctrl+A bypass the anchor; both land on `SetSelection`, which clears it.
- [ ] No automated coverage of the anchor itself (it lives in `AppState`, in the UI project, which has no test project). Only the range maths is covered.

---

## 4. Theme authoring & validation

Evidence: themes can only be added by dropping JSON into AppData — Settings mentions the folder but offers no "open folder" or "reload", and `ThemeService` reports parse failures through `Console.WriteLine` only. A partial theme renders with invisible default fallbacks and no explanation.

- [ ] Settings → **Open themes folder** + **Reload themes** buttons.
- [ ] Surface `ThemeService` load errors in the UI instead of the console.
- [ ] Validate on load: malformed values, unknown token names, missing contract tokens (reuse the `RequiredTokens` list from `ShippedThemeTests`).
- [ ] Contrast badge per theme in the theme list, computed with `ColorContrast` (warn where pairs fall below AA).

---

## 5. Polish backlog

- [ ] **Tag chips pass colours as inline strings.** `TagSidebar` builds `"background-color: …; border-color: …; color: …"` in C#; prefer a `--tag-color` custom property set inline and let CSS read it.
- [ ] **Media letterbox backgrounds are hardcoded:** `#000` (`FileGrid`, `FileListView`), `#101010` (`SingleFileInspector`). Fine for dark themes, blunt for Classic Light — introduce `--media-bg` if that should soften.
- [ ] **`TagManagerView` `.color-circle`** uses `2px solid var(--border-color)`, which is nearly invisible on Classic Light; give it the same treatment as the `TagIcon` dot.
- [ ] **Monokai `--danger-hover`** is 4.11:1 against `--text-on-danger`; the guardrail test allows 4.0 for hover states, but lightening the hover would clear 4.5 outright.
- [ ] Consider deriving the `--primary-tint` / `--surface-hover` alphas per theme from a shared formula instead of hand-tuned rgba values.

---

## Constraints worth remembering

- This sandbox has **no .NET SDK**, so nothing can be compiled locally — CI (`build-and-test` on PRs to `main`) is the only compile gate. Always open a PR rather than pushing straight to `main`.
- `gh pr edit` fails in this repo (`Projects (classic)` GraphQL deprecation). Patch PR bodies with `gh api --method PATCH repos/Vraxo/Tagium/pulls/<n> -f body="$(cat body.md)"`.
- Existing installs keep their theme files: shipped themes refresh only when their `"Version"` rises (`ThemeService.SyncDefaultThemesFromShippingDir`). Bump the version in a shipped theme JSON whenever its palette changes.
