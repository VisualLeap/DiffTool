# DiffTool

A read-only Windows desktop viewer for local Git changes. Select a changed file to see a unified diff with deleted lines in red, added lines in green, and old/new line numbers.

## Run

Double-click `launch.cmd`, then click **Open repository…** and choose your repository folder in the standard Windows Explorer-style Open dialog. The launcher builds the executable on its first run. You can also open `bin\DiffTool.exe` directly after building.

The viewer starts maximized with **Full file** enabled.

**Recent repositories** lists the last five opened repositories. With no command-line path, the viewer reopens the most recent repository on startup. History is stored in `%LOCALAPPDATA%\DiffTool\recent-repositories.txt`.

Existing recent-repository history from the previous application name is imported automatically on first launch.

To open a particular repository from a terminal:

```powershell
.\launch.cmd "C:\path\to\repository"
```

Requires Windows with .NET Framework 4.5 or later and Git on PATH. No SDK, package downloads, server, or browser is needed. Build with the included .NET Framework compiler:

```powershell
.\build.bat
.\build.bat -Test
```

Build output and test screenshots go in `bin/`, which is excluded from Git. Repository history and user preferences stay in Local AppData, outside the project.

The application icon is `assets/DiffTool.ico`; replace that file and rebuild to update the executable, window, and taskbar icons.
## Review changes

- **Branch:** choose a local or remote-tracking branch to browse its history and latest commit. This does not check out the branch; working-change comparisons still use the current checkout.
- **All working changes:** compares HEAD with the working folder, including staged and untracked files. Staged changes subsequently reversed in the working folder have no net diff in this view; use the staged view to inspect the index.
- **Staged changes:** compares HEAD with the index.
- **Unstaged changes:** compares the index with the working folder and includes untracked files.
- **Previous commits:** listed after the three working-change options in the **Compare** dropdown; select a commit by hash, date, and subject to review its changes against its first parent. The initial commit compares with an empty tree. The selector lists the latest 100 commits reachable from the selected branch; **Load older commits** adds another 200. Viewing history leaves the checkout unchanged.
- The compact file list has collapsible **Deleted**, **Added**, and **Modified** sections with counts. Added includes untracked/copied files; Modified includes renames, conflicts, and type changes. Hover a file for its specific status. Ignored files are excluded.
- All paths include file sizes (base-1024 units). Deleted files use the comparison's original blob; other files use the working file, the index blob in the staged view, or the selected commit's blob in history. Git LFS pointers report the recorded asset size.
- All text files show total lines, including blank lines and comments. Counts use the same file version as sizes, including the original version for deleted files. Binary files and Git LFS pointers omit line counts. UTF-8 and BOM-marked Unicode text are supported.
- Files initially group by folder, with a small vertical gap wherever adjacent files belong to different folders.
- Filename, Size, Lines, and Changed have separate columns; unavailable values show a dash. Changed shows green additions and red deletions for the selected comparison, while Lines remains the full file's total. Widen the left pane to see longer paths.
- Click a column header to sort ascending within Deleted, Added, and Modified; click again for descending. Changed sorts by additions plus deletions. Missing values stay last; the selected file and sort order survive filtering and refresh.
- The file list minimap replaces its vertical scrollbar with one centered block per section: red for Deleted, green for Added, and blue for Modified. When the list fits without scrolling, blocks align with the actual sections. Longer lists scale to the minimap's full height; click or drag its viewport to scroll.
- The app opens in a normal window and remembers its last position and size across launches.
- Filter the list by path, select a file, and scroll the diff. The file-list pane starts at 34.155% of the window width (subject to minimum pane sizes). Drag the divider to resize the panes. The two number columns show the old and new line numbers.
- Toggle **Full file** at the far right of the file header to include all unchanged lines alongside red deletions and green additions. Turn it off to return to changes with nearby context. The toggle carries across file selections and comparison views.
- The right-hand minimap shows tiny strokes for file contents, with red deletions and green additions. Click to jump or drag the highlighted viewport to scroll; mouse-wheel, trackpad, and keyboard scrolling also work. It follows the current compact/full-file view.
- Both panes and their minimaps use the same direct, pixel-level wheel/trackpad scrolling. Every input event moves the view immediately, retaining fractional movement and following the Windows line/page scrolling setting. There is no added animation or momentum; dragging and keyboard navigation also respond directly.
- Long source lines use a dark horizontal scrollbar. Drag the thumb or click the track; when focused, arrow keys, Page Up/Down, Home, and End also scroll horizontally.
- **Copy diff**, beside the filename, copies the selected file's full diff. **F5** refreshes after you change files outside the app. **Ctrl+O** opens a repository; **Ctrl+F** focuses the filter.

Binary and empty files show explanatory messages. Untracked text previews assume UTF-8 and are limited to 2 MB. Tracked diff previews are limited to 4 MB; copying retains the complete Git output. Unmerged files may use Git's combined-diff format, shown as text without per-line numbers. The viewer does not stage, modify, discard, or commit files.

`tests\Tests.cs` exercises real temporary repositories and captures a GUI smoke-test screenshot in `bin\smoke-test.png`.
