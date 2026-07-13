<div align="center">

<img width="1967" height="800" alt="SplitGM_SPLASH" src="https://github.com/user-attachments/assets/20a942f8-d675-4d12-a5bb-39bd511f2a71" />

# SplitGM-VM Decompiler

### Read-only GameMaker VM decompilation, resource exploration, extraction, relationship analysis, and project reconstruction

[![Version](https://img.shields.io/badge/version-0.4.0-6f42c1?style=for-the-badge)](../../releases/latest)
[![Status](https://img.shields.io/badge/status-public%20beta-f0ad4e?style=for-the-badge)](#public-beta-status)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078d4?style=for-the-badge)](#system-requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?style=for-the-badge)](#building-from-source)
[![License](https://img.shields.io/badge/license-GPL--3.0-2ea44f?style=for-the-badge)](#license)

**[Download the latest release](../../releases/latest) · [View all releases](../../releases) · [Report a problem](../../issues)**

</div>

---

## About SplitGM

**SplitGM-VM Decompiler** is a Windows desktop application for inspecting and reconstructing GameMaker games that use the GameMaker VM runtime.

SplitGM can load a supported GameMaker data archive, reconstruct readable GML from VM bytecode, display game resources through specialized read-only viewers, export recoverable assets, analyze relationships between resources and code, and produce an organized reconstructed-project export.

SplitGM is intentionally **read-only**. It does not edit, patch, recompile, or save changes back into the selected game file.

The program is built around the proven GameMaker research and parsing systems provided by:

- **UndertaleModLib** for GameMaker data loading, resource models, format/version handling, VM disassembly, texture access, and audio-group handling.
- **Underanalyzer** for GameMaker VM control-flow analysis and high-level GML reconstruction.

SplitGM adds its own GUI, loading workflow, viewers, relationship tools, export system, safety checks, progress reporting, caching, diagnostics, and future project-reconstruction pipeline.

## Public beta status

Version **0.4.0** is an early public beta. It has been tested successfully with locally owned copies of **UNDERTALE** and **DELTARUNE**, but GameMaker games vary widely by compiler version, runtime, platform, extensions, resource formats, and project structure.

Expect some games or individual resources to expose unsupported edge cases. Keep the original game files backed up, report reproducible failures, and never distribute copyrighted game data with an issue report.

## Main features

### GameMaker VM decompilation

- Reconstructs readable GML from supported GameMaker VM bytecode.
- Displays raw GameMaker VM assembly alongside reconstructed GML.
- Organizes code into scripts, object events, room code, global initialization, timelines, and other code categories.
- Preserves access to VM assembly when high-level decompilation fails.
- Continues processing after individual code-entry failures.
- Records detailed decompiler errors without cancelling the complete export.
- Detects YYC/native builds and clearly reports that normal VM GML is unavailable.

### Resource Explorer

SplitGM loads the selected game once and exposes its contents through a paged, searchable Resource Explorer. Supported categories include:

- General game information
- Code entries and scripts
- Objects and object events
- Rooms, layers, instances, views, tiles, and creation code
- Sprites and animation frames
- Backgrounds and tilesets
- Sounds, embedded audio, and audio groups
- Fonts and glyph atlases
- Shaders
- Paths
- Timelines
- Sequences
- Animation curves
- Particle systems and emitters
- Texture pages and texture-page items
- Texture groups
- Embedded images
- Extensions
- Filter effects
- Functions
- Variables
- Strings

Large resource categories are divided into manageable pages instead of creating thousands of interface elements at once.

### GML and VM code viewer

- Read-only AvalonEdit-based code display
- GML and VM assembly tabs
- Syntax highlighting
- Line numbers
- Word wrapping
- Search within the selected document
- Global search across code entries
- Copy and export actions through the menu bar
- Automatic performance limits for extremely large files
- Background decompilation so complex code does not block the WPF interface
- Cancellation when the user selects a different entry before the previous preview finishes
- Bounded code caches to reduce memory use

### Room viewer

The room workspace can display and inspect:

- Room dimensions, speed, persistence, and creation code
- GMS1 backgrounds
- GMS2 background layers
- Background colors and color layers
- Stretching and horizontal/vertical tiling
- Layer offsets, depth, visibility, and movement speeds
- Object instances and their transform data
- Instance creation and pre-create code
- Sprite assets placed in rooms
- GMS2 tile layers and tileset data
- Tile flip and rotation flags
- Room views and camera-related information

Room previews are bounded raster reconstructions intended for inspection. They do not run the game or reproduce runtime effects, shaders, dynamic drawing code, or every engine behavior.

### Object and connected-code navigation

Objects expose their assigned sprite, parent, collision mask, physics information, and event mappings.

Double-clicking an object, object event, room instance, or navigable relationship result opens a connected-code window. For room instances, SplitGM combines:

- Instance creation code
- Instance pre-create code
- Object Create, Step, Draw, Collision, and other events

The user can select a connected entry and open its reconstructed GML immediately.

### Sprite, image, and texture viewing

- View sprite metadata, origins, bounds, collision modes, and frame counts.
- Navigate or play normal raster sprite frames.
- Preview backgrounds, tilesets, font atlases, texture-page items, complete embedded texture pages, and embedded images.
- Export individual frames or complete sprite resources.
- Export collision masks as PNG files when recoverable.
- Export recoverable Spine JSON and atlas text where present.
- Use nearest-neighbor scaling for pixel-art-oriented room previews.

### Audio viewing and playback

- Inspect sound metadata, flags, file names, formats, volume, pitch, and audio-group references.
- Resolve audio embedded in the main data archive.
- Resolve audio stored in external GameMaker audio-group files when the files are present.
- Resolve streamed external audio when the referenced file is present.
- Play WAV audio.
- Play OGG Vorbis audio.
- Play MP3 audio.
- Stop playback safely when changing resources or closing a game.
- Export the selected sound.
- Export a selected audio group.
- Export all recoverable sounds or embedded audio.

### Relationship and reference analysis

The Relationships workspace can inspect:

- Function and script callers
- Function and script callees
- Global-variable usage
- Static room transitions such as `room_goto(room_name)`
- Object parent/child inheritance
- Object event code
- Objects placed across rooms
- Room and instance creation code
- Sprite and collision-mask relationships
- Room layers, backgrounds, and tileset relationships
- Named resource references found in reconstructed GML
- Heuristic unused-resource candidates

Results can be double-clicked to navigate to the related resource or code entry.

Relationship and unused-resource analysis is partly heuristic. GameMaker projects can resolve resources dynamically through strings, extensions, native code, generated names, or YYC code, so a reported unused resource is not guaranteed to be truly unused.

### Export system

The **Export** menu supports:

- Full reconstructed-project export
- Selected-resource export
- Selected audio-group export
- Complete export of a chosen resource type

Resource-type export groups include:

- **Graphics:** sprites, backgrounds/tilesets, fonts, texture pages, texture-page items, and embedded images
- **Audio:** sounds, audio groups, and embedded audio
- **Rooms and Objects:** rooms and objects
- **Motion and Time:** paths, timelines, sequences, and animation curves
- **Rendering and Effects:** shaders, particle systems, particle emitters, filter effects, and texture groups
- **Metadata:** extensions, strings, functions, and variables

Exports provide:

- A dedicated operation window
- Current stage and item information
- Item counts and percentage
- Elapsed time
- Live messages
- Cancellation at safe stopping points
- Per-resource error continuation
- Category summary files
- Safe output-path validation
- Optional opening of the completed output directory

A full reconstructed export uses an organized layout similar to:

```text
Game_SplitGM_Project\
├── SplitGM-Manifest.json
├── CodeIndex.json
├── GameInfo\
├── Code\
├── Scripts\
├── Objects\
├── Rooms\
├── GlobalInit\
├── Timelines\
├── VMAssembly\
├── ResourceIndexes\
├── Resources\
│   ├── Sprites\
│   ├── Sounds\
│   ├── Audio-Groups\
│   ├── Rooms\
│   ├── Objects\
│   ├── Fonts\
│   ├── Shaders\
│   ├── Texture-Pages\
│   └── ...
├── Errors\
└── Logs\
```

### Loading, progress, and diagnostics

Opening a supported data file or EXE displays a detailed loading window containing:

- Input path
- Current stage
- Current item
- Completed item count
- Percentage
- Elapsed time
- Live status messages
- Cancellation support

SplitGM also includes:

- Persistent activity logs
- Timestamped build logs
- Detailed crash reports with inner exceptions
- A copyable diagnostic report
- Privacy-conscious path handling in diagnostics
- Safe cleanup when replacing or closing very large game sessions

### Menu-driven interface

The main window uses a standard menu bar instead of a large row of action buttons:

- **File** — open, close, reopen, output-folder, and exit actions
- **Edit** — selection and copy-related actions
- **Export** — selected, grouped, and full-project exports
- **View** — workspaces, editor display options, and refresh actions
- **Tools** — search, relationships, unused candidates, logs, diagnostics, and settings
- **Help** — About and licensing information

### Settings

Settings are stored in:

```text
SplitGM_Settings.ini
```

SplitGM first attempts to store the INI file beside the executable. When that folder is read-only, it uses the SplitGM folder under Local AppData while keeping the same filename.

Available settings include:

- Default export directory
- Export overwrite behavior
- Progress-window behavior
- Automatically opening completed output
- Line-number visibility
- Word wrapping
- Relationship-result limits
- Window size and placement restoration

### About and licensing windows

The About interface is a dedicated resizable sub-window rather than a message box. It presents SplitGM version, runtime details, component information, project purpose, credits, and licensing information.

## Supported input

SplitGM can attempt to open:

```text
data.win
*.win
*.unx
*.ios
*.droid
*.android
*.game
*.exe
```

Windows EXE handling supports:

- A neighboring GameMaker data archive
- A validated embedded `FORM` archive when one can be located safely

SplitGM does not promise support for packed, encrypted, intentionally obfuscated, damaged, or DRM-protected executables.

## VM, YYC, and reconstructed source

GameMaker VM builds contain bytecode that can be analyzed and reconstructed into high-level GML.

YYC builds compile code into native machine code. SplitGM can still expose recoverable data resources and metadata from a YYC game, but it cannot recreate normal GameMaker VM GML when the VM bytecode is absent.

Even for supported VM games, decompiled GML is reconstructed source—not the exact original source. The following may be permanently unavailable or only inferable:

- Comments
- Original formatting
- Some local-variable names
- Original macros and enums
- Original folder organization
- Compiler-optimized-away code
- Exact source-level control-flow choices
- Some extension or platform-specific information

## Basic usage

1. Start `SplitGM-VM-Decompiler.exe`.
2. Select **File → Open Game**.
3. Choose a supported GameMaker data file or Windows executable.
4. Wait for the loading operation to finish.
5. Browse resources from the Resource Explorer.
6. Select code to view reconstructed GML or VM assembly.
7. Double-click objects, events, room instances, or relationships to navigate connected code.
8. Use the **Export** menu to export one resource, a complete category, an audio group, or a reconstructed project.

SplitGM never needs to be placed inside the game directory. Keeping it in its own folder is recommended.

## System requirements

### Prebuilt framework-dependent release

- Windows 10 or newer, x64
- .NET 10 Desktop Runtime, x64
- Sufficient free memory and disk space for the selected game and export

### Self-contained release

A self-contained build includes the required .NET runtime and does not require a separate .NET Desktop Runtime installation. It is larger than the standard framework-dependent package.

Very large games can require substantial memory when loading resources, decoding texture pages, rendering rooms, searching all code, or exporting every asset.

## Building from source

### Requirements

- Windows x64
- Visual Studio 2026
- **.NET desktop development** workload
- .NET 10 SDK
- PowerShell 5.1 or newer
- Internet access for the one-time dependency setup

Git and winget are not required.

### 1. Download dependencies

From the source root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Setup-Dependencies.ps1
```

The script downloads the pinned UndertaleModTool revision and the matching Underanalyzer revision into:

```text
External\UndertaleModTool\
```

To replace an existing dependency directory with the tested revisions:

```powershell
powershell -ExecutionPolicy Bypass -File .\Setup-Dependencies.ps1 -Force
```

The optional `-UseLatest` switch is intended for development experiments. A newer upstream revision may introduce API or format changes that have not been tested with SplitGM.

### 2. Build with Visual Studio

Open:

```text
SplitGM-VM-Decompiler.sln
```

Then:

1. Select **Release | x64**.
2. Set `SplitGM.Gui` as the startup project.
3. Build the complete solution.

The solution contains:

```text
Underanalyzer
UndertaleModLib
SplitGM.Core
SplitGM.Gui
```

SplitGM is a GUI-only application; there is no separate CLI release.

### 3. Build a release package

Framework-dependent Windows x64 build:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1
```

Self-contained Windows x64 build:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1 -SelfContained
```

Published application:

```text
artifacts\win-x64\SplitGM\
```

Release documentation and license files:

```text
artifacts\win-x64\README.md
artifacts\win-x64\LICENSE.txt
artifacts\win-x64\THIRD-PARTY-NOTICES.md
```

Timestamped build logs:

```text
Logs\Build-YYYY-MM-DD_HH-mm-ss.log
```

When creating a downloadable binary ZIP, package the complete contents of `artifacts\win-x64\`, not only the executable.

## Source layout

```text
SplitGM-VM-Decompiler\
├── src\
│   ├── SplitGM.Core\
│   └── SplitGM.Gui\
├── External\
│   └── UndertaleModTool\
├── docs\
├── Setup-Dependencies.ps1
├── Clean-Solution.ps1
├── Build-Release.ps1
├── Directory.Build.props
├── SplitGM-VM-Decompiler.sln
├── LICENSE.txt
└── THIRD-PARTY-NOTICES.md
```

## Planned development

The roadmap may change as testing reveals new priorities.

### v0.5 — Reconstructed project export

- Stable, versioned `.splitgmproj` intermediate format
- Stronger preservation of resource relationships
- Export validation and reconstruction warnings
- Experimental, version-aware GameMaker `.yyp` project generation
- Clear reporting for data that cannot be represented safely

A `.yyp` export will be reconstructed rather than identical to the original project. The first goal is a transparent, inspectable project that can be repaired—not a guarantee that every decompiled game will compile immediately.

### v0.6 — Project validation and repair

- Missing-reference detection
- Duplicate-name detection
- Broken room-instance and event-reference reports
- Unsupported extension/function reports
- Resource JSON validation
- Automated repairs for safe, unambiguous problems

### v0.7 — Advanced analysis

- Improved call graphs
- Struct and constructor detection
- Macro, enum, and constant inference
- Duplicate-code detection
- State-machine analysis
- Better local-variable recovery
- Additional room-transition and resource-use visualization

### v0.8 — Compatibility and batch workflows

- Compatibility profiles for more GameMaker generations
- Batch processing
- Per-game decompiler settings
- Resume and incremental export
- Comparison between builds of the same game
- Better recovery from partially damaged archives

### v0.9 — Release candidate

- Larger regression-test suite
- Performance and memory tuning
- Accessibility and interface polish
- Installer and portable distribution work
- Final public documentation and compatibility reporting

### v1.0 — Stable Decompiler Studio

The stable release target combines VM decompilation, complete resource browsing and extraction, relationship analysis, project reconstruction, validation, large-game stability, documentation, and a tested public release workflow.

## Reporting issues

A useful issue report should include:

- SplitGM version
- Windows version
- Whether the release is framework-dependent or self-contained
- Detected GameMaker and bytecode version, when available
- Selected input type (`data.win`, EXE, audio group, and so on)
- Exact steps to reproduce the problem
- The operation that failed
- The relevant SplitGM activity, build, or crash log
- A screenshot when it helps explain the interface problem

Do **not** upload a commercial game’s `data.win`, executable, sprites, audio, or other copyrighted assets unless you have permission to distribute them. A minimal independently created GameMaker test project is preferred when a file is needed to reproduce a bug.

## Legal and responsible use

SplitGM is intended for legitimate research, interoperability, preservation, debugging, education, and analysis of files the user is legally allowed to inspect.

Users are responsible for complying with applicable laws, licenses, platform rules, and the rights of game creators. SplitGM does not include game files and is not intended to bypass DRM, encryption, authentication, access controls, or paid-content restrictions.

## License

SplitGM-VM Decompiler is licensed under the **GNU General Public License version 3.0**. See [`LICENSE.txt`](LICENSE.txt).

Binary releases should be accompanied by the complete corresponding source code for the same release and must retain the applicable license and copyright notices.

Third-party components remain under their own licenses. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for details.

Major components include:

| Component | Purpose | License |
|---|---|---|
| UndertaleModTool / UndertaleModLib | GameMaker loading, models, disassembly, texture/audio infrastructure | GPL-3.0 |
| Underanalyzer | VM analysis and GML reconstruction | MPL-2.0 |
| AvalonEdit | GML, assembly, and text viewing | MIT |
| NAudio | Windows audio output and WAV/MP3 decoding | MIT |
| NAudio.Vorbis | OGG Vorbis decoding | MIT |
| Magick.NET | Texture decoding, image export, and room-preview composition | Apache-2.0 |

## Credits

- **sonic Fan Tech** — SplitGM project, interface, workflow, viewers, export systems, relationship tools, and integration
- **UnderminersTeam and UndertaleModTool contributors** — UndertaleModTool, UndertaleModLib, GameMaker format research, and Underanalyzer
- **AvalonEdit contributors**
- **NAudio and NAudio.Vorbis contributors**
- **Magick.NET contributors**
- Everyone who tests SplitGM and submits useful compatibility reports

## Disclaimer

SplitGM-VM Decompiler is an independent community project. It is not affiliated with, endorsed by, or sponsored by YoYo Games, GameMaker, Toby Fox, 8-4, or the developers and publishers of games inspected with the program. GameMaker, UNDERTALE, DELTARUNE, and other names belong to their respective owners.
