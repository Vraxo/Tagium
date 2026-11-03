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
dotnet run --project Tagium.csproj
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

## 🗂️ Structure: one folder per feature

The repository is organized by **feature/domain**, not by technical layer. Every top-level folder is a feature, except the single thin `Core/` folder. Everything a feature needs — C# services, repositories, models, Razor components, scoped CSS, JavaScript, JSON themes — lives in that one folder.

| Folder | Feature | What lives there |
| :--- | :--- | :--- |
| [`Library/`](Library) | Library catalog | Folder roots, rescan/rename/move detection, hashing, import, duplicate handling, integrity checks, relocation, drag-and-drop |
| [`FileBrowser/`](FileBrowser) | Browsing files | Grid/list views, folder tree and breadcrumbs, inspectors, pagination, sorting, selection/view state, box-selection JS, launching files |
| [`Tags/`](Tags) | Tagging | Tag CRUD, aliases, hierarchies, file-to-tag links, tag sidebar, tag manager, tag selector/icon, tag inspector panel, tag normalization |
| [`Search/`](Search) | Search | Query compiler, search repository, search input component and its CSS |
| [`Thumbnails/`](Thumbnails) | Thumbnails & previews | Image/video thumbnail generation, cache, FFmpeg locator, video frame extraction, size slider, `tagium://` scheme handler |
| [`Metadata/`](Metadata) | Embedded metadata | ExifTool daemon, metadata model, tag-to-file metadata sync |
| [`Settings/`](Settings) | Preferences & appearance | Settings model/manager and enums, settings modal, JSON themes, theme service, UI zoom and sidebar-resizer JS |
| [`DataExport/`](DataExport) | Catalog export | JSON/CSV export and backup data shapes |
| [`Undo/`](Undo) | Undo | Undo stack and the tag-assignment / file-deletion commands |
| [`Notifications/`](Notifications) | Toast feedback | Toast model, service, types, and toast container component |
| [`AppShell/`](AppShell) | Desktop shell | Startup, Photino window, DI composition root, root component, layout, toolbar, status bar, shortcuts, modal host, OS dialogs/launcher, host page, global CSS, fonts and vendor icons, [`design.md`](AppShell/design.md) |
| [`Core/`](Core) | Shared, stable only | Database connection/schema, app-data paths, timing helper, and the two domain models every feature uses (`FileEntry`, `Tag`). See [`Core/README.md`](Core/README.md) |

The root holds only project-level files: `Tagium.csproj`, `Tagium.slnx`, `Directory.Build.props`, `_Imports.razor` (applies to components in every feature folder), and this README.

Rules of thumb:

- Adding a feature means adding a folder; removing a feature means deleting a folder.
- A file goes in a feature folder if it exists for that feature. If it does not fit an existing feature, create a new feature folder.
- A file goes in `Core/` only when several features use it **and** it is stable. When in doubt, keep it in the feature and move it later if a second feature needs it. `Core/` must stay thin.
- No `Features/`, `Commands/`, `Infrastructure/`, `Common/`, `Components/`, `Services/`, `Data/`, or `Models/` buckets — those group by layer, not by purpose.
- To change a feature, read that feature's folder first. If you have to open several folders to understand it, the boundaries need work, not more `Core/` files.

The two former projects (`Tagium.Core` and `Tagium.Photino.Blazor`) are now one root project, `Tagium.csproj`, so a single feature folder can hold both its C# services and its Razor UI. Source code itself was moved, not rewritten: namespaces and `using` directives are unchanged, and almost the entire diff is pure relocation. Feature-specific web assets stay in their feature folder and are linked into the `wwwroot` paths that Photino.Blazor serves at build time (see the `Content` items in `Tagium.csproj`); there is deliberately no source `wwwroot/` folder. `AppShell/ServiceRegistrar.cs` is the single composition root that wires the features' services together.

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
