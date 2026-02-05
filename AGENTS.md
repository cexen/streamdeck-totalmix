# AGENTS.md

## Project Overview

This repo is a fork of `shells-dw/streamdeck-totalmix` with Stream Deck+ dial support.
Target platform is Windows. The plugin is a .NET Framework app that ships as a
`.streamDeckPlugin` bundle.

Fork and branch:

- Fork: `https://github.com/cexen/streamdeck-totalmix/tree/feature/streamdeckplus-dial`
- Work branch: `feature/streamdeckplus-dial` (in the fork)
- Upstream: `shells-dw/streamdeck-totalmix`
- Related upstream issue: `https://github.com/shells-dw/streamdeck-totalmix/issues/53`

## Quick Start

1. Install Visual Studio 2026 with .NET desktop development.
   - One-shot install (Visual Studio + workload):
     - `winget install -e --id Microsoft.VisualStudio.Community --override "--passive --add Microsoft.VisualStudio.Workload.ManagedDesktop --includeRecommended --norestart"`
   - If Visual Studio is already installed without .NET desktop development, add it:
     - `$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`
     - `& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe" modify --installPath "$vs" --add Microsoft.VisualStudio.Workload.ManagedDesktop --includeRecommended --passive --norestart`
     - If the CLI workflow fails, use the Visual Studio Installer UI to add **.NET desktop development**.
2. Build with MSBuild (Debug or Release):
   - Debug:
     - `$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`
     - `& "$vs\MSBuild\Current\Bin\MSBuild.exe" /t:Build /p:Configuration=Debug`
   - Release:
     - `$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`
     - `& "$vs\MSBuild\Current\Bin\MSBuild.exe" /t:Build /p:Configuration=Release`

If the build fails while copying `de.shells.totalmix.exe`, Stream Deck is locking the file.
Stop these processes and retry:
`Stop-Process -Name StreamDeck -Force -ErrorAction SilentlyContinue; Stop-Process -Name streamdeck-totalmix -Force -ErrorAction SilentlyContinue`

## Run / Debug

Stream Deck loads plugins from:
`%APPDATA%\Elgato\StreamDeck\Plugins\de.shells.totalmix.sdPlugin`

Typical debug flow:

1. Close Stream Deck.
2. Copy `bin\Debug\de.shells.totalmix.sdPlugin` over the plugin folder.
3. Reopen Stream Deck.

## Release

Manifest Version must be numeric and is expected to use 4 parts. This fork is
based on upstream `3.3.5`, so we use `3.3.5.1`.

Steps:

1. Update `manifest.json` Version (e.g., `3.3.5.1`).
2. Build Release (see Quick Start).
3. Recreate the bundle:
   - `Compress-Archive -Path "bin\Release\de.shells.totalmix.sdPlugin" -DestinationPath "Release\de.shells.totalmix.streamDeckPlugin" -Force`

GitHub Release:

1. Install GitHub CLI: `winget install -e --id GitHub.cli`
2. Login: `gh auth login`
3. Create release with asset:
   - `gh release create v3.3.5.1 "Release/de.shells.totalmix.streamDeckPlugin" -t "v3.3.5.1" -F notes.md`

## Working With GitHub

When checking the upstream issue from a terminal/agent session, prefer GitHub CLI:

- `gh issue view 53 -R shells-dw/streamdeck-totalmix --comments`

## Non-Obvious Implementation Notes

Dial implementation lives in `OscDial.cs` and uses the Stream Deck+ Encoder API.

- Feedback layout file: `layouts/osc-dial.json`
  - `SetFeedbackLayoutAsync` must be called with the **layout file path**.
  - Stream Deck sometimes ignores the initial layout; `EnsureFeedbackLayout()` re-applies it.
- Dial target is selectable:
  - `DialFunction`: `volume` or `pan`
  - Dial press resets to default: 0 dB (volume) or center pan.
- Touch action is selectable:
  - `TouchAction`: `mute`, `solo`, or `none`
  - Solo is **not supported on Output** channels; touch is ignored and the “S” indicator is suppressed.
- Display prefers OSC `*Val` strings when available; otherwise it computes:
  - Volume -> dB string
  - Pan -> `L/R` or `C`
  - Local smoothing uses `LocalValueMaxMs=1000` and `LocalValueEpsilon` to reduce flicker.

## Validation

Run at least one MSBuild configuration (Debug for dev, Release for shipping) after
code changes and before committing or releasing.
