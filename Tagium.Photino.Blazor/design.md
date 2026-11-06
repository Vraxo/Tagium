# Tagium UI/UX Design System Guidelines

This document serves as the absolute contract for all visual developments in Tagium. All styles must strictly adhere to the tokens defined here.

---

## 1. Absolute Guardrails & Anti-Patterns

* **NO Raw Hex Codes**: Never write raw colors (e.g., `#1a1a1a` or `#333`) in any component or stylesheet. Always map colors to semantic CSS custom properties.
* **NO Inline Styles**: Do not use `style="..."` attribute assignments for padding, margins, colors, or sizes. Keep CSS isolated inside companion `.razor.css` files.
* **Grid Alignment**: Layouts, padding, margins, and component gaps must strictly use multipliers of the 8px grid (using spacing custom properties).
* **Minimalist Feedback**: Keep hover states subtle and rounded. Ensure borders are thin (`1px`) and highly contrasting.

---

## 2. Global Semantic Tokens

All CSS variables are declared globally in `app.css`. Use them according to these functional definitions:

### Colors

| CSS Variable | Semantic Purpose / Value Mapping |
| :--- | :--- |
| `var(--bg-color)` | Core app background (dark charcoal) |
| `var(--panel-bg)` | Sidebar and secondary panel background |
| `var(--surface-bg)` | Cards, input fields, and elevated elements |
| `var(--surface-hover)` | Interactive hover state (translucent white on dark themes, black on light themes) |
| `var(--border-color)` | Structural separation lines, thin borders |
| `var(--text-color)` | Primary readable content |
| `var(--text-muted)` | Supporting descriptors, labels, and timestamps |
| `var(--primary-color)` | Focus rings, primary buttons, accents |
| `var(--primary-hover)` | Active primary button hover states |
| `var(--primary-tint)` | Translucent primary wash for selection rings and selected rows |
| `var(--text-on-primary)` | Contrasting text color sitting on primary-colored backgrounds (WCAG AA) |
| `var(--danger-color)` | Destructive buttons, warnings, missing alerts |
| `var(--danger-hover)` | Active destructive hover states |
| `var(--text-on-danger)` | Contrasting text color sitting on solid danger buttons |
| `var(--success-color)` | Completed operations, thumbnail progress, success toasts |
| `var(--folder-color)` | Directories, roots, and folder paths (`#ffca28`) |
| `var(--warning-color)` | High-visibility alert orange for missing file flags (`#ff9800`) |
| `var(--appearance-color)` | Secondary accent for background task progress, e.g. the tag-sync bar (`#ba68c8`) |

### Spacing & Grid System

| CSS Variable | Value | Purpose |
| :--- | :--- | :--- |
| `var(--space-2xs)` | 4px | Micro-paddings, element borders |
| `var(--space-xs)` | 8px | Grid gap, label-to-input gap, list item vertical padding |
| `var(--space-sm)` | 12px | Standard component padding, sub-panels |
| `var(--space-md)` | 16px | Workspace borders, grid container margins |
| `var(--space-lg)` | 24px | Layout spacing, empty state paddings |

### Typography

| CSS Variable | Value | Purpose |
| :--- | :--- | :--- |
| `var(--font-size-xs)` | 11px | Metadata, badges, counters, field hints |
| `var(--font-size-sm)` | 12px | Default control and body text |
| `var(--font-size-lg)` | 16px | Stat values and single prominent numbers |

Breakpoints are literal `@media` values and cannot be tokens (custom properties are not
valid inside media queries): the shell collapses at **900px** and the tag manager at **760px**.

### Border Radii & Transitions

The `Corner Style` preference selects the shared radius tokens used throughout the interface. The default values preserve the original design; circles and intentionally pill-shaped controls keep their distinctive geometry.

| Corner Style | `--radius-sm` | `--radius-md` | `--radius-lg` |
| :--- | :---: | :---: | :---: |
| Sharp | 0px | 0px | 0px |
| Subtle | 2px | 4px | 6px |
| Default | 4px | 6px | 8px |
| Rounded | 8px | 12px | 16px |

| CSS Variable | Value | Purpose / Standard Application |
| :--- | :--- | :--- |
| `var(--radius-sm)` | Set by Corner Style | Small buttons, badges, compact controls |
| `var(--radius-md)` | Set by Corner Style | Input fields, file cards, small containers |
| `var(--radius-lg)` | Set by Corner Style | Modals, preview windows, dialog frames |
| `var(--transition-fast)` | 0.1s ease-in-out | Hovers, background transitions, selection changes |
| `var(--transition-normal)` | 0.15s ease-in-out | Drawers, modal overlays, progress bars |

---

## 3. Interactive Component State Rules

Configure UI elements and icons dynamically using these targeted states:

