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
| `var(--media-bg)` | Matte behind thumbnails, previews and video letterboxing — near-black on dark themes, lifted on light themes so media is not boxed in black |
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

The `Corner Style` preference selects the shared radius tokens used throughout the interface, including sidebar tag pills. The default values preserve the original design; circular controls keep their distinctive geometry.

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
* **Tag-Coloured Fills**: Any surface painted with a tag's own `ColorHex` (sidebar pills, inspector pills, swatches with labels) must derive its label ink from `TagColorContrast.GetTextColor(...)` instead of a fixed white, because tag colours range from near-white to near-black. Controls nested inside those pills inherit that ink and give feedback via opacity plus `var(--surface-hover)`, never via a fixed accent colour. Tag colours reach the stylesheet as custom properties — `--tag-color` for the fill or outline and `--tag-on-color` for the ink resolved by `TagColorContrast` — never as `background-color:`/`border-color:`/`color:` inside an inline `style` string, so the component supplies data and the component CSS decides where it is painted.
* **Tag Colour Dots**: Dots and tag-manager colour buttons use a thin, tag-tinted edge rather than a prominent neutral ring. This helps distinguish extreme colours from similar backgrounds without making every dot look white-ringed. The tag colour itself is the fallback when `color-mix` is unavailable; keyboard focus still uses the separate focus outline.
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
| Tag filters (`TagSidebar`) | `role="button"` + `tabindex="0"` + `aria-current` on the active filter |
| Folder tree (`FolderSidebar`) | container `role="tree"` + `aria-labelledby`; rows `role="treeitem"` with `aria-level`, `aria-expanded`, `aria-selected` and a roving tabindex; children live in `role="group"` inside the parent treeitem |
| Modals (`ModalHost`) | `role="dialog"` + `aria-modal` + `aria-labelledby`, `tabindex="-1"`; focus moves into the dialog on open, Escape closes, `Tab`/`Shift+Tab` cycle inside the dialog, and focus returns to the opener on close |

**Roving tabindex**: library surfaces expose exactly one tab stop (the first selected item, otherwise the first item on the page); every other item is `tabindex="-1"` and reached with the arrow keys. Never make every row a tab stop.

**Selection gestures**: a plain click (or a plain arrow) replaces the selection and sets the anchor; `Ctrl`+click toggles and re-anchors; `Shift`+click and `Shift`+arrow extend an inclusive range from the anchor across the current page in display order and leave the anchor in place, so repeated shifts grow the same range. The span maths lives in `Tagium.Core/Files/SelectionRange` so it can be unit-tested; `AppState` only decides when to apply it. A filter change, `SetSelection` or `ClearSelection` drops the anchor, and an anchor that is no longer on the page degrades to a plain click rather than selecting rows the user cannot see.

**Folder tree keys**: Up/Down move one visible row and clamp at the ends, Right expands a collapsed folder or steps into its first child, Left collapses or steps out to the parent, Home/End jump to the ends, and Enter/Space select the folder. Expansion is therefore fully keyboard-reachable, which makes the chevron a mouse-only affordance: it is `aria-hidden`, never a separate tab stop, and never a nested button inside the treeitem. The row maths lives in `Tagium.Core/Library/FolderTreeNavigation` and is unit-tested there; the component only decides when to apply it.

**Icon-only buttons** must carry `aria-label` alongside `title`, and toggles should expose `aria-expanded`/`aria-current` so state is announced.

**Focus in dialogs**: an open dialog is the only thing Tab can reach. `ModalHost` captures `document.activeElement` before it re-renders (rendering the dialog can replace the node that opened it), traps Tab at the first/last focusable element, and restores the opener only after the dialog has left the DOM. Do not add a second modal surface that renders its own backdrop — every modal goes through `ModalService`, so this containment stays in one place.

**Live regions**: library selection and filter counts are announced by exactly one polite live region — `AppShell/LibraryAnnouncer`, rendered by `MainLayout` and never removed. It must stay mounted and only have its text replaced: a region that appears with its message already in the DOM is frequently not announced at all. Announcements are debounced by 400 ms so arrow-key repeats collapse into a single sentence, and the wording lives in `Tagium.Core/Files/LibraryAnnouncement` so the phrasing and its plural and clamp edge cases are unit-tested. Other surfaces keep their own regions (`TagManagerView` results, the `TagSidebar` empty-filter notice); never mirror the same state into two of them.

