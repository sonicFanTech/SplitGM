<div align="center">

<img width="512" height="232" alt="SplitGM_SPLASH_v0 5 1 0" src="https://github.com/user-attachments/assets/38ecc482-9e25-46c3-8efd-028d02533596" />

# SplitGM-VM Decompiler

### Read-only GameMaker VM decompilation, resource exploration, extraction, relationship analysis, TEMP launching, and reconstructed GameMaker project export

[![Version](https://img.shields.io/badge/version-0.5.1.0-6f42c1?style=for-the-badge)](../../releases/latest)
[![Status](https://img.shields.io/badge/status-public%20beta-f0ad4e?style=for-the-badge)](#public-beta-and-reconstruction-status)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078d4?style=for-the-badge)](#system-requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?style=for-the-badge)](#building-from-source)
[![License](https://img.shields.io/badge/license-GPL--3.0-2ea44f?style=for-the-badge)](#license-and-source-code)

**[Download the latest release](../../releases/latest) · [View all releases](../../releases) · [Report a problem](../../issues)**

**[SplitGM Trailer / Showcase Video](https://www.youtube.com/watch?v=--CGDXPK9Jc)**

</div>

## Trailer / Showcase Video

https://www.youtube.com/watch?v=--CGDXPK9Jc

---

## SplitGM v0.5.1.0

SplitGM v0.5.1.0 is the largest SplitGM update so far.

It adds:

- a required animated startup launcher;
- five selectable startup-display modes;
- automatic UNDERTALE, DELTARUNE, and Generic GameMaker profiles;
- a responsive **Run Game from TEMP** workflow;
- detailed TEMP-run manifests and logs;
- a hidden-by-default experimental reconstructed `.yyp` exporter;
- safer room and project linking;
- automatic reconstructed-project repair;
- validation and quarantine of resources that cannot be represented safely;
- UMT-style background scheduling for large exports;
- real decoded-audio waveform previews;
- and a dedicated read-only UMT-style room viewer.

The selected game remains read-only. SplitGM does not patch or overwrite the original `data.win`, game executable, or installation directory.

The TEMP runner creates and may rewrite only an isolated temporary copy.

---

## About SplitGM

**SplitGM-VM Decompiler** is a Windows desktop application for inspecting games built with GameMaker's VM runtime.

It is designed for:

- game-format research;
- interoperability;
- preservation;
- debugging;
- educational inspection;
- resource recovery;
- code and relationship analysis;
- and creation of transparent GameMaker repair workspaces.

SplitGM can:

- open supported GameMaker data archives and Windows executables;
- reconstruct readable GML from GameMaker VM bytecode;
- display VM assembly beside reconstructed code;
- browse and preview resources;
- export recoverable assets and metadata;
- analyze relationships between code and resources;
- export an organized SplitGM extraction project;
- run a loaded game from an isolated TEMP copy;
- and generate an experimental reconstructed GameMaker `.yyp` project.

SplitGM is built around the GameMaker research and parsing systems used by UndertaleModTool:

- **UndertaleModLib / UndertaleIO** for GameMaker loading, resource models, format detection, textures, audio, and VM disassembly;
- **Underanalyzer** for GameMaker VM analysis and high-level GML reconstruction;
- **UMT-compatible texture and room behavior** for cached texture recovery and read-only room rendering.

SplitGM adds its own WPF interface, project workflows, viewers, relationship tools, extraction pipeline, reconstructed-project exporter, automatic repair system, TEMP-run workflow, diagnostics, and safety checks.

---

## Required animated launcher

SplitGM v0.5.1.0 must be opened through:

```text
SGMVMDLauncher.exe
```

The launcher:

- embeds all 412 JPEG frames from the trailer ending;
- does not require an external frame folder;
- preserves the original 24 FPS timing;
- decodes only the currently visible frame at the launcher display size;
- reads the startup mode selected in SplitGM Settings;
- starts the main application through a one-time current-user named-pipe authorization handshake;
- waits until the main SplitGM window reports that it is visible;
- and closes after startup finishes.

Opening this file directly is intentionally blocked:

```text
SplitGM-VM-Decompiler.exe
```

The main executable requires a live authorization session created by `SGMVMDLauncher.exe`. A copied command-line argument is not enough.

The old static WPF splash has been removed from the main executable.

### Startup display modes

Change the startup mode through:

```text
Tools → Settings → Startup
```

| Mode | Behavior |
|---|---|
| **Normal** | Plays all 412 frames. |
| **First Half** | Plays the logo-assembly and SplitGM-title section only. |
| **Second Half** | Plays the animated splash-image section only. |
| **First Half Static** | Shows the completed SplitGM logo and title frame. |
| **Second Half Static** | Shows the completed splash image and all text. |

Static modes remain visible for at least two seconds and then stay visible while SplitGM finishes opening.

Space, Enter, or Escape can skip the remaining animation or minimum static hold. They do not bypass launcher authorization.

The launcher cannot be disabled.

---

## Game profiles

SplitGM v0.5.1.0 includes these profile choices:

- **Auto Detect**
- **Generic GameMaker Game**
- **UNDERTALE**
- **DELTARUNE**

The default is **Auto Detect**.

Profile detection uses multiple signals, including:

- input paths and containing directories;
- internal game, display, and runner names;
- recognized room, object, script, sprite, and code names;
- and combinations of known resources.

The current effective profile, selection method, confidence, and detection reasons are shown in SplitGM.

A manual override is available for renamed, heavily modified, or incorrectly detected games.

Profiles guide conservative behavior such as:

- runner executable discovery;
- TEMP sidecar discovery;
- game-specific compatibility handling;
- and reconstructed-project diagnostics.

The **Generic GameMaker Game** profile avoids UNDERTALE- and DELTARUNE-specific assumptions.

---

## Run Game from TEMP

Use:

```text
Tools → Run Game from TEMP
```

SplitGM creates a unique run directory under:

```text
%TEMP%\SplitGM-VM-Decompiler\GameRuns\
```

The TEMP runner:

- keeps the original game files unchanged;
- creates a temporary `data.win`;
- copies required audio groups and runtime sidecars;
- searches for a compatible original GameMaker runner;
- allows manual runner selection when detection is ambiguous;
- starts the original runner with `-game <temporary-data.win>`;
- writes GameMaker debug output into the TEMP run;
- keeps SplitGM responsive during preparation and while the game is running;
- tracks the launched process in the background;
- and writes a detailed log and `TempRunManifest.json`.

Additional Tools commands can:

- open the current TEMP run folder;
- and clean old inactive TEMP runs.

### Steam-enabled games

When a loaded game contains Steam metadata, SplitGM records the detected Steam App ID and its source in the TEMP-run manifest.

To prevent Steam from replacing the custom TEMP launch with the normal installed game, SplitGM may write the temporary data copy with GameMaker's Steam flags and Steam App ID disabled.

This changes only the TEMP copy.

As a result, Steam-dependent features such as these may be unavailable during a TEMP run:

- Steam Overlay;
- achievements;
- rich presence;
- Steam networking;
- and other Steam API services.

SplitGM does not bypass ownership, DRM, authentication, or access controls.

---

## Experimental reconstructed `.yyp` project export

The reconstructed GameMaker project exporter is disabled by default.

Enable it through:

```text
Tools → Settings → Experimental
```

Turn on:

```text
Enable Decompile to .yyp Project
```

When disabled, the command is completely absent from the Tools menu.

When enabled, use:

```text
Tools → Decompile to Reconstructed .yyp Project...
```

The first use displays an experimental-feature warning.

The output is intended to be:

- openable and editable in a compatible GameMaker IDE;
- transparent about what was recovered, inferred, repaired, omitted, or lost;
- useful as a research, preservation, inspection, and repair workspace;
- and safer than silently generating invalid project data.

It is not guaranteed to:

- match the original developer project;
- compile immediately;
- run correctly;
- preserve every original resource relationship;
- or reproduce the original game perfectly.

---

## Automatic reconstructed-project repair

After initial `.yyp` generation, SplitGM runs a conservative, report-first repair pass.

The repair system:

- preserves the untouched pre-repair project;
- records every change with an ID, category, confidence, evidence, before/after values, and manual steps;
- allocates globally unique case-insensitive GameMaker asset names;
- updates exact GML and JSON references after known renames;
- registers recoverable `GlobalInit` and `GlobalScript` code as modern script resources;
- repairs placeholder and duplicate enum names;
- removes duplicate identifiers from compatible recovered `var` declarations;
- adds safe optional parameters when recovered code references higher `argumentN` slots;
- normalizes `.yy`, `.yyp`, folder, resource-order, reference, and target-version fields;
- creates clearly marked structural placeholders for required missing code files;
- checks sprite dimensions, frames, bounds, origins, sequences, playback, and collision defaults;
- identifies extension and unresolved-function candidates;
- and runs a static compile preflight.

Every repaired project can include:

```text
SplitGM-Repair-Report.txt
SplitGM-Repair-Report.json
SplitGM-Unresolved-Functions.txt
__SplitGM_OriginalDecompilerOutput\
```

Static preflight is not a replacement for compiling the project in GameMaker.

### Room and project-linking validation

v0.5.1.0 includes additional checks for:

- project resource-list types;
- room-order entries;
- room layer types;
- room instance object references;
- and `instanceCreationOrder` entries.

The repair pass avoids rewriting a room-instance creation-order name into the room's own resource name. That incorrect rewrite caused GameMaker errors such as:

```text
Resource '<room name>' does not match the list type expected.
```

The exporter now validates that creation-order entries refer to actual `GMRInstance` entries in the same room.

Resources that cannot be represented safely can be excluded from unsafe project lists and preserved as inspectable fallback data.

---

## Reconstructed project output

A reconstructed project can contain:

```text
GameName_Reconstructed\
├── GameName.yyp
├── GameName.resource_order
├── GameName.splitgmproj
├── README-SplitGM-Reconstructed-Project.txt
├── SplitGM-Reconstruction-Report.txt
├── SplitGM-Reconstruction-Report.json
├── SplitGM-Reconstruction-Validation.txt
├── SplitGM-Reconstruction-Validation.json
├── SplitGM-Repair-Report.txt
├── SplitGM-Repair-Report.json
├── SplitGM-Unresolved-Functions.txt
├── folders\
├── scripts\
├── objects\
├── rooms\
├── sprites\
├── sounds\
├── paths\
├── audiogroups\
├── __SplitGM_Metadata\
├── __SplitGM_Unrepresented\
└── __SplitGM_OriginalDecompilerOutput\
```

Not every folder or report is produced for every game.

### `.splitgmproj` intermediate format

Every reconstructed project includes a versioned `.splitgmproj` document.

It stores:

- source and detected GameMaker information;
- the selected game profile;
- target reconstruction information;
- stable resource IDs;
- original and reconstructed names;
- resource statuses;
- recovered relationships;
- generated files;
- warnings and errors;
- and validation state.

This gives future SplitGM builds a stable format for repair and migration work.

### Transparent fallback output

SplitGM preserves data that cannot be added safely to the `.yyp` under folders such as:

```text
__SplitGM_Metadata\
__SplitGM_Unrepresented\
```

These can contain:

- raw recovered metadata;
- previews;
- raw code;
- VM assembly;
- unsupported resources;
- fallback JSON or text;
- and explanations of why a resource was omitted.

---

## Main features

### Resource Explorer and code inspection

- Paged and searchable Resource Explorer.
- Reconstructed GML display.
- VM assembly display.
- AvalonEdit syntax highlighting.
- Line numbers and word wrap.
- Code search.
- Connected-code navigation.
- Large-document safeguards and bounded caches.

### Resource viewers

- Sprite frame preview and playback.
- Object sprite and event/code mapping.
- Dedicated read-only room viewer.
- Background and tileset preview.
- Font atlas preview.
- Embedded texture-page preview.
- Audio playback.
- Real decoded-sample audio waveform display.
- Resource metadata and property tables.

### Audio

- WAV playback and decoding.
- OGG Vorbis playback and decoding.
- MP3 playback and decoding.
- Waveform duration, sample rate, channels, peak, RMS, decoder, and scan status.
- SVG and JSON waveform sidecars during compatible exports.
- Audio failures do not block original audio export.

### Read-only room viewer

- Nearest-neighbor rendering.
- Scrollable room surface.
- Zoom, fit-to-window, and 100% controls.
- Ctrl+mouse-wheel zoom.
- Optional grid.
- Layer, instance, and tile tables.
- GMS1 and GMS2 room support.
- GMS2 color layers, tile transforms, offsets, stretching, and tiling.
- No mutation, drag editing, or save-back behavior.

### Relationship analysis

- Callers and callees.
- Global-variable usage.
- Static room transitions.
- Object inheritance.
- Room/object placement relationships.
- Named asset references.
- Heuristic unused-resource candidates.
- Navigation from relationships to connected code.

### Export workflows

- Selected-resource export.
- Complete resource-category export.
- Complete audio-group export.
- Organized SplitGM extraction-project export.
- Experimental reconstructed GameMaker project export.
- Cancellable progress windows.
- Deterministic output ordering.
- Per-resource error continuation.
- Detailed activity, reconstruction, repair, validation, and crash logs.

---

## UMT-aligned loading and performance

The open/decompile path is centralized through `UmtNativePipeline`.

It uses:

- `UndertaleIO.Read` for loading;
- shared `GlobalDecompileContext`;
- `DecompileContext.DecompileToString()` for high-level GML recovery;
- `UndertaleCode.Disassemble()` for VM assembly;
- bounded parallel decompilation;
- deterministic output merging;
- shared and short-lived `TextureWorker` caches.

Large reconstructed exports run on a worker task.

A throttled UI progress pump coalesces status, resource rows, previews, and logs instead of flooding the WPF dispatcher with one update for every exported item.

Sprite export is grouped around connected texture pages with bounded parallelism.

These changes keep large exports more responsive and reduce texture-cache contention.

---

## Supported input

SplitGM accepts:

- `data.win`;
- `.win`;
- `.unx`;
- `.ios`;
- `.droid`;
- `.android`;
- `.game`;
- and Windows GameMaker executables with a neighboring or validated embedded data archive.

VM games can provide reconstructed GML and VM assembly.

YYC games can still provide recoverable resources and metadata, but they do not contain normal GameMaker VM bytecode for SplitGM to decompile.

Packed, encrypted, damaged, unsupported, or platform-specific layouts may not load.

---

## System requirements

### Precompiled framework-dependent build

- Windows x64
- .NET 10 Desktop Runtime

### Precompiled self-contained build

- Windows x64
- No separate .NET Desktop Runtime installation required
- Larger download and extracted size

### Source build

- Windows x64
- Visual Studio 2026
- .NET desktop development workload
- .NET 10 SDK
- PowerShell 5.1 or newer
- Internet access when dependencies or NuGet packages must be restored

---

## Installing a precompiled release

1. Download the desired Windows x64 release ZIP.
2. Extract the complete ZIP into its own folder.
3. Keep all EXEs, DLLs, resources, license files, and notices together.
4. Start:

   ```text
   SplitGM\SGMVMDLauncher.exe
   ```

5. Do not start `SplitGM-VM-Decompiler.exe` directly.

Do not copy only one executable out of the release folder.

SplitGM does not need to be placed inside a game directory.

---

## Basic usage

### Open and inspect a game

1. Start SplitGM through `SGMVMDLauncher.exe`.
2. Select **File → Open game...**
3. Choose a supported GameMaker data file or executable.
4. Wait for loading and profile detection to finish.
5. Browse resources through the Resource Explorer.
6. Open code entries to inspect reconstructed GML or VM assembly.
7. Use relationship tools to navigate connected resources and code.
8. Use the Export menu to recover selected resources or complete categories.

### Run the loaded game from TEMP

1. Load a supported Windows GameMaker game.
2. Select **Tools → Run Game from TEMP**.
3. Confirm or select the original runner executable when requested.
4. Wait for TEMP preparation and launch.
5. Review the Activity Log or TEMP-run log when needed.
6. Use **Tools → Open Current TEMP Run Folder** to inspect the run manifest.

### Export an organized SplitGM extraction project

Use the extraction-project export when the goal is to inspect code, metadata, indexes, assets, relationships, and errors outside GameMaker.

This is separate from the reconstructed `.yyp` feature.

### Decompile to a reconstructed GameMaker project

1. Open **Tools → Settings → Experimental**.
2. Enable **Decompile to .yyp Project**.
3. Load a supported GameMaker VM game.
4. Select **Tools → Decompile to Reconstructed .yyp Project...**
5. Choose a parent output directory.
6. Review queued resources and progress.
7. Read the reconstruction, validation, and repair reports.
8. Open the generated `.yyp` in a compatible GameMaker IDE.
9. Treat GameMaker's own parser/compiler output as the final authority.

Do not overwrite an unrelated project directory.

---

## Public beta and reconstruction status

SplitGM v0.5.1.0 remains a public beta.

The reconstructed-project exporter is experimental and disabled by default.

A compiled GameMaker game normally does not contain every part of its original project. Information that may be missing, renamed, optimized, inferred, or impossible to recover includes:

- comments;
- formatting;
- original local-variable names;
- macros;
- original enum names;
- exact function signatures;
- original optional arguments;
- original folder organization;
- IDE-only metadata;
- import settings;
- original sprite source canvases;
- extension source;
- native code;
- platform-specific project data;
- and code removed or changed by optimization.

Common remaining reconstructed-project problems can include:

- unresolved built-ins or extension functions;
- behavior lost from missing source data;
- inaccurate room layers;
- dynamic resource lookups that static analysis cannot recover;
- sprite padding, origin, mask, or source-canvas differences;
- shader and extension incompatibilities;
- platform configuration differences;
- and runner-version-specific project fields.

An openable project is not necessarily a compilable project.

A compilable project is not necessarily an accurate recreation of the original game.

---

## Performance notes

Large games can contain thousands of code entries, sprites, frames, rooms, sounds, and texture-page items.

Performance depends on:

- CPU core count;
- available memory;
- storage speed;
- antivirus and Windows Defender scanning;
- cloud synchronization;
- texture-page size;
- resource count;
- and output file count.

For best results:

- use a local SSD;
- avoid network, external, cloud-synchronized, or compressed output folders;
- keep sufficient free disk space;
- close unnecessary memory-heavy software;
- and let one large operation finish before starting another.

Do not disable security software solely to improve export performance.

---

## Troubleshooting

### SplitGM does not start

Confirm that:

- the complete release was extracted;
- `SGMVMDLauncher.exe` is being used;
- all release files remain together;
- the correct Windows x64 package was downloaded;
- and the .NET 10 Desktop Runtime is installed for a framework-dependent package.

Check the `Logs` folder for a crash report.

### The main EXE says the launcher is required

This is expected.

Start:

```text
SGMVMDLauncher.exe
```

Do not start:

```text
SplitGM-VM-Decompiler.exe
```

### Run Game from TEMP cannot find a runner

Use the file-selection dialog to choose the original Windows GameMaker game executable.

The runner normally needs to remain in its original installation directory so its runtime DLLs and other dependencies can be found.

### A TEMP-run game starts without Steam features

This can be expected.

For Steam-enabled games, SplitGM may disable Steam metadata only in the temporary data copy so the original runner accepts the custom TEMP launch instead of handing control back to Steam.

The original game installation is not changed.

### A game will not open

The input may be:

- unsupported;
- YYC-only;
- packed or encrypted;
- damaged;
- missing a neighboring data archive;
- or using an unsupported platform or GameMaker generation.

Include the activity log and detected format information in a bug report.

Do not upload copyrighted game files.

### The reconstructed `.yyp` command is missing

Open:

```text
Tools → Settings → Experimental
```

Enable:

```text
Enable Decompile to .yyp Project
```

The command is hidden by default.

### The `.yyp` opens but does not compile

Read:

- GameMaker's compiler errors;
- `SplitGM-Reconstruction-Report.txt`;
- `SplitGM-Reconstruction-Validation.txt`;
- `SplitGM-Repair-Report.txt`;
- `SplitGM-Unresolved-Functions.txt`;
- and files under `__SplitGM_Unrepresented`.

Manual repair may still be required.

### The project compiles but looks or behaves incorrectly

Check:

- sprite source dimensions and padding;
- frame placement;
- origins and collision masks;
- room layers;
- viewport settings;
- application-surface scaling;
- shaders;
- extensions;
- and dynamic drawing behavior.

---

## Building from source

Run dependency setup:

```powershell
powershell -ExecutionPolicy Bypass -File .\Setup-Dependencies.ps1
```

Then open:

```text
SplitGM-VM-Decompiler.sln
```

Select:

```text
Release | x64
```

Set this as the startup project:

```text
SGMVMDLauncher
```

Build the complete solution.

The main GUI project is launcher-authorized and is not the correct direct startup project.

### Framework-dependent release

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1
```

### Self-contained release

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1 -SelfContained
```

Published application output:

```text
artifacts\win-x64\SplitGM\
```

Start the published application with:

```text
artifacts\win-x64\SplitGM\SGMVMDLauncher.exe
```

Package the complete:

```text
artifacts\win-x64\
```

folder so the application, license, notices, and README remain together.

The build script:

- uses a repository-local NuGet cache;
- retries once after rebuilding that cache when restore fails;
- builds Underanalyzer and UndertaleModLib first;
- builds the full SplitGM solution;
- publishes the GUI;
- publishes the launcher as a single file with its embedded frame resources;
- verifies both required EXEs;
- and writes a timestamped build log.

---

## Source package

The v0.5.1.0 source release should correspond to the same version as the precompiled build.

The source package contains:

- SplitGM.Core;
- SplitGM.Gui;
- SGMVMDLauncher;
- SGMVMDLauncher.Playback;
- build and dependency scripts;
- documentation;
- licenses and notices;
- and the pinned UndertaleModTool / Underanalyzer source tree required by this release.

Generated release binaries, NuGet caches, user game files, TEMP runs, and unrelated build output should not be included in the clean source archive.

---

## Reporting problems

A useful report should include:

- SplitGM version;
- Windows version;
- framework-dependent or self-contained package;
- loaded input type;
- detected GameMaker and bytecode version;
- effective game profile and detection confidence;
- exact steps to reproduce;
- the operation that failed;
- activity, TEMP-run, reconstruction, repair, validation, build, or crash logs;
- and screenshots when helpful.

Do not upload commercial game archives, executables, recovered assets, audio, sprites, or complete reconstructed projects unless you have permission to distribute them.

A small independently created GameMaker test project is preferred.

---

## Legal and responsible use

SplitGM is intended for legitimate research, interoperability, preservation, debugging, education, and analysis of files the user is legally allowed to inspect.

Users are responsible for applicable laws, licenses, platform rules, and the rights of game creators.

SplitGM is not intended to bypass:

- DRM;
- encryption;
- authentication;
- ownership checks;
- access controls;
- or paid-content restrictions.

---

## License and source code

SplitGM-VM Decompiler is licensed under the **GNU General Public License version 3.0**.

Binary distributions must retain the applicable license and third-party notices and must be accompanied by, or provide access to, the complete corresponding source code for the same release.

See:

```text
LICENSE.txt
THIRD-PARTY-NOTICES.md
```

Major third-party components include:

- UndertaleModTool and UndertaleModLib — GPL-3.0;
- Underanalyzer — MPL-2.0;
- AvalonEdit — MIT;
- NAudio — MIT;
- NAudio.Vorbis — MIT;
- Magick.NET — Apache-2.0.

---

## Credits

- **sonic Fan Tech** — SplitGM project, interface, extraction workflows, viewers, relationship tools, project reconstruction, branding, launcher design, testing, and release work.
- **UnderminersTeam and UndertaleModTool contributors** — UndertaleModTool, UndertaleModLib, Underanalyzer, GameMaker format research, and the loading/decompilation/texture/room systems SplitGM uses or adapts.
- **OpenAI Codex** — implementation and iterative testing assistance for the final v0.5.1.0 profile, TEMP-run, reconstruction-validation, and bug-fix work.
- AvalonEdit contributors.
- NAudio and NAudio.Vorbis contributors.
- Magick.NET contributors.
- Everyone who tests SplitGM and submits useful reports.

---

## Disclaimer

SplitGM-VM Decompiler is an independent community project.

It is not affiliated with, endorsed by, or sponsored by:

- YoYo Games;
- GameMaker;
- Valve or Steam;
- Toby Fox;
- 8-4;
- or the developers and publishers of games inspected with SplitGM.

GameMaker, Steam, UNDERTALE, DELTARUNE, and other names belong to their respective owners.
