# Systenclin

Windows cleanup and optimization. Built from scratch — **no code, assets, or branding reused**.

## Status

| Version | Module | Status |
|---|---|---|
| **v0.1** | Temporary file cleanup | ✅ done |
| **v0.2** | Browser privacy | ✅ done |
| **v0.3** | Startup manager | ✅ done |
| **v0.4** | Memory, DNS, processes, and live monitor | ✅ done |
| **v0.5** | Disk and space (optimization, duplicates, large files, folders) | ✅ done |
| **v0.6** | Applications: update and uninstall (winget) | ✅ done |
| **v0.7** | Restore point + undo (Recycle Bin, backups, history) | ✅ done |
| **v0.8** | Extras: broken shortcuts, recycle bin, status bar, 1-click boost, conservative network | ✅ done |

## Run

```bash
cd Systenclin
dotnet run -c Release
```

Ready-to-run executable: `dist/Systenclin.exe` (≈ 775 KB + `e_sqlite3.dll`),
single file, requires the .NET Desktop Runtime 10.

## Tests

```bash
cd Testes/Systenclin.Testes
dotnet run -c Release
```

**62 checks, 0 failures.** The focus is on what can go dangerously wrong, not
pretty coverage. The harness compiles `Core/*.cs` directly — no UI — and uses
the desktop framework only for the boot-event EventLog.

---

## v0.1 — Temporary files

**Scan** — read-only. Measures 16 known targets: TEMP, Windows Update cache,
thumbnails, error reports, NuGet/npm/pip caches, old logs.

**Cleanup** — files go to the **Recycle Bin** (recoverable). Cache folders are
removed for good, and the UI says so instead of promising undo that does not
exist.

### Safety rules (covered by tests)

1. **Never crosses junctions or symbolic links.** A junction in `%TEMP%`
   pointing to `C:\Windows` would make the cleaner delete Windows.
2. **9 protected areas**, checked during the scan **and again** at delete time.
3. **Rejects drive roots, shallow paths, and traversal** (`..`).
4. **High risk is never pre-checked.** Neither is the Recycle Bin.
5. **One failure does not stop the rest.** The reason gets logged.

---

## v0.2 — Browser privacy

Reads and cleans history, cookies, form data, downloads, permissions, cache,
and saved tabs from **Chrome, Edge, Firefox, Brave, Opera, and Vivaldi** —
every profile of each (on this machine: 8 profiles).

### What changes from v0.1

v0.1 measured bytes. Here **measuring is not enough** — the user must see what
is about to be deleted. Each item shows a readable preview:

```
Browsing history — Google Chrome — Default
  10/15/26 22:14  ×3   .NET documentation
      https://learn.microsoft.com/dotnet/...
  15/10/26 21:58  ×1   GitHub — systenclin
      https://github.com/...
```

### The three non-negotiables

**1. Browser closed for database data.**

There is no safe way to write to an open browser's SQLite file: it keeps state
in memory and overwrites, or corrupts the database. The app detects this and
**refuses**, telling you which browser to close. Products that promise
"cleaning with the browser open" either lie or corrupt data.

Cache and saved tabs are files on disk — those can be cleaned right now.

**2. Backup before deleting.**

