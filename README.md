<p align="center">
  <img src="assets/logo_no_bg.jpg" alt="emMDee" width="128" />
</p>

<h1 align="center">emMDee</h1>
<p align="center"><strong>A fast, native Markdown viewer for Windows — rendered beautifully with WebView2.</strong></p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-blue?logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET" />
  <img src="https://img.shields.io/github/license/foodak/emMDee" alt="License" />
</p>

---

## What is emMDee?

emMDee is a desktop Markdown previewer. You throw `.md` files at it and it renders them into clean, properly styled HTML — on the spot. It doesn't edit. It doesn't sync. It just previews, and it does that well.

Under the hood it uses [marked.js](https://github.com/markedjs/marked) for GitHub‑flavoured Markdown parsing and the Windows WebView2 control for display. The entire UI is native WPF, so it respects your system theme, snaps to your monitor, and stays out of the way.

![Screenshot](assets/emMDee.jpg)

## Features

- **Rich preview** — full GitHub‑flavoured Markdown via `marked.js`, with tables, task lists, syntax‑highlighted code blocks, and images.
- **Tabs** — open as many `.md` files as you need. Switch with the tab strip, Ctrl+Tab, or Ctrl+1‑9. Each tab remembers its scroll position.
- **Drag & drop** — fling a file anywhere onto the window to open it. Multi‑select in the open dialog is supported too.
- **Session restore** — open tabs, recent files, window size/position, and zoom level are saved to `%AppData%\emMDee\session.json` and restored when you relaunch.
- **Search** — Ctrl+F to find text in the current preview; Ctrl+Shift+F searches across all open tabs. Hit F3 to jump through matches.
- **Copy as Markdown** — select text in the preview, press Ctrl+Shift+C, and the *original* Markdown source lands on your clipboard. Plain Ctrl+C copies rich text (HTML) so pasting into Word or an email preserves formatting.
- **System‑aware theming** — follows your Windows light/dark preference automatically. The title bar, menus, and chrome all adapt.
- **Graceful fallback** — if WebView2 Runtime isn't installed (rare on Windows 11, possible on older Windows 10), the app shows a clear banner with a download link instead of crashing.

## Download

Grab the latest build from the [Releases](https://github.com/foodak/emMDee/releases) page.

Two flavours are provided for each release:

| Package | Description |
|---|---|
| `emMDee-win-x64.zip` | Self‑contained 64‑bit executable — no .NET install needed |
| `emMDee-win-arm64.zip` | Same, for ARM64 devices (Surface Pro X, etc.) |

The self‑contained build bundles the .NET runtime. All you need is **Windows 10 or later** with the [WebView2 Runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703) — which ships inbox on Windows 11 and on most up‑to‑date Windows 10 machines.

If you run into a "WebView2 not found" message, [download the Evergreen installer here](https://go.microsoft.com/fwlink/p/?LinkId=2124703).

## Build from source

You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

```powershell
git clone https://github.com/foodak/emMDee.git
cd emMDee
dotnet build src/emMDee.csproj
dotnet run --project src/emMDee.csproj
```

To produce a self‑contained release binary:

```powershell
dotnet publish src/emMDee.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
Compress-Archive -Path publish/win-x64/* -DestinationPath emMDee-win-x64.zip
```

Swap `win-x64` for `win-arm64` to target ARM64 hardware.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+O | Open file(s) |
| Ctrl+W | Close current tab |
| Ctrl+F | Find in current preview |
| Ctrl+Shift+F | Find across all open tabs |
| F3 | Next search match |
| Shift+F3 | Previous search match |
| Ctrl+C | Copy selection as rich text (HTML) |
| Ctrl+Shift+C | Copy selection as raw Markdown |
| Ctrl+Tab | Switch to next tab |
| Ctrl+1 … 9 | Jump to a specific tab |

## What's inside

```
emMDee.sln
└── src/
    ├── emMDee.csproj              # WPF project, net8.0‑windows
    ├── App.xaml / App.xaml.cs     # Startup, theme detection, colour switching
    ├── MainWindow.xaml / .cs      # Window shell — menus, tabs, search bar
    ├── Models/
    │   ├── TabItem.cs             # Per‑tab state (path, content, scroll pos)
    │   ├── SessionData.cs         # Serialised session DTO
    │   └── RecentFileItem.cs      # Recent‑file menu model
    ├── ViewModels/
    │   ├── MainViewModel.cs       # Commands, tab management, search logic
    │   ├── RelayCommand.cs        # Simple ICommand helper
    │   ├── ObservableObject.cs    # INotifyPropertyChanged base class
    │   └── Converters.cs          # Bool → Visibility converter
    ├── Views/
    │   └── MarkdownPreviewControl.xaml / .cs   # WebView2 host with fallback UI
    ├── Services/
    │   └── SessionManager.cs      # Reads/writes session.json
    └── wwwroot/
        ├── index.html             # Shell page — marked.js glue, search JS, copy handlers
        └── marked.min.js          # marked.js (GFM parser, v15‑ish)
```

## Known limitations

- **View‑only.** There's no editor pane. emMDee is a previewer, not an IDE.
- **Copy granularity.** Ctrl+Shift+C extracts Markdown at block level. For short inline selections the result may include the whole paragraph — that's by design; the source mapping works on token boundaries.
- **WebView2 prerequisite.** The runtime is bundled with Windows 11. On Windows 10 it's usually present, but if it's missing the app will show a download link.
- **File extensions.** Only `.md`, `.markdown`, `.mdown`, and `.mkd` are recognised in the open dialog. You can drag anything onto the window, though.

## License

[MIT](LICENSE) © 2026 foodak
