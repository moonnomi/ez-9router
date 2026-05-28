# ez-9router Native

Lightweight Windows tray companion for 9router. It avoids Electron and uses WinForms plus native global hotkeys.

## Features

- System tray menu.
- Global hotkeys for selected text, snip mode, and custom prompts.
- Settings dialog for endpoint, API key, model picker, prompts, hotkey recording, stealth mode, and semi-stealth snip.
- Sends selected text by temporarily copying the active selection.
- Sends snips as OpenAI-style `image_url` content to 9router.
- Hotkey fields record keybinds directly; focus a hotkey box and press the combo.
- Stealth mode uses borderless white answer windows with black text. Semi-stealth snip keeps that window open until you press `X` while hovering it.

## Build / Run

```powershell
dotnet run --project native/Ez9Router.Native/Ez9Router.Native.csproj
```

Default hotkeys:

- `Ctrl+Alt+1`: answer selected text
- `Ctrl+Alt+2`: snip mode
- `Ctrl+Alt+3`: custom prompt for selected text

Settings are stored in `%APPDATA%/ez-9router-native/settings.json`.