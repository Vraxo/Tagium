--- START OF FILE troubleshooting.md ---



\# 🛠️ Tagium Troubleshooting \& Debugging Log



This document serves as a reference for known issues, architectural quirks, and critical bug fixes in the Tagium codebase. Use this to diagnose recurring problems or understand the "why" behind specific implementation details.



---



\## 🕵️ 1. Blank or Unresponsive Inspector Panel



\*\*Problem:\*\* The right-hand "Inspector" panel (File Details) remains completely empty (gray background) upon application startup or fails to update when a file is selected.

\*\*Observed Behavior:\*\* No "Select a file" message appears. Clicking files changes the selection in `AppState`, but the Inspector does not reflect the change.



\### 🩺 Symptoms



\- \*\*👁️ UI:\*\* The right sidebar is a blank color block. No text, no buttons.

\- \*\*📝 Logs:\*\* No visible errors in the console, but breakpoints inside `FileInspector.razor` render block are not hit.

\- \*\*🏗️ Layout:\*\* The panel exists in the DOM but has no children.



\### 🔍 Root Cause



1\. \*\*🏁 Lifecycle Race Condition:\*\* The component subscribed to `AppState.OnSelectionChange` inside `OnInitialized`, but did not perform an initial data fetch (`UpdateView()`). If the component loaded \*after\* a selection was made (or if the default state was "None"), it waited passively for an event that wouldn't fire until the \*next\* user interaction.

2\. \*\*🕳️ Rendering Logic Gap:\*\* The Razor view used an `if / else if` chain. If the state didn't strictly match `Single`, `Multi`, or `None` (e.g., during a stale ID lookup), execution fell through the bottom of the logic chain, resulting in an empty DOM.

3\. \*\*💥 Silent Render Crash:\*\* If an exception occurred during the render logic (e.g., accessing a property on a null `SelectedFile`), Blazor's renderer would abort the component tree update silently in production modes, leaving the UI stale.



\### 💉 Solution



1\. \*\*⚡ Immediate State Sync:\*\* Explicitly call `UpdateView()` inside `OnInitialized()` to ensure the UI reflects the current `AppState` immediately on mount.

2\. \*\*🛡️ Defensive Rendering:\*\*

&nbsp;  - Added a final `else` block to handle indeterminate states (shows "Loading...").

&nbsp;  - Wrapped the data fetching logic in a `try/catch` block. If data fetching fails, explicitly set the state to `InspectorMode.Error` to show a "Retry" button instead of a blank void.

3\. \*\*📐 CSS Flex Safety:\*\* Added `flex-shrink: 0` to `.right-panel` in `MainLayout.razor.css` to prevent the browser from "crushing" the inspector to 0 width if the file grid content gets too wide.



---



\## 🖼️ 2. Broken Thumbnails \& Previews (Custom Scheme)



\*\*Problem:\*\* Images or video previews display the "broken image" icon, even though the file exists on disk.

\*\*Observed Behavior:\*\* `<img src="tagium://thumbnail/123">` fails to load.



\### 🩺 Symptoms



\- \*\*👁️ UI:\*\* Broken image icons in the Grid or Inspector.

\- \*\*📝 Console:\*\* Errors like `Failed to load resource: net::ERR\_UNKNOWN\_URL\_SCHEME` or generic 404s.



\### 🔍 Root Cause



1\. \*\*🔗 Static Context Loss:\*\* The `tagium://` scheme is handled by a static method (`SchemeHandlers.HandleTagiumScheme`). It requires access to the `FileRepository` to resolve file IDs to paths. If `SchemeHandlers.ServiceProvider` is not assigned in `Program.cs` immediately after building the app, the handler cannot resolve dependencies.

2\. \*\*🧵 Threading/Async in Scheme:\*\* WebView2 scheme handlers are synchronous streams. Attempting to perform complex `async` database lookups or heavy image processing inside the handler can deadlock or time out the request.



\### 💉 Solution \& Prevention



1\. \*\*💉 Service Injection:\*\* Ensure `SchemeHandlers.ServiceProvider = app.Services;` is called in `Program.cs` before `app.Run()`.

2\. \*\*⚡ Fast Path Logic:\*\* The Scheme Handler uses synchronous IO (`new FileStream`). Do not convert this to async.

3\. \*\*🏷️ ID-Based Lookup:\*\* The URL format is `tagium://preview/{FileId}`. The handler resolves `{FileId}` -> `FilePath` via the DB, then streams the file. If the DB lookup returns null, it returns `Stream.Null` (404).



---



\## 💾 3. "File Locked" Errors via SQLite



\*\*Problem:\*\* Occasionally, operations like `Add Tag` or `Import Files` throw an exception: `Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'`.



\### 🩺 Symptoms



\- \*\*🛑 UX:\*\* The operation fails, usually with a red Toast notification (if error handling is active) or a console log.

\- \*\*🐢 Performance:\*\* The app might freeze briefly before failing.



\### 🔍 Root Cause



1\. \*\*🔄 Concurrency:\*\* SQLite is a file-based database. While `WAL` (Write-Ahead Logging) mode is enabled in `DatabaseManager.cs`, attempting to write from multiple threads simultaneously (e.g., the UI thread updating a tag while a background task imports files) can cause contention.

2\. \*\*👻 Un-disposed Connections:\*\* If a `SqliteConnection` is created but not disposed (missing `using` statement), the file lock persists until the Garbage Collector runs.



\### 💉 Solution



1\. \*\*⚡ Short-Lived Connections:\*\* Always use `using new SqliteConnection(...)` inside the repository methods. Never share a connection instance across the class or application.

2\. \*\*📝 Write-Ahead Logging:\*\* Ensure `PRAGMA journal\_mode = WAL;` is executed on database initialization (already implemented in `DatabaseManager`).

3\. \*\*🛑 Serialized Writes:\*\* The `LibraryService` serves as a natural bottleneck. Ensure critical write operations are not run in parallel `Task.Run` loops without careful consideration. The current `ImportBatchAsync` processes sequentially to avoid this.

