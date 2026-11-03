# Core

This is the **only** non-feature folder in the repository, and it is intentionally thin.

A file belongs here only when both are true:

1. it is used by more than one feature, and
2. it is stable infrastructure or a shared domain model, not feature behavior.

What is here:

| File | Why it is shared |
| :--- | :--- |
| `DatabaseManager.cs` | Owns the SQLite `library.db` / `cache.db` connections and schema for every repository (`Library`, `Tags`, `Search`, `DataExport`, …). |
| `AppDataHelpers.cs` | Resolves `%LOCALAPPDATA%\Tagium` paths used by the database, settings, themes, thumbnails, and logging. |
| `LogTiming.cs` | Logging/timing helper used by scanning, search, metadata, and thumbnail generation. |
| `FileEntry.cs` | The catalog entry every feature reads or writes. |
| `Tag.cs` | The tag every feature reads or writes. |

If a file here starts to know about one feature in particular, move it into that feature's folder. If `Core/` starts growing, the feature boundaries are wrong — fix the boundaries instead of adding more shared files. Never use this folder as a `Common/` or `Infrastructure/` catch-all.
