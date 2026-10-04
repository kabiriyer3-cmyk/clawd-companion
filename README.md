# Clawd Companion

A living, 3D Clawd that hangs out on your Windows 11 desktop. Built with C# / WPF (.NET 8), styled after the OpenAI Codex app's pets (dark `#181818` surfaces, faint white ring borders, compact controls, Codex status colors).

## Build the .exe (GitHub Actions)

1. Create a GitHub repo and push the contents of this folder (keep `.github/workflows/build.yml`).
2. Open **Actions → Build Clawd Companion**. It runs on every push, or start it yourself with **Run workflow**.
3. When it finishes, download **ClawdCompanion-win-x64** (or `win-arm64`) from the run's **Artifacts**. You get one self-contained `ClawdCompanion.exe` that doesn't need .NET installed.
4. Push a tag like `v1.0.0` and the workflow also attaches both exes to a GitHub Release.

To build locally on Windows with the .NET 8 SDK:

```
dotnet publish ClawdCompanion/ClawdCompanion.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Playing with Clawd

| Do this | Clawd does |
| --- | --- |
| Drag the **key ring** | Hangs from the chain: a real triple pendulum (key ring, jump ring, body) that swings, twists and stretches |
| Fling and let go | Gets thrown, bonks off screen edges, rights itself mid-air and lands on its feet (or goes `x x` if it was hard) |
| Spin him a lot | Gets dizzy once he lands (spinning eyes, wobbling) |
| Click the body | Giggles. Poke too much and he gets grumpy and hops away |
| Double-click | Big hop |
| Drag the body | "grab my ring ↑" |
| Rub the cursor over him | Heart eyes, purring wiggle, floating pixel hearts |
| Leave him alone | Wanders, looks around, stretches, dances, twirls, taps a foot, follows a resting cursor, and eventually yawns and naps (`z`). Pet him while he sleeps and he smiles |
| Hover | A Codex-style tray shows his status plus Say hi / Pet / Nap / Hop / More |
| Right-click or tray icon | Menu: size, walk around, sit on windows, start with Windows, tuck away, quit |
| `Ctrl+Alt+C` | Wake or tuck away |

He stands on the taskbar and on top of app windows. Drag a window and he rides along. Cover his ledge or close the window and he falls.

## Project layout

```
ClawdCompanion/
  App.xaml(.cs)        startup, single instance, tray + Codex menu
  PetWindow.xaml(.cs)  transparent topmost overlay, input, status tray, particles
  Theme/Codex.xaml     Codex design tokens and control styles
  Sim/PetPhysics.cs    chain/pendulum, falling, landing, walking, squash and stretch
  Sim/PetBrain.cs      behaviours, moods, reactions, face and limb animation
  Render/ClawdRig.cs   3D rig: bendable legs and claws, swappable eye glyphs, rings
  Render/ObjLoader.cs  OBJ reader
  Core/DesktopWorld.cs monitors, taskbars, window tops
  Assets/clawd_rig.obj your model, triangulated with extra edge loops at hips and shoulders
  Assets/clawd_source.obj  your original Blender export
```

Settings and a small log are saved under `%AppData%\ClawdCompanion`.
