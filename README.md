# 🏷️ Tagium

[![CI](https://github.com/Vraxo/Tagium/actions/workflows/ci.yml/badge.svg)](https://github.com/Vraxo/Tagium/actions/workflows/ci.yml)

Desktop media library and file tagger for Windows, built with [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0), [Photino.Blazor](https://www.tryphotino.io/), and SQLite.

## ✨ Features

- Index multiple local folders in place without moving or copying your files
- Track renamed and moved files across scans using SHA-256 hashes
- Browse media in grid or list views, or navigate by folder tree
- Choose 50, 100 (default), or 250 files per page; the preference is remembered across sessions
- Sort the library by date added, name, path, size, modified time, or rating (ascending/descending)
- Filter with a query language — `tag:`, `rating:`, `size:`, `ext:`, `added:`, `modified:`, `has:`, `missing:`, `untagged`, the `artist:`/`series:`/… category filters and `-` to negate — with an in-app syntax reference on the search box and the last ten queries recalled from a dropdown under it
- Create color-coded tags with optional emoji labels, batch-tag selections, and undo tag changes
- Explore up to 120 of the most-used tags in an interactive graph, rendered locally with PixiJS and laid out with D3 force simulation; tag emojis appear inside larger nodes, nodes scale by file count, links show shared files or explicit hierarchy, neighbors respond to forces while dragging, and Obsidian-style display/force controls tune the graph
- Read embedded metadata (`Subject`, `Keywords`, `Title`, `Description`, `Rating`) on scan and sync tag updates back to files via ExifTool
- Rate files with a 1–5 star rating from the inspector (single or multi-select), searchable with `rating:` and undoable; ratings sync back to files via ExifTool
- Inspect read-only file details in the inspector: type, size, added/modified dates, content hash, and embedded metadata (title, description)
- Find duplicate files by content hash (View Options → Find Duplicates); scans also flag newly imported files that duplicate existing entries
- See library statistics — file counts, total size, missing/duplicate counts, tags, and a per-extension breakdown — from View Options → Library Statistics
- Generate cached image and video thumbnails in the background
- Customize the interface with JSON color themes, UI zoom, and resizable sidebars
- Check library integrity, purge missing entries, export catalogs to JSON or CSV, and back up or restore the database

## 🖼️ Supported Formats

| Type | Extensions |
| :--- | :--- |
| Images | `.jpg`, `.jpeg`, `.png`, `.bmp`, `.gif`, `.webp` |
| Videos | `.mp4`, `.mkv`, `.mov`, `.avi`, `.wmv`, `.webm` |

## 🧰 Requirements

| Dependency | Status | Notes |
| :--- | :--- | :--- |
| Windows (x64) | Required | Targets `win-x64` |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Required | Builds and runs the solution |
| [ExifTool](https://exiftool.org/) | Optional | `exiftool.exe` on `PATH` to read and write embedded file metadata |
| [FFmpeg](https://ffmpeg.org/) | Optional | `ffmpeg.exe` and `ffprobe.exe` in the app directory or on `PATH` for video thumbnails |

Without ExifTool or FFmpeg, Tagium still indexes and tags files in SQLite and generates placeholder thumbnails for videos.

## 🚀 Quick Start

```powershell
git clone https://github.com/Vraxo/Tagium.git
cd Tagium
dotnet build Tagium.slnx
dotnet run --project Tagium.Photino.Blazor
```

Build outputs are written to `artifacts/bin/`. On first launch, open **Settings > Library Folders** to add folders and run an initial scan.

## 🧪 Tests

Unit tests live in [`Tagium.Core.Tests/`](Tagium.Core.Tests) and use [xUnit v3](https://xunit.net/) with [AwesomeAssertions](https://awesomeassertions.org) for fluent assertions:

```powershell
dotnet test Tagium.Core.Tests
dotnet test Tagium.slnx
```

## 🔍 Search Syntax

Space-separated terms combine with `AND`. Prefix any token with `-` to exclude matches.

| Filter | Matches | Example |
| :--- | :--- | :--- |
| `<term>` | File name, path, tag name, or tag alias | `landscape` |
| `-<token>` | Excludes files matching `<token>` (works with every filter below) | `-draft -ext:gif` |
| `ext:<ext>` | File extension (`extension:` also supported) | `ext:webp` |
| `size:<op><val>` | File size (`=`, `>`, `<`, `>=`, `<=` with `b`, `kb`, `mb`, `gb`) | `size:>10mb` |
| `rating:<op><n>` | Imported file rating (`=`, `>`, `<`, `>=`, `<=`) | `rating:>=4` |
| `tag:<val>` | Has a tag or alias matching `<val>` (tag names/aliases only, not file names) | `tag:landscape` |
| `untagged` | Files with no tags (`-untagged` = has at least one tag) | `untagged` |
| `missing:<bool>` | Missing flag (`true`/`false`/`yes`/`no`/`1`/`0`) | `missing:true` |
| `has:<field>` | Field is present: `rating`, `title`, `description`, or `tags` | `has:rating` |
| `added:<op><date>` / `modified:<op><date>` | Date added/modified in ISO format (`=`, `>`, `<`, `>=`, `<=`; a bare date means on/after) | `added:>=2026-01-01` |
| `<category>:<val>` | Tag in category (`artist`, `character`, `series`, `location`, `person`, `franchise`, `species`, `year`, `meta`) | `artist:moebius` |

## ⌨️ Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl+F` | Focus search bar |
| `Ctrl+R` | Rescan library folders |
| `Ctrl+Z` | Undo last tag change |
| `Ctrl+A` | Select all files on current page (grid view) |
| `Arrow Keys` | Move selection across files (grid view) |
| `Enter` | Open selected file in default app (grid view) |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | Zoom in, zoom out, or reset zoom (`Ctrl+Scroll` also supported) |

## 🗂️ Project Layout

| Path | Description |
| :--- | :--- |
| [`Tagium.Core/`](Tagium.Core) | SQLite/Dapper repositories, search query compiler, folder scanner, SkiaSharp/FFmpeg thumbnails, ExifTool daemon, JSON/CSV export, and undo |
| [`Tagium.Photino.Blazor/`](Tagium.Photino.Blazor) | Photino desktop shell, Blazor UI components, custom `tagium://` preview/thumbnail scheme, and themes |
| [`Tagium.Photino.Blazor/design.md`](Tagium.Photino.Blazor/design.md) | UI design tokens, spacing grid, and component rules |
| [`package.json`](package.json) and [`scripts/`](scripts) | Pinned local PixiJS/D3 force bundle inputs; run `npm ci && npm run build:tag-graph` to regenerate the checked-in offline vendor bundle |

## 💾 Local Data

Application state is stored in `%LOCALAPPDATA%\Tagium`:

| Path | Contents |
| :--- | :--- |
| `library.db` | Tags, hierarchies, aliases, and configured library roots |
| `cache.db` | Indexed file entries and file-to-tag associations |
| `settings.json` | UI preferences, sort settings, sidebar widths, and zoom level |
| `Thumbnails/` | Cached JPEG thumbnails |
| `Themes/` | Custom JSON themes (ships with `Classic Light`, `Monokai Pro`, and `Nordic Frost`) |
| `app*.log` | Daily diagnostic logs (7-day retention) |
