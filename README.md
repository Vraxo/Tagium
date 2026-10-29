# 🏷️ Tagium

Desktop media library and file tagger for Windows, built with [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0), [Photino.Blazor](https://www.tryphotino.io/), and SQLite.

## ✨ Features

- Index multiple local folders in place without moving or copying your files
- Track renamed and moved files across scans using SHA-256 hashes
- Browse media in grid or list views, or navigate by folder tree
- Create color-coded tags with optional emoji labels, batch-tag selections, and undo tag changes
- Read embedded metadata (`Subject`, `Keywords`, `Title`, `Description`, `Rating`) on scan and sync tag updates back to files via ExifTool
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

## 🔍 Search Syntax

Space-separated terms combine with `AND`. Prefix any token with `-` to exclude matches.

| Filter | Matches | Example |
| :--- | :--- | :--- |
| `<term>` | File name, path, tag name, or tag alias | `landscape` |
| `-<token>` | Excludes files matching `<token>` | `-draft -ext:gif` |
| `ext:<ext>` | File extension (`extension:` also supported) | `ext:webp` |
| `size:<op><val>` | File size (`=`, `>`, `<`, `>=`, `<=` with `b`, `kb`, `mb`, `gb`) | `size:>10mb` |
| `rating:<op><n>` | Imported file rating (`=`, `>`, `<`, `>=`, `<=`) | `rating:>=4` |
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
