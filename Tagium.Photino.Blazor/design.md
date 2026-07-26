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
| `var(--surface-hover)` | Interactive hover state (semi-transparent white) |
| `var(--border-color)` | Structural separation lines, thin borders |
| `var(--text-color)` | Primary readable content |
| `var(--text-muted)` | Supporting descriptors, labels, and timestamps |
| `var(--primary-color)` | Focus rings, primary buttons, accents |
| `var(--primary-hover)` | Active primary button hover states |
| `var(--text-on-primary)` | Contrasting text color sitting on primary-colored backgrounds (WCAG AA) |
| `var(--danger-color)` | Destructive buttons, warnings, missing alerts |
| `var(--danger-hover)` | Active destructive hover states |
| `var(--folder-color)` | Directories, roots, and folder paths (`#ffca28`) |
| `var(--warning-color)` | High-visibility alert orange for missing file flags (`#ff9800`) |
| `var(--appearance-color)` | Theme preferences and creative palettes (`#ba68c8`) |

### Spacing & Grid System

| CSS Variable | Value | Purpose |
| :--- | :--- | :--- |
| `var(--space-2xs)` | 4px | Micro-paddings, element borders |
| `var(--space-xs)` | 8px | Grid gap, label-to-input gap, list item vertical padding |
| `var(--space-sm)` | 12px | Standard component padding, sub-panels |
| `var(--space-md)` | 16px | Workspace borders, grid container margins |
| `var(--space-lg)` | 24px | Layout spacing, empty state paddings |

### Border Radii & Transitions

| CSS Variable | Value | Purpose / Standard Application |
| :--- | :--- | :--- |
| `var(--radius-sm)` | 4px | Small buttons, badges, tag pills |
| `var(--radius-md)` | 6px | Input fields, file cards, small containers |
| `var(--radius-lg)` | 8px | Modals, preview windows, dialog frames |
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

### Settings Panel Tab Accents

Map category tab icons to these domains to optimize visual navigation:
* **Library Folders Tab**: Icon color `var(--folder-color)`
* **Appearance Tab**: Icon color `var(--appearance-color)`
* **Maintenance Tab**: Icon color `var(--primary-color)`
* **Active State**: Selected tab icon transition to `color: var(--primary-color)`.

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