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

## 2. Keyboard & screen-reader pass — next

Evidence: file cards are `<div @onclick>`, list rows are `<tr @onclick>`, sidebar tag items are `<div @onclick>` — no `tabindex`, no `role`, no `aria-selected`. There is not a single `:focus-visible` rule in the app, and the only two `@onkeydown` handlers are on text inputs. Selection and navigation are mouse-only today.

- [ ] Add a focus-ring rule (and `design.md` entry): `:focus-visible` outline using `--primary-color` with offset.
- [ ] `FileGrid` cards: `role`/`tabindex`/`aria-selected`, Enter/Space selects, Enter opens.
- [ ] `FileListView` rows: same contract as the grid.
- [ ] `TagSidebar` tag items + system items: focusable, `aria-current` on the active filter.
- [ ] Roving-tabindex arrow navigation (grid: 2D, list: linear) plus Home/End.
- [ ] Audit icon-only buttons (`IconButton`, toolbar, modal close) for `aria-label`.
- [ ] `ModalHost`: focus trap, initial focus, restore focus to the opener on close.
- [ ] Verify tab order end-to-end once the above lands.

---

## 3. Theme authoring & validation

Evidence: themes can only be added by dropping JSON into AppData — Settings mentions the folder but offers no "open folder" or "reload", and `ThemeService` reports parse failures through `Console.WriteLine` only. A partial theme renders with invisible default fallbacks and no explanation.

- [ ] Settings → **Open themes folder** + **Reload themes** buttons.
- [ ] Surface `ThemeService` load errors in the UI instead of the console.
- [ ] Validate on load: malformed values, unknown token names, missing contract tokens (reuse the `RequiredTokens` list from `ShippedThemeTests`).
- [ ] Contrast badge per theme in the theme list, computed with `ColorContrast` (warn where pairs fall below AA).

---

## 4. Polish backlog

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