Every database is copied to `%LOCALAPPDATA%\Systenclin\backup-privacidade\`
before any write. It is the only honest way to offer undo — v0.1 could not,
this one can. The **Backups** button lists them and explains how to restore.

**3. Whole category, never a single cookie.**

Half-measures leave users thinking they cleaned it when they did not.

### Reading with the browser open

The app reads even while Chrome and Edge are running, opening the file with
`FILE_SHARE_READ | FILE_SHARE_WRITE`. A plain `File.ReadAllBytes` would fail
with "file in use".

### Risks, stated clearly

| Data | Risk | Checked by default? |
|---|---|---|
| Browsing history | low | yes |
| Browser cache | low | yes |
| Download history | low | yes |
| Filled forms | medium | **no** |
| Saved tabs | medium | **no** |
| Site permissions | medium | **no** |
| **Cookies** | **high** | **no** |

Cookies sign you out of every account. The warning appears in the confirmation
dialog, in plain text, not hidden in a tooltip.

---

## v0.3 — Startup manager

View, disable, and re-enable what starts with Windows, from five sources: the
user `Run` key, the machine `Run` key (64- and 32-bit views), and both Startup
folders.

**Disabling loses nothing.** The original value — name, type, and data — is
written to `%LOCALAPPDATA%\Systenclin\inicializacao.json` **before** leaving
the registry (and the store is rolled back if the write fails). Startup folder
files are moved to `inicializacao_desativados`. Enabling undoes it through the
same saved path. Machine items require administrator, and the UI says so
instead of failing silently.

## v0.4 — Memory, DNS, processes, and monitor

- **Free memory** — trims the working set of processes above 20 MB.
- **Flush DNS cache** — direct flush.
- **Processes** — top 15 by memory; killing confirms with two clicks.
- **Live monitor** — CPU and RAM with 60 s history, in the Optimization tab
  and in the footer since v0.8. `GetSystemTimes` + `GlobalMemoryStatusEx`, no
  `PerformanceCounter`. Runs only while the window is open — nothing resident.

## v0.5 — Disk and space

- **Optimize disk** — detects SSD or HDD (volume seek penalty) and runs
  `defrag /O`: TRIM on SSDs, defragmentation on HDDs. Requires administrator.
- **Duplicates** — grouped by hash; checked ones go to the Recycle Bin.
- **Large files** — sorted by size; Recycle Bin.
- **Folder analysis** — what takes up space, folder by folder.

## v0.6 — Applications (winget)

Update (one or all) and uninstall via winget. Without winget, the UI explains
instead of throwing an error. Uninstall uses winget's own flow — nothing
silent.

## v0.7 — Restore and undo

- **Restore point** — optional, before cleaning, when running as administrator
  (the checkbox sits in the confirmation bar).
- **Layered undo** — files go to the Recycle Bin; browser data has backups in
  `%LOCALAPPDATA%\Systenclin\backup-privacidade\`; every operation lands in
  the **History** (`CleanLog`, JSON).

## v0.8 — Extras

- **Broken shortcuts** — `.lnk` files pointing to missing targets, on the
  Desktop and Start Menu; checked ones go to the Recycle Bin.
- **Recycle Bin** — counts items and size (`SHQueryRecycleBin`) and empties
  with two-click confirmation (`SHEmptyRecycleBin`).
- **Status bar** — CPU and RAM in the footer, next to Backups and History.
- **1-click boost** — frees idle memory and flushes the DNS cache in one
  action, with a consolidated result; the button disables itself while another
  Optimization-tab operation runs.
- **Network** — lists Windows adapters (type, status, speed, IPv4); renewing
  the IP address (`ipconfig /renew`) and clearing the ARP cache (`netsh`)
  require administrator. Conservative on purpose: no TCP/registry tweaks, no
  winsock reset.

---

## What this app does NOT do (on purpose)

- **No kernel driver.** ASC installs two minifilters that intercept file and
  registry I/O. None of that here.
- **No resident service.** Nothing runs in the background.
- **No silent install.** No `Auto` services without warning.
- **No registry cleaner.** Risk out of proportion to the gain; left out.
- **No cross-selling.** Does not push other products.

---

## Structure

```
Systenclin/
├── Core/
│   ├── — cleanup (v0.1)
│   ├── ScanCategory.cs        file categories, risk, explanation
│   ├── CleanableItem.cs       one file item
│   ├── ScanTargets.cs         targets + safety guard
│   ├── DirectoryMeasurer.cs   measures a tree without following links
│   ├── Scanner.cs             file scan
│   ├── Cleaner.cs             cleanup + send to Recycle Bin
│   ├── FileDeleter.cs         permanent removal of cache folders
│   ├── CleanLog.cs            history in JSON
│   ├── — privacy (v0.2)
│   ├── BrowserProfile.cs      browser profile discovery
│   ├── PrivacyItem.cs         one privacy item, with preview
│   ├── PrivacyScanner.cs      reads the databases (SQLite)
│   ├── PrivacyCleaner.cs      cleanup with backup and restore
│   ├── SharedFileReader.cs    reading locked files
│   ├── — optimization and tools (v0.3+)
│   ├── StartupManager.cs      what starts with Windows
│   ├── MemoryOptimizer.cs     working set trim
│   ├── DnsFlusher.cs          flushes the DNS cache
│   ├── NetworkInfo.cs         adapters and network actions
│   ├── BootTimeReader.cs      boot time from Windows events
│   ├── ProcessManager.cs      processes by memory + kill
│   ├── SystemMonitor.cs       live CPU/RAM
│   ├── RecycleBin.cs          query and empty the Recycle Bin
│   ├── DiskOptimizer.cs       TRIM/defrag with media detection
│   ├── RestorePoint.cs        restore point
│   ├── DuplicateFinder.cs     duplicates by hash
│   ├── LargeFileFinder.cs     large files
│   ├── FolderAnalyzer.cs      what takes up space, folder by folder
│   ├── ShortcutFixer.cs       broken shortcuts
│   ├── AppUpdater.cs          updates via winget
│   └── AppUninstaller.cs      uninstall via winget
├── MainWindow.xaml            eight-tab interface
└── app.manifest               asInvoker (no admin by default)

Testes/Systenclin.Testes/      the 62 checks (compiles Core directly)
dist/                          published Systenclin.exe
```

## Next step

**Next:** fix the Applications tab update list — winget finds 27 updates in the
terminal, but the app shows none. Further increments as prioritized.
