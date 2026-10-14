# Wyvern

A keyboard launcher for Windows. Hit the hotkey, type a few letters, press Enter.

I wanted a fast launcher on Windows that keeps everything on my own machine, so I built one. Nothing you type, launch or copy leaves your PC unless you set up sync yourself, and then it's encrypted before it goes anywhere.

## Screenshots

Coming soon.

## What it does

- Finds and launches installed apps, plus files and folders in the places you pick
- Quicklinks (URLs or paths with a `{query}` placeholder) and text snippets with keywords
- Snippet auto-expand: type a keyword anywhere and it's replaced with the snippet (off by default)
- Script commands: drop a .cmd, .bat, .ps1, .py or .js file in the scripts folder and it shows up as a command
- Clipboard history with pinning, picture support and text search inside screenshots (uses the OCR built into Windows)
- Window snapping (halves, thirds, quarters) and a window switcher
- Emoji picker
- System stuff: lock, sleep, restart, volume, media keys, empty recycle bin, light/dark mode
- Optional encrypted sync between your PCs over SFTP to a server you own
- Optional updates from your own server (off unless you build with an update channel, see `installer/new-update-key.ps1`)

## Requirements

- Windows 10 (2004 or later) or Windows 11
- .NET 10 SDK to build, .NET 10 Desktop Runtime to run

## Build and run

```
dotnet build Wyvern.slnx
dotnet test Wyvern.slnx
dotnet run --project src/Wyvern.App
```

Wyvern starts in the tray with no window. Press **Alt+Space** to open the launcher. On first run it asks for a clipboard history hotkey too (it suggests Ctrl+Shift+V). You can change both in Settings.

Settings live in `%LOCALAPPDATA%\Wyvern\settings.json`. The "Open Settings File" command opens it.

## Installing

To build a setup program you need Inno Setup 6:

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

That puts `WyvernSetup-<version>.exe` in `dist\`. It installs per user, no admin needed. Or just publish it to a folder:

```
dotnet publish src/Wyvern.App -c Release -o "$env:LOCALAPPDATA\Programs\Wyvern"
```

## Publishing updates

Release signing runs on Ubuntu. Its P-256 private key stays in
`~/.local/share/wyvern-release/update-signing-key.pem` (directory mode 700, key mode 600),
outside the HTTP download directory. The server needs Python 3 and `cryptography`.
Initialize once; rerunning keeps the existing key:

```
powershell -ExecutionPolicy Bypass -File installer\new-update-key.ps1 -Server ubuntu -Url http://100.110.71.108:8795/wyvern/
```

Build after initializing so the app embeds the public key and feed address, then publish:

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
powershell -ExecutionPolicy Bypass -File installer\publish-update.ps1 -Server ubuntu
```

Publishers write `/srv/app-updates/wyvern/` through `~/app-updates/wyvern/`. The existing private HTTP endpoint remains compatible. Its original Beacon
feed remains available at the root. PCs using the old signing key need one manual Wyvern
install before future Wyvern updates can be verified. Keep the private signing directory
in protected server backups; losing this key would require another manual upgrade.

Check the signer with `python3 installer/server-signing.py check`. Regenerate the eight-size
Windows icon from `installer/wyvern-icon.png` with `installer/make-icon.ps1`.

## License

MIT, see [LICENSE](LICENSE).

See [Ubuntu release storage and publishing](docs/UPDATE-STORAGE.md).