**Recent searches**: the search box offers its history as a labelled `role="group"` of real `<button>`s, so Tab reaches them and Enter or Space applies one — no custom arrow-key model and no half-implemented `combobox` role. Escape closes the list without touching the query; choosing a suggestion fills the box and re-records it as most recent. The list stays open until a 200 ms grace period after blur so a click on it is not swallowed by the blur that precedes it. The search box also carries a one-button syntax reference beside it: a real `<button>` with `aria-expanded`, opening a read-only panel of syntax-to-meaning rows that takes the same slot as the history list — never both at once, and Escape closes either.


---

## 8. Windows 11 Interface Style (Fluent 2)

The optional Windows 11 interface is a Fluent 2-inspired WebView implementation. It must preserve the application's existing behavior, semantic theme colors, keyboard support, and accessibility contracts.

### Visual hierarchy

* Use Segoe UI Variable with Segoe UI fallback.
* Use the Windows type ramp as a guide: 12px captions, 14px body text, 18px large body, 20px subtitles, and 28px page titles. Do not globally shrink body copy to 12px.
* Use a persistent left navigation pane, with a narrow accent indicator and a restrained selected-item surface.
* The page command bar presents the current page identity, search, and persistent actions in stable positions. Do not duplicate primary navigation in the command bar.
* Keep the file workspace visually quiet. Selection must be clear without turning every card into a raised tile.
* Inspector, settings, and dialogs should share the same surface hierarchy and spacing rhythm.

### Controls and surfaces

* Standard controls use a 32px baseline height, 4px control corners, visible focus, and consistent hover, pressed, selected, and disabled states.
* Use semantic theme tokens as the source of color. Derived Fluent layers may use `color-mix()`, but must not hardcode a second unrelated palette.
* Elevated menus and dialogs use a restrained shadow and clear separation. Blur is reserved for floating surfaces, never used as a substitute for surface contrast.
* Keep the Windows 11 style independent of the user's global corner-style setting; its geometry is part of the selected interface language.
* Respect `prefers-reduced-motion`.

### Platform boundary

* This is a WebView implementation, not actual WinUI. HTML controls must retain native semantics, keyboard behavior, and accessible names.
* Keep Photino's normal decorated window for every interface style. Windows owns the title bar, caption buttons, system menu, snapping, and resize borders; do not replace these with HTML controls.
* Mica is a Windows native backdrop, not a CSS blur. On Windows 11 22H2 (build 22621) and later, the host requests `DWMSBT_MAINWINDOW` through `DWMWA_SYSTEMBACKDROP_TYPE` only when the Windows 11 interface style is selected at startup.
* Do not enable Photino's `Chromeless` or `Transparent` options to obtain Mica. Photino's transparent path requires a chromeless window, which sacrifices the native frame.
* The DWM backdrop is requested behind the full top-level window, but the client area must also expose the system material. The Windows 11 Mica path therefore calls `DwmExtendFrameIntoClientArea` with negative margins before applying the backdrop attribute. This extends the DWM frame through the client area without changing Photino's normal `WS_OVERLAPPEDWINDOW` styles or replacing the native caption controls. WebView2's transparent default background then allows the DWM material to show through HTML surfaces that are themselves transparent.
* The no-redirection style and transparent WebView background are enabled only for the Windows 11 interface style. Classic and macOS-inspired modes keep their ordinary opaque rendering and native window frame.
* If the native style or DWM backdrop request fails, the application retains opaque Fluent surfaces and the standard decorated window. No custom title bar, resize hit-testing, layered-window fallback, or synthetic caption controls are used.
* Switching into or out of the Windows 11 Mica configuration requires a restart so the native backdrop is reapplied consistently. Other operating systems and unsupported Windows versions retain opaque UI.


### Native Mica with the decorated Photino window

* The Windows 11 interface keeps Photino's standard decorated WS_OVERLAPPEDWINDOW; it does not enable Chromeless, Transparent, or WS_EX_LAYERED.
* Before Photino starts its native message loop and creates the WebView2 controller, the host sets WEBVIEW2_DEFAULT_BACKGROUND_COLOR=00000000. WebView2's transparent default background lets HTML pixels with transparent backgrounds reveal the hosting window.
* The host calls DwmExtendFrameIntoClientArea with {-1, -1, -1, -1} margins before setting the DWM backdrop. This extends the DWM frame across the client area while preserving the ordinary decorated window styles and native caption controls.
* DWM receives DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW, which requests Mica behind the entire top-level window bounds. The WebView2 background and outer HTML surfaces are transparent only when this native Mica configuration is active.
* This is a native-composition experiment, not a CSS imitation. It still requires Windows 11 runtime validation because CI cannot confirm that Photino's HWND, WebView2 child surface, and DWM backdrop compose correctly together. If this path fails, keep the native frame and revert to opaque Fluent surfaces rather than replacing the title bar.
