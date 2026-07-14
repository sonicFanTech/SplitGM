<div align="center">

# SplitGM-VM Decompiler

### Read-only GameMaker VM decompilation, resource exploration, extraction, relationship analysis, and reconstructed GameMaker project export

[![Version](https://img.shields.io/badge/version-0.5.0-6f42c1?style=for-the-badge)](../../releases/latest)
[![Status](https://img.shields.io/badge/status-public%20beta-f0ad4e?style=for-the-badge)](#public-beta-and-reconstruction-status)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078d4?style=for-the-badge)](#system-requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?style=for-the-badge)](#building-from-source)
[![License](https://img.shields.io/badge/license-GPL--3.0-2ea44f?style=for-the-badge)](#license)

**[Download the latest release](../../releases/latest) · [View all releases](../../releases) · [Report a problem](../../issues)**

</div>

---

## SplitGM v0.5.0

SplitGM v0.5.0 adds the first working **reconstructed GameMaker `.yyp` project exporter**.

A supported compiled GameMaker VM game can now be loaded, inspected, decompiled, and exported into a real GameMaker project folder containing reconstructed resources, recovered GML, project metadata, resource relationships, validation reports, and fallback data for anything SplitGM cannot represent safely.

The generated project is intended to be:

- openable and editable in GameMaker;
- transparent about what was reconstructed, inferred, changed, or lost;
- useful as a research, preservation, inspection, and repair workspace;
- safer than silently generating invalid project data.

It is **not** guaranteed to be identical to the original developer project, compile immediately, run correctly, or reproduce the original game perfectly.

## About SplitGM

**SplitGM-VM Decompiler** is a Windows desktop application for inspecting and reconstructing games made with GameMaker's VM runtime.

SplitGM can:

- load supported GameMaker data archives and Windows executables;
- reconstruct readable GML from GameMaker VM bytecode;
- display VM assembly beside reconstructed code;
- browse and preview game resources;
- export recoverable assets and metadata;
- analyze relationships between code and resources;
- create an organized SplitGM extraction project;
- generate an experimental reconstructed GameMaker `.yyp` project.

SplitGM is intentionally **read-only toward the selected game**. It does not patch, overwrite, recompile, or save changes back into the original `data.win`, executable, or other selected game file.

SplitGM is built around the proven GameMaker research and parsing systems provided by:

- **UndertaleModLib** for GameMaker data loading, resource models, format and version handling, VM disassembly, texture access, audio handling, and related infrastructure.
- **Underanalyzer** for GameMaker VM control-flow analysis and high-level GML reconstruction.

SplitGM adds its own desktop interface, loading workflow, viewers, relationship tools, extraction system, project reconstruction pipeline, validation, progress reporting, performance controls, caching, diagnostics, and safety checks.

## Public beta and reconstruction status

Version **0.5.0** is a public beta and the first SplitGM release capable of generating a real, openable GameMaker project.

The reconstructed `.yyp` feature has been tested with a locally owned **DELTARUNE Chapter Select `data.win`**. The generated project opened in GameMaker, loaded reconstructed resources, and could be edited. After manual repairs to common decompiler artifacts, it also compiled and entered the reconstructed chapter-select room, although the rendered result was not visually accurate.

That test proves the approach is possible, but it does not prove compatibility with every GameMaker title.

GameMaker games vary by:

- GameMaker and bytecode version;
- VM or YYC compilation;
- target platform;
- extensions and native components;
- resource formats;
- room and layer systems;
- shaders, sequences, particles, and timelines;
- compiler optimizations and removed source information;
- custom build pipelines and deliberate protection.

Expect unsupported games, partially reconstructed resources, GML errors, name collisions, missing references, inaccurate rooms, or projects that require manual repair.

Always keep the original files backed up, and do not distribute copyrighted game data when reporting a problem.

## Main features

### GameMaker VM decompilation

- Reconstructs readable GML from supported GameMaker VM bytecode.
- Displays raw GameMaker VM assembly beside reconstructed GML.
- Organizes code into scripts, object events, room code, global initialization, timelines, and other code categories.
- Reconstructs GlobalScript and GlobalInit code as script resources when possible.
- Preserves VM assembly when high-level decompilation fails.
- Continues processing after individual code-entry failures.
- Records detailed decompiler failures without cancelling an entire export.
- Detects YYC/native builds and reports when normal VM GML is unavailable.
- Keeps raw or fallback code in the reconstructed output when it cannot be represented safely.

### Decompile to Reconstructed `.yyp` Project

Use:

```text
Tools → Decompile to Reconstructed .yyp Project...
```

This feature creates a repair-oriented GameMaker project folder from the currently loaded game.

Where recoverable and safely representable, the export can include:

- a real GameMaker `.yyp` project file;
- scripts and recovered global functions;
- object resources and object-event GML;
- room resources;
- room creation code;
- room instance creation and pre-create code;
- sprites and animation frames;
- collision masks;
- sounds and audio groups;
- paths;
- project folders and resource ordering;
- room order;
- object physics settings and vertices;
- sprite playback, origin, bounds, and collision settings;
- room views, view-follow targets, room physics, and background color;
- object sprite, parent, mask, collision-event, and room-instance relationships;
- sound-to-audio-group relationships.

The export is reconstructed rather than copied from original source files. A compiled game normally does not contain the complete original project, comments, formatting, folder layout, macros, extension source, or every source-level relationship.

### Versioned `.splitgmproj` intermediate format

Every reconstructed GameMaker project also includes a stable, inspectable `.splitgmproj` document.

Format version `1.0` records information such as:

- SplitGM and format version;
- source game and detected GameMaker information;
- selected target project schema;
- deterministic resource IDs;
- original and reconstructed resource names;
- resource type and reconstruction status;
- recovered relationships;
- output files;
- warnings and errors;
- validation results;
- unsupported or unrepresented data.

The format is intended to give future SplitGM versions a stable repair and migration layer instead of tying every feature directly to one GameMaker `.yyp` schema.

See:

```text
docs\SPLITGMPROJ-FORMAT.md
```

### Reconstruction validation and fallback reporting

SplitGM validates reconstructed output for common structural problems, including:

- invalid or missing files;
- malformed JSON;
- duplicate resource identifiers;
- duplicate or unsafe names;
- broken reconstructed references;
- output paths that escape the chosen project directory;
- resources that cannot safely be added to the `.yyp`.

Resources that cannot yet be represented are not silently discarded. SplitGM stores inspectable data in folders such as:

```text
__SplitGM_Metadata\
__SplitGM_Unrepresented\
```

A reconstructed project also receives reports describing what was generated, what was skipped, and what may require repair.

### Reconstructed-project progress window

The reconstructed `.yyp` exporter has a dedicated progress window that shows:

- output path;
- current stage;
- elapsed time;
- completed and total item counts;
- percentage;
- every queued script and resource;
- current status;
- resource type;
- resource name;
- output file;
- live warnings and errors;
- cancellation status.

Visual resources can display image previews while they are exported. Other inspectable resources can display text or metadata. Audio resources are exported without attempting to display an audio preview.

### High-speed bulk export

Large games can contain tens or hundreds of thousands of code entries, frames, and resources. SplitGM uses bounded parallel processing for reconstructed-project export and category-wide resource export.

The export system:

- processes independent resources in parallel;
- limits worker counts to avoid excessive memory use;
- reuses decoded texture-page data while exporting sprites;
- avoids generating a second expensive preview copy before the real export;
- preserves deterministic project and manifest ordering after parallel work completes;
- continues after individual resource failures.

Performance still depends heavily on storage speed, CPU core count, available memory, texture-page size, antivirus scanning, and the number of files being written.

### Resource Explorer

SplitGM loads the selected game once and exposes its contents through a paged, searchable Resource Explorer.

Supported categories include:

- general game information;
- code entries and scripts;
- objects and object events;
- rooms, layers, instances, views, tiles, and creation code;
- sprites and animation frames;
- backgrounds and tilesets;
- sounds, embedded audio, and audio groups;
- fonts and glyph atlases;
- shaders;
- paths;
- timelines;
- sequences;
- animation curves;
- particle systems and emitters;
- texture pages and texture-page items;
- texture groups;
- embedded images;
- extensions;
- filter effects;
- functions;
- variables;
- strings.

Large categories are divided into manageable pages instead of creating thousands of interface elements at once.

### GML and VM code viewer

- Read-only AvalonEdit-based code display.
- Reconstructed GML and VM assembly tabs.
- Syntax highlighting.
- Line numbers.
- Word wrapping.
- Search inside the selected document.
- Global search across code entries.
- Copy and export actions through the menu bar.
- Automatic performance limits for extremely large files.
- Background decompilation so complex code does not block the WPF interface.
- Cancellation when another entry is selected before the previous preview finishes.
- Bounded code caches to reduce memory use.
- Failure-safe syntax highlighting so a definition problem does not prevent SplitGM from opening.

### Room viewer

The room workspace can display and inspect:

- room dimensions, speed, persistence, and creation code;
- GMS1 backgrounds;
- GMS2 background layers;
- background colors and color layers;
- stretching and horizontal or vertical tiling;
- layer offsets, depth, visibility, and movement speeds;
- object instances and transform data;
- instance creation and pre-create code;
- sprite assets placed in rooms;
- GMS2 tile layers and tileset data;
- tile flip and rotation flags;
- room views and camera-related information.

Room previews are bounded raster reconstructions intended for inspection. They do not run the game or reproduce dynamic drawing code, shaders, surfaces, particles, runtime cameras, or every GameMaker behavior.

### Object and connected-code navigation

Objects expose:

- assigned sprite;
- parent object;
- collision mask;
- visibility, solidity, persistence, and depth;
- physics information;
- event mappings.

Double-clicking an object, object event, room instance, or navigable relationship result opens connected code.

For a room instance, SplitGM can combine:

- instance creation code;
- instance pre-create code;
- object Create, Step, Draw, Collision, and other events.

The user can then select a connected entry and open its reconstructed GML.

### Sprite, image, and texture viewing

- View sprite metadata, dimensions, origins, bounds, collision modes, and frame counts.
- Navigate or play normal raster sprite frames.
- Preview backgrounds, tilesets, font atlases, texture-page items, full texture pages, and embedded images.
- Export individual frames or complete sprite resources.
- Export collision masks as PNG files when recoverable.
- Export recoverable Spine JSON and atlas text when present.
- Use nearest-neighbor scaling for pixel-art-oriented room previews.
- Reuse texture-page decoding during bulk exports for improved performance.

### Audio viewing and playback

- Inspect sound metadata, flags, source names, formats, volume, pitch, and audio-group references.
- Resolve audio embedded in the main data archive.
- Resolve audio stored in external GameMaker audio-group files when those files are present.
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

- function and script callers;
- function and script callees;
- global-variable usage;
- static room transitions such as `room_goto(room_name)`;
- object parent and child inheritance;
- object event code;
- objects placed across rooms;
- room and instance creation code;
- sprite and collision-mask relationships;
- room layers, backgrounds, and tileset relationships;
- named resource references found in reconstructed GML;
- heuristic unused-resource candidates.

Results can be double-clicked to navigate to the related resource or code entry.

Relationship and unused-resource analysis is partly heuristic. GameMaker projects can resolve resources dynamically through strings, extensions, native code, generated identifiers, or YYC code, so a reported unused resource is not guaranteed to be unused.

### Extraction and resource export system

The **Export** menu supports:

- full organized SplitGM extraction-project export;
- selected-resource export;
- selected audio-group export;
- complete export of a chosen resource category.

Resource-type groups include:

- **Graphics:** sprites, backgrounds, tilesets, fonts, texture pages, texture-page items, and embedded images;
- **Audio:** sounds, audio groups, and embedded audio;
- **Rooms and Objects:** rooms and objects;
- **Motion and Time:** paths, timelines, sequences, and animation curves;
- **Rendering and Effects:** shaders, particle systems, particle emitters, filter effects, and texture groups;
- **Metadata:** extensions, strings, functions, and variables.

Exports provide:

- a dedicated operation window;
- current stage and item information;
- item counts and percentage;
- elapsed time;
- live messages;
- cancellation at safe stopping points;
- per-resource error continuation;
- category summary files;
- safe output-path validation;
- optional opening of the completed output directory.

A full SplitGM extraction project uses an organized layout similar to:

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

This organized extraction format is separate from the reconstructed GameMaker `.yyp` project export.

### Loading, progress, and diagnostics

Opening a supported data file or EXE displays a detailed loading window containing:

- input path;
- current stage;
- current item;
- completed item count;
- percentage;
- elapsed time;
- live status messages;
- cancellation support.

SplitGM also includes:

- persistent activity logs;
- timestamped build logs;
- detailed crash reports with inner exceptions;
- a copyable diagnostic report;
- privacy-conscious path handling in diagnostics;
- safe cleanup when replacing or closing very large game sessions.

### Menu-driven interface

The main window uses a standard menu bar:

- **File** — open, close, reopen, output-folder, and exit actions;
- **Edit** — selection and copy-related actions;
- **Export** — selected, grouped, audio-group, and full extraction-project exports;
- **View** — workspaces, editor display options, and refresh actions;
- **Tools** — reconstructed `.yyp` export, search, relationships, unused candidates, logs, diagnostics, and settings;
- **Help** — About and licensing information.

Context-specific playback and frame-navigation controls remain inside applicable resource viewers.

### Settings

Settings are stored in:

```text
SplitGM_Settings.ini
```

SplitGM first attempts to store the INI file beside the executable. If that folder is read-only, it uses the SplitGM folder under Local AppData while keeping the same filename.

Available settings include:

- default export directory;
- export overwrite behavior;
- progress-window behavior;
- automatically opening completed output;
- line-number visibility;
- word wrapping;
- relationship-result limits;
- window size and placement restoration.

### About and licensing windows

The About interface is a dedicated resizable window rather than a message box. It presents:

- SplitGM version;
- runtime details;
- component information;
- project purpose;
- credits;
- license information.

Version 0.5.0 also integrates the SplitGM program logo and splash artwork throughout the application.

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

- a neighboring GameMaker data archive;
- a validated embedded `FORM` archive when one can be located safely.

SplitGM does not promise support for packed, encrypted, intentionally obfuscated, damaged, DRM-protected, or unsupported executables.

## VM, YYC, and reconstructed source

GameMaker VM builds contain bytecode that can be analyzed and reconstructed into high-level GML.

YYC builds compile code into native machine code. SplitGM can still expose recoverable resources and metadata from a YYC game, but it cannot recreate normal GameMaker VM GML when VM bytecode is absent.

Even for a supported VM game, reconstructed GML and project data are not the exact original source. The following may be unavailable, altered by compilation, or only inferable:

- comments;
- original formatting;
- some local-variable names;
- original macros and enum names;
- original function signatures and optional arguments;
- original folder organization;
- compiler-optimized-away code;
- exact source-level control-flow choices;
- extension source and platform-specific information;
- original sprite source canvases or import settings;
- IDE-only metadata not stored in the compiled game.

## Basic usage

### Browse and extract a game

1. Start `SplitGM-VM-Decompiler.exe`.
2. Select **File → Open Game**.
3. Choose a supported GameMaker data file or Windows executable.
4. Wait for loading to finish.
5. Browse resources in the Resource Explorer.
6. Select code to view reconstructed GML or VM assembly.
7. Double-click objects, events, room instances, or relationships to navigate connected code.
8. Use **Export** to export one resource, an audio group, a complete category, or a full organized SplitGM extraction project.

### Generate a reconstructed GameMaker project

1. Load a supported GameMaker VM game.
2. Select **Tools → Decompile to Reconstructed .yyp Project...**.
3. Choose an empty output folder or a folder previously created by SplitGM's reconstructed-project exporter.
4. Review the complete queued resource list.
5. Allow the export to finish, or cancel at a safe stopping point.
6. Read the generated reconstruction and validation reports.
7. Open the generated `.yyp` in a compatible GameMaker IDE.
8. Treat the project as a repair workspace and expect manual work.

SplitGM does not need to be placed in the game directory. Keeping it in its own folder is recommended.

## System requirements

### Framework-dependent release

- Windows 10 or newer, x64;
- .NET 10 Desktop Runtime, x64;
- sufficient free memory and disk space for the selected game and export.

### Self-contained release

A self-contained build includes the required .NET runtime and does not require a separate .NET Desktop Runtime installation. It is larger than the framework-dependent package.

Very large games can require substantial memory and disk activity when loading resources, decoding texture pages, rendering rooms, searching all code, extracting every asset, or writing a reconstructed GameMaker project containing thousands of small files.

## Building from source

### Requirements

- Windows x64;
- Visual Studio 2026;
- **.NET desktop development** workload;
- .NET 10 SDK;
- PowerShell 5.1 or newer;
- internet access for the one-time dependency setup.

Git and winget are not required.

### 1. Download dependencies

From the source root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Setup-Dependencies.ps1
```

The script downloads the pinned UndertaleModTool revision and matching Underanalyzer revision into:

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

SplitGM is a GUI-only application. There is no separate SplitGM CLI release.

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

## Known limitations

- Reconstructed GML is not the exact original source.
- A reconstructed `.yyp` can open successfully while still containing compile errors.
- Common remaining problems can include invalid names, duplicate names, duplicate enum placeholders, incorrect function signatures, missing global functions, duplicate local variables, missing extension functions, unresolved references, and version-specific syntax.
- Reconstructed sprites can have incorrect canvas size, transparent padding, origin, collision mask, or frame-layout behavior.
- Reconstructed rooms can lose complex layers, effects, tiles, cameras, sequence data, or runtime-created content.
- Extensions, shaders, timelines, sequences, particles, and platform-specific systems may be incomplete or exported only as fallback data.
- Successful compilation does not guarantee correct behavior.
- Successful runtime startup does not guarantee visual or behavioral accuracy.
- Relationship and unused-resource analysis is heuristic.
- YYC builds do not contain normal VM bytecode for GML reconstruction.
- SplitGM does not patch or save modified game archives.

## Planned development

The roadmap may change as testing reveals new priorities.

### v0.5.1.0 — Automatic reconstructed-project repair

The next repair-focused build is planned to scan reconstructed projects and automatically fix common, well-understood decompiler and project-generation problems.

Possible repair areas include:

- duplicate placeholder enum names;
- invalid, unsafe, or duplicate resource names;
- case-insensitive name collisions;
- missing GlobalInit and GlobalScript registration;
- recovered functions that require optional parameters;
- duplicate local-variable declarations;
- broken resource references after renaming;
- missing object-event, room-code, or instance-code files;
- sprite canvas, padding, origin, and collision-mask inconsistencies;
- malformed `.yy`, `.yyp`, folder, and resource-order data;
- missing default fields required by the target GameMaker version;
- unresolved function and extension reports;
- project compile-preflight validation.

SplitGM should preserve original decompiler output, record every repair, assign confidence levels, and generate a highly detailed text report for anything it cannot fix safely, including possible manual repair steps.

### v0.6 — Repair expansion and advanced analysis

- safer compatibility shims;
- improved call graphs;
- struct and constructor detection;
- macro, enum, and constant inference;
- duplicate-code detection;
- state-machine analysis;
- better local-variable recovery;
- additional room-transition and resource-use visualization.

### v0.7 — Compatibility and batch workflows

- compatibility profiles for additional GameMaker generations;
- batch processing;
- per-game decompiler and repair settings;
- resume and incremental export;
- comparison between builds of the same game;
- improved recovery from partially damaged archives.

### v0.8 — Project reconstruction expansion

- wider room-layer support;
- improved sequence and animation-curve reconstruction;
- additional shader, font, timeline, and particle handling;
- better extension and platform-data reporting;
- improved sprite and tileset source reconstruction.

### v0.9 — Release candidate

- larger regression-test suite;
- performance and memory tuning;
- accessibility and interface polish;
- installer and portable distribution work;
- final public documentation and compatibility reporting.

### v1.0 — Stable Decompiler Studio

The stable release target combines VM decompilation, complete resource browsing and extraction, relationship analysis, project reconstruction, automatic repair, validation, large-game stability, documentation, and a tested public release workflow.

SplitGM will never be able to recreate every original project perfectly. The goal is to recover as much inspectable and editable information as possible, repair well-understood reconstruction artifacts, and clearly report everything that remains uncertain or unsupported.

## Reporting issues

A useful issue report should include:

- SplitGM version;
- Windows version;
- whether the release is framework-dependent or self-contained;
- detected GameMaker and bytecode version, when available;
- selected input type (`data.win`, EXE, audio group, and so on);
- exact steps to reproduce the problem;
- the operation that failed;
- the relevant SplitGM activity, build, reconstruction, or crash log;
- generated validation or reconstruction reports;
- a screenshot when it helps explain the problem.

Do **not** upload a commercial game's `data.win`, executable, sprites, audio, reconstructed project, or other copyrighted assets unless you have permission to distribute them.

A minimal independently created GameMaker test project is preferred when a file is needed to reproduce a bug.

## Legal and responsible use

SplitGM is intended for legitimate research, interoperability, preservation, debugging, education, and analysis of files the user is legally allowed to inspect.

Users are responsible for complying with applicable laws, licenses, platform rules, and the rights of game creators.

SplitGM does not include game files and is not intended to bypass DRM, encryption, authentication, access controls, or paid-content restrictions.

## License

SplitGM-VM Decompiler is licensed under the **GNU General Public License version 3.0**. See [`LICENSE.txt`](LICENSE.txt).

Binary releases should be accompanied by the complete corresponding source code for the same release and must retain the applicable license and copyright notices.

Third-party components remain under their own licenses. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Major components include:

| Component | Purpose | License |
|---|---|---|
| UndertaleModTool / UndertaleModLib | GameMaker loading, models, disassembly, texture and audio infrastructure | GPL-3.0 |
| Underanalyzer | VM analysis and GML reconstruction | MPL-2.0 |
| AvalonEdit | GML, assembly, and text viewing | MIT |
| NAudio | Windows audio output and WAV/MP3 decoding | MIT |
| NAudio.Vorbis | OGG Vorbis decoding | MIT |
| Magick.NET | Texture decoding, image export, and room-preview composition | Apache-2.0 |

## Credits

- **sonic Fan Tech** — SplitGM project, interface, workflow, viewers, extraction systems, relationship tools, reconstructed-project system, and integration;
- **UnderminersTeam and UndertaleModTool contributors** — UndertaleModTool, UndertaleModLib, GameMaker format research, and Underanalyzer;
- **AvalonEdit contributors**;
- **NAudio and NAudio.Vorbis contributors**;
- **Magick.NET contributors**;
- everyone who tests SplitGM and submits useful compatibility and reconstruction reports.

## Disclaimer

SplitGM-VM Decompiler is an independent community project. It is not affiliated with, endorsed by, or sponsored by YoYo Games, GameMaker, Toby Fox, 8-4, or the developers and publishers of games inspected with the program.

GameMaker, UNDERTALE, DELTARUNE, and other names belong to their respective owners.
