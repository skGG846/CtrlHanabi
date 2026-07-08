# CtrlHanabi

**English** | [日本語](./README.ja.md)

A Windows system tray utility built with WPF and .NET 10 that launches fireworks on your desktop when you repeatedly press the `Ctrl` key.
It renders a transparent overlay to display beautiful animations from rocket ascent to colorful bursts and afterglow.

![CtrlHanabi Demo](./CtrlHanabi_v1.0.4.gif)

## Features

- **Regular Firework Launch**: Double-tap the `Ctrl` key to launch a firework at the current mouse cursor position.
- **Starmine Sequence**: Triple-tap the `Ctrl` key to trigger a spectacular starmine firework show.
- **Cursor Tracking**: The launch location dynamically follows the coordinates of your mouse cursor.
- **System Tray Residency**: The application runs efficiently in the background, accessible from the system tray.
- **Windows Startup Integration**: Easily toggle auto-start on Windows login from the system tray menu.
- **Customizable Settings**: Fine-tune trigger timing thresholds, particle physics, and animation properties via a configuration file.
- **Multi-Monitor Support**: Configure which display is used for rendering the starmine sequence.

## Firework Animations & Variations

- Mortar tube visuals and launch smoke effects
- 3D-style spherical particle propagation
- Realistic afterglow and bloom (glow) effects
- Randomized, vibrant color combinations
- **Burst Types (Randomly generated)**:
  - Chrysanthemum (菊)
  - Peony (牡丹)
  - Kamurogiku (冠菊)
- **Ascent Trail Variations**:
  - Standard rocket ascent
  - Silver-dragon style tail
  - Mid-flight small-flower bloom

## System Requirements

- **OS**: Windows x64 (Windows 10 / 11)
- **Framework/Runtime**: .NET 10 (WPF enabled)

## Usage

1. Launch `CtrlHanabi.exe`.
2. The application will start running in the system tray.
3. Quickly press the `Ctrl` key **twice** anywhere on the desktop to launch a regular firework.
4. Quickly press the `Ctrl` key **three times** to launch a starmine sequence.

### How to Exit
- Right-click the tray icon and select **[Exit]**.
- Alternatively, press the `Ctrl` key quickly **five times** to bring up the exit confirmation dialog.

## System Tray Menu

- **Run at Windows startup**: Automatically register the app to registry path `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Launch starmine every hour**: When enabled, automatically launches a starmine sequence from the center of the screen at `59:30` every hour.
- **Reset settings**: Restores the configuration file to default settings.
- **Exit**: Closes the application.

## Build & Run

### Run in Development
```powershell
dotnet run
```

### Build the Project
```powershell
dotnet build
```

## Configuration & Setup

### Settings File
User settings are stored in JSON format at the following path:

- **Path**: `%LocalAppData%\CtrlHanabi\settings.json`

#### Configuration Example
```json
{
  "DoubleTapThresholdMs": 320,
  "CooldownMs": 500,
  "ParticleCount": 90,
  "ExplosionRadius": 110,
  "HourlyStarmineEnabled": false,
  "StarmineLaneLeftEnabled": true,
  "StarmineLaneCenterEnabled": true,
  "StarmineLaneRightEnabled": true,
  "StarmineDisplayIndex": 1,
  "UiLanguage": null
}
```

#### Field Descriptions
- `DoubleTapThresholdMs`: The maximum interval (in milliseconds) between keypresses to detect double/triple taps.
- `CooldownMs`: Cooldown period (in milliseconds) to prevent consecutive spamming.
- `ParticleCount`: Base number of sparks/particles generated per explosion.
- `ExplosionRadius`: Base radius of the firework explosion.
- `HourlyStarmineEnabled`: Toggles the hourly starmine show at `59:30`.
- `StarmineLaneLeftEnabled` / `StarmineLaneCenterEnabled` / `StarmineLaneRightEnabled`: Toggles individual launch lanes (Left, Center, Right) for the starmine show.
- `StarmineDisplayIndex`: 1-based index of the display monitor to render the starmine sequence (out-of-range values fallback to `1`, the primary display).
- `UiLanguage`: System tray menu UI language. Set to `null` or `"auto"` for automatic detection from Windows. Explicitly supports `"ja"` or `"en"`.

*Note: There is no dedicated configuration GUI. Please modify `settings.json` directly to change settings.*

### Runtime Control (Environment Variables)
You can configure the following environment variables to adjust rendering behaviors and log levels.

#### Graphics & Physics Control
If unset, these values default to `1` (enabled). Set to `0` to disable/fallback.

- `CTRLHANABI_D3D11`: Set to `0` to disable the Direct3D 11 particle renderer (falls back to software rendering).
- `CTRLHANABI_GPU_PHYSICS`: Set to `0` to disable GPU-based physics processing (falls back to CPU physics processing).

#### Logging Configuration
Direct3D 11 runtime logs are saved to `%LocalAppData%\CtrlHanabi\d3d11.log`.

- `CTRLHANABI_LOG`: Global log toggle. Set to `1` to enable, `0` to disable.
- `CTRLHANABI_D3D11_LOG`: Compatibility switch. Evaluated only when `CTRLHANABI_LOG` is unset. Set to `1` to enable.

**Priority Order**:
1. If `CTRLHANABI_LOG=0`, logging is always disabled.
2. If `CTRLHANABI_LOG=1`, logging is enabled.
3. If `CTRLHANABI_LOG` is unset, the setting of `CTRLHANABI_D3D11_LOG` is used.

## Technical Details

- **Tech Stack**: C# / WPF (.NET 10), Direct3D 11 rendering integration
- **Keyboard Hook**: Uses low-level Windows keyboard hooks combined with polling for robust keypress detection.
- **Dependencies**: Zero external NuGet packages or third-party DLL dependencies. See [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) for details.

## Notes & Limitations

- **Platform Restriction**: Due to keyboard input hook requirements, only Windows x64 environments are officially supported (detection might be unstable on x86).
- **User Account Control (UAC)**: The app runs with standard user permissions. Windows privilege isolation prevents the app from capturing `Ctrl` inputs while an elevated window (e.g., Task Manager) is active. To resolve this, run CtrlHanabi as Administrator or install it as a signed `uiAccess` application in a trusted system folder.

## License

This project is licensed under the [MIT License](./LICENSE).
