# Focus Study

A distraction-free study app for Windows. You set a session length; the PC then locks into a fullscreen
study space with a browser, your study folder, notes, document viewers, and Python / MATLAB coding
(your installed MATLAB, or free GNU Octave).

**Website:** https://focus.duskmoonstudio.com/ (source in `docs/`)

## Download

Grab the exe for your PC from the **Releases** page (or the latest **Actions** build):

| File | For |
|---|---|
| `FocusStudy.exe` | Most PCs (Intel / AMD 64-bit) |
| `FocusStudy-arm64.exe` | ARM laptops (Snapdragon, Surface Pro X, Copilot+ PCs) |

Each is a single file: no install and no .NET needed. Run it, or try it first with `--demo`.
Not sure which you have? Settings → System → About → "System type".

Requirements: Windows 10/11 with the WebView2 runtime (built into Windows 11).
Optional: Python (python.org), and MATLAB or GNU Octave (octave.org/download, free).

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
bin\Release\net10.0-windows\FocusStudy.exe            # real session (locks the PC)
bin\Release\net10.0-windows\FocusStudy.exe --demo     # try it in a normal window, no lock

powershell -ExecutionPolicy Bypass -File publish.ps1  # -> dist\FocusStudy.exe (single file)
powershell -ExecutionPolicy Bypass -File publish.ps1 -Runtime win-arm64  # -> dist-arm64\FocusStudy.exe
```

Tip while developing: always use `--demo`, so a bug can't trap you in a locked session.
`--snap <folder>` renders every screen to PNG without any lock (handy for checking UI changes).

## Code map

| File | What it does |
|---|---|
| `SetupWindow.xaml(.cs)` | Start screen: duration, study folder, idle time, blocked sites |
| `SessionWindow.xaml(.cs)` | The locked session: browser tabs, Study Files, Code, Notes, exit steps, summary |
| `KeyboardLock.cs` | Low-level keyboard hook (blocks Win / Alt+Tab..., turns Alt+F4 into the exit steps) |
| `FocusMonitor.cs` | Page classification, idle/drift counting, focus score, `sessions.csv` |
| `CodeRunner.cs` | Runs Python, MATLAB (warm background server) or Octave; captures plots |
| `WebScripts.cs` | Script injected into pages: study-folder-only upload picker |
| `BlackoutWindow.cs` | Covers extra monitors |
| `Settings.cs` | `%AppData%\FocusStudy\settings.json` |
| `viewers/` | Office viewers (docx-preview, SheetJS, own `pptx.js`) |

## Contributing

Fork, change, and open a pull request. The GitHub Actions workflow builds the exe for every push;
pushing a tag such as `v1.1.0` publishes it as a Release. Licensed MIT (see `LICENSE`).

MATLAB: if MATLAB is installed, the MATLAB tab uses it (headless, no MATLAB window, your own licence).
A background MATLAB starts with the session so runs take a few seconds; it quits when the session ends,
or by itself within ~45 s if the app is closed. Without MATLAB, Octave is used. Force one with
`"MatlabEngine": "matlab" | "octave"` in settings.json. Activate MATLAB once normally before your first session.

## During a session

- No clock or countdown anywhere. The session ends on its own, then shows a summary.
- Blocked: Win key, Alt+Tab, Alt+Esc, Alt+Space, Ctrl+Esc, Ctrl+Shift+Esc, taskbar, other monitors.
- **Alt+F4** starts the exit steps: reason → typed pledge (no paste) → arithmetic → cool-down → unlock.
- **Ctrl+Alt+Del** always works (Windows reserves it). Task Manager is allowed to come to the front.
- Idle nudge after N minutes without input (paused while audio/video plays or code runs).
- Page checking: blocked domains can't load; pages whose titles look off-topic get a warning.
- Downloads go to `<study folder>\Downloads`; uploads use an in-app picker limited to the study folder.
  Any Windows file dialog a page still manages to open is closed automatically.
- Website notifications are always refused. Monitors plugged in mid-session are blacked out too.
- Signing out / shutting down Windows saves open code and notes and logs the session as ended early.

## Notes

- **📝 Notes** (top bar, or Ctrl+Shift+N) opens a notes panel beside every page: Browser, Study Files and Code.
- Notes autosave to `<study folder>\Notes\Session notes <date>.md`. **New** / **Open…** switch notes.
- **＋ Clip page** appends the current web page as a Markdown link, so sources are kept.

## Documents in Study Files

| Type | Viewer (bundled in `viewers/`, open source) |
|---|---|
| PDF, images, audio, video | WebView2 built-in |
| .docx | docx-preview (Apache-2.0) + JSZip (MIT) |
| .xlsx .xls .ods .csv | SheetJS (Apache-2.0); unsaved formulas show as `=…` |
| .pptx | `viewers/pptx.js`, this project's own renderer (MIT) on JSZip: text, bullets, shapes, pictures, tables, backgrounds; charts/SmartArt shown as labelled boxes |
| .doc .ppt .odt .rtf | Not supported: save as .docx / .pptx / PDF before the session |

Licences for the bundled libraries are in `viewers/lib/`. Viewing is read-only.

## Settings & logs

- `%AppData%\FocusStudy\settings.json` – blocked/trusted domains, keywords, cool-down seconds, interpreter paths.
- `%AppData%\FocusStudy\sessions.csv` – one row per session.
- `%AppData%\FocusStudy\errors.log` – crash details.

## Known limits

- Ctrl+Alt+Del → Task Manager / Sign out is an intentional emergency exit.
- Touchpad/touchscreen gestures handled by the Windows shell can't be blocked by an app; the app pulls focus back.
- Python/Octave code runs with your normal user rights. The folder limit applies to the app's UI, not to code you write.
- For an escape-proof setup, create a separate Windows account and set this app as its kiosk app (Assigned Access).
