# Tagium UI/UX Design System Guidelines

This document serves as the absolute, non-negotiable contract for all visual developments in Tagium. All styles must strictly adhere to the tokens defined here. No "magic numbers" or localized hex codes are allowed.

## 1. Visual Design Philosophy
Tagium is a highly compact, professional, and performance-minded tool. Visual elements must feel snappy, structurally cohesive, and aligned with a strict semantic hierarchy. 

* **No Hex Codes**: Never write raw colors (e.g., `#1a1a1a` or `#333`) in any component or page. Always map colors to semantic custom CSS properties.
* **8px Grid System**: Layouts, padding, margins, and component gaps must be multipliers of the 8px grid. Use spacing custom properties.
* **Minimalist Feedback**: Keep hover states subtle and rounded. Ensure borders are thin (`1px`) and highly contrasting.

---

## 2. Global Semantic Tokens

All CSS variables are declared globally in `app.css`. Use them according to these functional definitions:

### Colors

| Token | CSS Variable | Semantic Purpose |
|---|---|---|
| Background | `var(--bg-color)` | Core app background (dark charcoal) |
| Panel Background | `var(--panel-bg)` | Sidebar and secondary panel background |
| Surface Background | `var(--surface-bg)` | Cards, input fields, and elevated elements |
| Surface Hover | `var(--surface-hover)` | Interactive hover state (semi-transparent white) |
| Border Color | `var(--border-color)` | Structural separation lines, thin borders |
| Text Primary | `var(--text-color)` | Primary readable content |
| Text Muted | `var(--text-muted)` | Supporting descriptors, labels, and timestamps |
| Primary Brand | `var(--primary-color)` | Focus rings, primary buttons, accents |
| Primary Hover | `var(--primary-hover)` | Active primary button hover states |
| Danger Accent | `var(--danger-color)` | Destructive buttons, warnings, missing alerts |
| Danger Hover | `var(--danger-hover)` | Active destructive hover states |

### Spacing (8px Scale)

| Token | CSS Variable | Size | Purpose |
|---|---|---|---|
| `space-2xs` | `var(--space-2xs)` | 4px | Micro-paddings, element borders |
| `space-xs` | `var(--space-xs)` | 8px | Grid gap, label-to-input gap, list item vertical padding |
| `space-sm` | `var(--space-sm)` | 12px | Standard component padding, sub-panels |
| `space-md` | `var(--space-md)` | 16px | Workspace borders, grid container margins |
| `space-lg` | `var(--space-lg)` | 24px | Layout spacing, empty state paddings |

### Border Radii

| Token | CSS Variable | Value | Purpose |
|---|---|---|---|
| Small | `var(--radius-sm)` | 4px | Small buttons, badges, tag pills |
| Medium | `var(--radius-md)` | 6px | Input fields, file cards, small containers |
| Large | `var(--radius-lg)` | 8px | Modals, preview windows, dialog frames |

### Transitions

* **Fast Interactive**: `var(--transition-fast)` (0.1s ease-in-out) — hover, background transitions, selections.
* **Standard Interactive**: `var(--transition-normal)` (0.15s ease-in-out) — drawers, modal overlay fades, progress bars.

---

## 3. Mandatory AI Directives
When building, modifying, or refactoring UI components for Tagium:
1. **Never use inline styles (`style="..."`)** for colors, padding, margins, or heights.
2. **Never define new local colors** unless explicitly instructed.
3. **Always keep CSS isolated** to the companion `.razor.css` file where applicable.
4. **Ensure high contrast** and preserve readable dark-theme hierarchy at all times.