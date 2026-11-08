# FlashInput

A tiny Windows system tray app that makes cheap generic USB gamepads (the ones built on a **DragonRise** chip) show up as a normal **Xbox 360 controller**, so games that only understand XInput just work.

No window, no config screens. It lives in the tray, you pick your pad, you toggle it on.

Made by [Tsukuridasu Studio](https://github.com/Tsukuridasu-Studio) and released under the MIT license.

## Why

Lots of budget controllers show up in Windows as "Generic USB Joystick". Plenty of modern games ignore those and only accept XInput pads. FlashInput reads the generic pad and mirrors it onto a virtual Xbox 360 controller using the ViGEm driver.

It was tested on a generic DragonRise USB gamepad, including in *Wuthering Waves*.

## Requirements

- Windows 10 or 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- The **ViGEmBus** driver ([github.com/nefarius/ViGEmBus](https://github.com/nefarius/ViGEmBus)). Check that project for its current status and installer.

## Download

Grab the latest build from the [Releases page](https://github.com/Tsukuridasu-Studio/FlashInput/releases), or build it yourself (see below).

## Usage

1. Plug in your controller.
2. Run `FlashInput.exe`. A bolt icon appears in the system tray (it may be hiding under the `^` overflow arrow).
3. Right-click the icon, then **Select Controller** and pick your pad. DragonRise devices are marked.
4. Click **Toggle Emulation** (or double-click the icon). The tooltip changes to "emulation ON".
5. Start your game **after** turning emulation on.
6. **Exit** from the tray menu shuts everything down and releases the virtual pad.

## Building from source

```bash
git clone https://github.com/Tsukuridasu-Studio/FlashInput.git
cd FlashInput
dotnet build src -c Release
```

The `Nefarius.ViGEm.Client` package is restored from NuGet automatically. You still need the ViGEmBus driver installed to run it.

## Default mapping

Generic pads differ, so the defaults are a best guess for a typical DragonRise layout.

| Physical pad | Virtual Xbox 360 |
|---|---|
| Left stick (X/Y axes) | Left stick |
| Right stick (Z/R axes) | Right stick |
| Buttons 1 to 4 | A, B, X, Y |
| Buttons 5, 6 | LB, RB |
| Buttons 7, 8 | LT, RT (digital, full pull) |
| Buttons 9, 10 | Back, Start |
| Buttons 11, 12 | Left / right stick click |
| Hat switch | D-pad |

If your pad is laid out differently, edit `ButtonMap` at the top of `ControllerBridge.cs` and rebuild. Some pads use the U/V axes for the right stick, so swap those in `Translate` if yours does.

## Troubleshooting

- **Error balloon when toggling:** the ViGEmBus driver probably isn't installed.
- **No controllers listed:** make sure the pad is plugged in and shows up in `joy.cpl`.
- **Game sees two controllers / double inputs:** the game is reading both the real and virtual pad. Hiding the physical one with [HidHide](https://github.com/nefarius/HidHide) is the usual fix.
- **D-pad acts like the left stick:** many generic pads have a mode button that switches this.

## Anti-cheat note

FlashInput uses a driver-level virtual device. Most games don't care, but a strict anti-cheat could. Use it at your own risk in competitive or anti-cheat protected games.

## How it works

A background thread polls the selected pad through the Windows joystick API and writes the state to a virtual Xbox 360 pad through `Nefarius.ViGEm.Client`. Stopping the emulation joins the thread, unplugs the virtual pad, and frees the driver connection, so nothing is left behind.

## Contributing

Issues and pull requests are welcome, especially button maps for other generic pads.

## License

MIT, see [LICENSE](LICENSE).