* **Default Icons**: Render inactive system controls using `var(--text-muted)`.
* **System Folders**: Always render folder icons with `color: var(--folder-color)`.
* **Alarms & Warnings**: Render critical states and missing files with `color: var(--warning-color)`.
* **Selection Override**: When a container gains the active selection state, all inner text and colored icons must immediately transition to `color: var(--text-on-primary) !important`.
* **Interactive Deletions**: Tag-removal/delete controls must rest at `var(--text-muted)` and transition to `var(--danger-color)` on hover.
* **Tag-Coloured Fills**: Any surface painted with a tag's own `ColorHex` (sidebar pills, inspector pills, swatches with labels) must derive its label ink from `TagColorContrast.GetTextColor(...)` instead of a fixed white, because tag colours range from near-white to near-black. Controls nested inside those pills inherit that ink and give feedback via opacity plus `var(--surface-hover)`, never via a fixed accent colour.
* **Keyboard Focus**: Every interactive element must show a `:focus-visible` ring — `outline: 2px solid var(--primary-color)` with `outline-offset: 2px`, declared once in `app.css`. Never style `:focus` without `:focus-visible`, and never leave `outline: none` behind without a replacement. Text inputs and selects keep their border-colour focus treatment instead.

### Settings Panel Tabs

Settings navigation is a single unified surface: tabs carry no per-category accent colours.
* **Inactive Tab**: Label and icon `var(--text-muted)`.
* **Hover**: Label and icon `var(--text-color)` on `var(--surface-hover)`.
* **Active Tab**: Label and icon `var(--primary-color)` on `var(--surface-bg)`.
* **Icons**: Never assign a domain colour (folder/appearance/maintenance) to a tab icon; they inherit the label colour.

---

## 4. Component Sizing Specs (Compact Desktop)

Ensure all inputs, selectors, and controls conform to this compact desktop horizontal and vertical layout formula:

### Standard Utility Buttons, Dropdowns & Inputs
* **Height**: Lock strictly between **24px and 26px**.
* **Padding**: Vertical: `var(--space-2xs)` (4px); Horizontal: `var(--space-xs)` (8px) or `var(--space-sm)` (12px).
* **Line Height**: Lock to `line-height: 1.2` or `1.3` to prevent vertical skewing.
* **Dropdowns**: Select fields must match adjacent button paddings and line heights exactly to ensure perfect horizontal alignment.

### Hardware-Style Toggle Switches
* **Outer Profile**: Width `30px`, Height `16px`.
* **Visual Thumb**: Perfect circle, diameter `10px`.
* **Internal Clearance**: Centered with exactly `2px` clearance on all sides (`left: 2px; bottom: 2px`).
* **Active Position**: Transform offset transition is locked to `translateX(14px)`.
* **Thumb Color**: Always use `background: var(--text-on-primary)` for high contrast when activated.

---

## 5. Keyboard & Screen Reader Contract

Every interactive surface must be operable without a mouse. Two layers cooperate:

* **Blazor owns semantics and actions** — roles, `aria-*`, and handling Enter (activate), Space (toggle selection), Escape (clear/close) on the focused element.
* **`wwwroot/js/libraryNav.js` owns focus movement** — geometry-based arrow/Home/End movement between `data-id` items, so the same helper works for the auto-fill grid and the single-column list. It also suppresses the browser default for Space/arrows/Home/End on library items so the container does not scroll away.
* **`wwwroot/js/modalFocus.js` owns modal focus** — it records the opener, traps `Tab`/`Shift+Tab` inside the open dialog and gives focus back to the opener on close.

| Surface | Markup contract |
| :--- | :--- |
| File grid (`FileGrid`) | container `role="listbox"` + `aria-multiselectable`; cards `role="option"` with `aria-selected` and an `aria-label` |
| File list (`FileListView`) | table `role="grid"`, rows `role="row"` with `aria-selected`, cells `role="gridcell"` |
| Sidebar filters (`TagSidebar`, `FolderSidebar`) | `role="button"` + `tabindex="0"` + `aria-current` on the active filter |
| Modals (`ModalHost`) | `role="dialog"` + `aria-modal` + `aria-labelledby`, `tabindex="-1"`; focus moves into the dialog on open, Escape closes, `Tab`/`Shift+Tab` cycle inside the dialog, and focus returns to the opener on close |

**Roving tabindex**: library surfaces expose exactly one tab stop (the first selected item, otherwise the first item on the page); every other item is `tabindex="-1"` and reached with the arrow keys. Never make every row a tab stop.

**Selection gestures**: a plain click (or a plain arrow) replaces the selection and sets the anchor; `Ctrl`+click toggles and re-anchors; `Shift`+click and `Shift`+arrow extend an inclusive range from the anchor across the current page in display order and leave the anchor in place, so repeated shifts grow the same range. The span maths lives in `Tagium.Core/Files/SelectionRange` so it can be unit-tested; `AppState` only decides when to apply it. A filter change, `SetSelection` or `ClearSelection` drops the anchor, and an anchor that is no longer on the page degrades to a plain click rather than selecting rows the user cannot see.

**Icon-only buttons** must carry `aria-label` alongside `title`, and toggles should expose `aria-expanded`/`aria-current` so state is announced.

**Focus in dialogs**: an open dialog is the only thing Tab can reach. `ModalHost` captures `document.activeElement` before it re-renders (rendering the dialog can replace the node that opened it), traps Tab at the first/last focusable element, and restores the opener only after the dialog has left the DOM. Do not add a second modal surface that renders its own backdrop — every modal goes through `ModalService`, so this containment stays in one place.
