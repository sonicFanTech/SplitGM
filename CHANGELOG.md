# Changelog

## v0.5.1.0 Startup Display Settings Update

- Added a dedicated **Startup** tab to the main SplitGM Settings window.
- Added five required-launcher display modes: Normal, First Half, Second Half, First Half Static, and Second Half Static.
- Kept `SGMVMDLauncher.exe` mandatory; the setting changes only which part of the embedded sequence is shown.
- Added ranged embedded-frame playback so the launcher can play frames 1–180 or 185–412 without extracting or duplicating frame files.
- Added static-frame display using frame 145 for the completed logo/title and frame 385 for the completed splash image.
- Static modes remain visible for at least two seconds and stay on screen while SplitGM finishes opening.
- Stored the selection in `SplitGM_Settings.ini` under `[Startup] Mode=...`; malformed or missing values safely fall back to Normal.
- Space, Enter, and Escape continue to skip animated playback and now also skip the minimum static-frame hold.

## v0.5.1.0 Runtime Hotfix 3

- Fixed reconstructed-project exports appearing frozen on large games even when Windows did not report Not Responding.
- Replaced per-resource `Progress<T>` Dispatcher posting with a UMT-style 33 ms progress pump that coalesces aggregate state, resource rows, previews, and logs.
- Moved the entire reconstruction pipeline, including synchronous setup work, onto a worker task.
- Added bounded catalog loading, mutable row updates, recycling row/column virtualization, deferred scrolling, and throttled selection/scroll behavior.
- Limited progress-preview readback to at most four images per second instead of reading every exported PNG into memory.
- Reworked sprite export around UMT-style texture-page groups: one short-lived `TextureWorker` per connected page group and outer parallelism of approximately `ProcessorCount / 4`.
- Preserved deterministic project/resource merge order, every resource catalog entry, cancellation, generated schemas, repair behavior, and validation behavior.
- Includes Build Hotfix 1 and Build Hotfix 2.

## v0.5.1.0 Build Hotfix 2

- Fixed `CS1503` in `AudioWaveformControl.cs`: `VisualTreeHelper.GetDpi` requires a `System.Windows.Media.Visual`, but the null fallback was a plain `DependencyObject`.
- The waveform text renderer now reads DPI from the current main-window `Visual` when available and uses `1.0` pixels-per-DIP during early startup or design-time rendering.
- Includes the `GlobalDecompileContext` namespace fix from Build Hotfix 1.
- No waveform sample data, decompiler behavior, reconstructed-project format, repair logic, or room-viewer behavior was changed.

## v0.5.1.0 Build Hotfix 1

- Fixed `CS0246` in `UmtNativePipeline.cs` by importing `UndertaleModLib.Decompiler`, the namespace that contains UMT's `GlobalDecompileContext`.
- Restored compilation of the shared UMT/Underanalyzer decompilation pipeline.
- No decompiler behavior, reconstructed-project format, repair logic, waveform output, or room-viewer behavior was changed.

## v0.5.1.0

- Replaced the static main-executable splash with the required `SGMVMDLauncher.exe` animated startup launcher.
- Embedded all 412 JPEG frames from the trailer ending into the launcher and preserved the source 24 FPS timing without video playback or an external frame folder.
- Added the `SGMVMDLauncher.Playback` WPF playback project, which decodes one display-sized frame at a time and corrects timing with a `Stopwatch`.
- Added a one-time current-user named-pipe handshake between the launcher and `SplitGM-VM-Decompiler.exe`; direct main-executable startup is now blocked with a clear error.
- The launcher waits for the main window's READY signal before closing and supports Space, Enter, or Escape to skip the animation.
- Replaced the old application/program artwork and executable icon with the supplied blue-and-orange cube SplitGM logo.
- Updated release publishing so `SGMVMDLauncher.exe` is published as a single-file launcher with its frame resources bundled inside it.

- Added the automatic reconstructed-project repair engine with preserved pre-repair output, per-change confidence/evidence, JSON/text repair reports, unresolved-function reports, and manual repair steps.
- Added one global case-insensitive reconstructed asset namespace, safe/duplicate name allocation, exact GML reference rewrites, and JSON resource-path/name repair.
- Registered recovered `GlobalInit` and `GlobalScript` entries as modern `GMScript` resources.
- Added conservative GML repairs for placeholder/duplicate enum names, duplicate identifiers in the same local declaration, and recovered functions that require optional `argumentN` parameters.
- Added missing script, object-event, room-creation, and instance-creation code-file checks with explicitly marked structural placeholders.
- Added sprite canvas, collision-bound, origin, frame, sequence-length, playback, and default-field checks.
- Added `.yy`, `.yyp`, folder, resource-order, resource-reference, and target-version normalization.
- Added static compile-preflight validation for JSON, GML delimiter balance, names, duplicates, missing files, and broken references.
- Centralized direct UndertaleModTool loading, decompilation, and disassembly calls in `UmtNativePipeline` and added shared parallel bulk decompilation for normal and reconstructed exports.
- Added actual-sample audio waveform analysis and a WPF waveform viewer for sounds and embedded audio.
- Added SVG/JSON waveform sidecars to selected, category, audio-group, and reconstructed sound exports.
- Added a dedicated read-only UMT-style room viewer with nearest-neighbor display, zoom, fit, 100%, Ctrl+wheel zoom, grid, and organized room tables.
- Fixed a duplicate `xorigin` key in reconstructed sprite JSON generation.

## v0.5.0 Build Hotfix 1

- Fixed `CS0103` in `GameProjectSession.Reconstruction.cs` by importing `UndertaleModLib.Util`, the namespace that contains UMT's `TextureWorker`.
- Restored compilation of reconstructed sprite collision-mask export through `TextureWorker.ExportCollisionMaskPNG`.
- No project-format or reconstruction behavior was changed.

## v0.5.0

- Added **Tools > Decompile to Reconstructed .yyp Project...**.
- Added experimental modern GameMaker `.yyp` generation as a transparent repair workspace rather than an identical source-project claim.
- Added stable `.splitgmproj` format version 1.0 with source/target metadata, deterministic stable IDs, resource records, relationships, messages, output files, and validation state.
- Reconstructs scripts, sprites, sounds, paths, audio groups, objects, rooms, object-event GML, room-creation GML, and room instance-creation GML where the compiled data can be represented safely.
- Preserves object sprite, parent, collision-mask, collision-event, room-instance/object, room-view-follow, and sound/audio-group relationships.
- Preserves normal sprite frame/playback/collision settings, object physics settings and vertices, room views, room physics, and room background color when they can be represented safely.
- Added `__SplitGM_Metadata` and `__SplitGM_Unrepresented` output folders for inspectable metadata, previews, raw recovery data, assembly fallbacks, and unsupported project resources.
- Added JSON/text validation reports and a repair-workspace README to every reconstructed project.
- Added a dedicated large reconstruction progress window showing every exported resource, status, output path, live messages, image previews, and non-audio text/detail previews; audio is exported without a preview.
- Added safe output-folder markers so non-empty unrelated folders cannot be overwritten by the reconstructed-project exporter.
- Integrated the supplied SplitGM logo and v0.5.0 splash artwork into the executable, main window, About window, and progress interfaces.
- Updated product, assembly, setup, and release versions to 0.5.0.

## v0.4.0

- Replaced the large top action row with a standard File/Edit/Export/View/Tools/Help menu bar.
- Removed the remaining header Copy and Export buttons; those actions now live in the menu bar.
- Added grouped Export-menu commands for exporting every resource in a chosen category.
- Added `SplitGM_Settings.ini` and a complete Settings window.
- Replaced the About message box with a dedicated resizable About window.
- Added detailed cancellable progress windows for loading, all export paths, and relationship analysis.
- Added the Relationships workspace with callers/callees, global variables, static room transitions, object inheritance, room/object links, asset references, and heuristic unused candidates.
- Added connected-code navigation for object resources, object events, room instances, and relationship results.
- Added a connected-code selection dialog for room instances and objects with multiple event entries.
- Fixed GMS1/GMS2 room backgrounds by initializing UMT room parent links, using unpadded texture regions, rendering color layers, and supporting tiling/stretch/offsets.
- Added read-only GMS2 tile-layer rendering, including tile transform flags, output borders, and room-layer offsets.
- Kept SplitGM GUI-only; no separate CLI project was reintroduced.
- Updated product, assembly, setup, and release versions to 0.4.0.

## v0.3.1

- Fixed the GUI startup crash caused by unsupported `escapeCharacter` attributes in the custom AvalonEdit XSHD definition.
- Made syntax highlighting optional and failure-safe so a highlighting-definition problem can no longer stop SplitGM from opening.
- Replaced WPF `StartupUri` creation with guarded manual startup so the real inner exception is preserved.
- Added detailed crash reports, deepest-error text in the dialog, and an option to open the crash report immediately.
- Moved preferred crash reports to `Logs\CrashReports` beside the application, with LocalAppData fallback.
- Removed the separate CLI project and CLI release output. SplitGM is now GUI-only.


## v0.3.1 Build Hotfix 1

- Fixed four `CS0117` build errors caused by using the nonexistent Magick.NET enum value `PixelInterpolateMethod.NearestNeighbor`.
- Updated room-preview scaling to use the valid `PixelInterpolateMethod.Nearest` value.
- Fixed the nullable `CS8600` warning in audio-group path resolution.
- No feature behavior or project format was changed.

## v0.3.1

- Expanded the Resource Explorer into a read-only full resource viewer.
- Added sprite frame preview and playback.
- Added object sprite preview and event/code mapping.
- Added bounded room raster previews plus layer, instance, and tile tables.
- Added background, font-atlas, and embedded texture-page previews.
- Added WAV, OGG Vorbis, and MP3 audio playback.
- Added selected-resource export, including sprite collision masks and recoverable Spine JSON/atlas text.
- Added complete selected audio-group export.
- Added full recoverable resource extraction to project export.
- Added resource extraction totals and failures to the manifest.
- Added per-resource error continuation.
- Replaced RichTextBox token rendering with AvalonEdit.
- Added background loading states, automatic highlighting limits for huge documents, smaller code caches, and no caching for multi-megabyte code entries.
- Added paged resource/code tree groups so huge categories do not create every WPF node at once.
- Moved disposal of a still-busy old game session off the UI thread when closing or replacing a game.
- Added texture-page and embedded-audio resource categories.

## v0.2.1

- Fixed WPF temporary-project global using and nullable compilation errors.
- Fixed release build log encoding.

## v0.2.0

- Added the WPF Resource Explorer.
- Added GML and VM assembly views.
- Added code search, compatibility reports, indexes, and organized project export.

## v0.1.2

- First working prototype tested with UNDERTALE and DELTARUNE.
- Fixed dependency project build ordering.

## v0.5.0 Performance Hotfix 2

- Reworked reconstructed-project resource export to use bounded parallel workers.
- Adopted UndertaleModTool's texture-export strategy: reuse decoded texture pages through a shared `TextureWorker` cache while limiting image concurrency to avoid excessive memory use.
- Removed the expensive pre-export preview pass. The progress window now uses the PNG that was actually exported, avoiding a second texture decode/crop for every visual resource.
- Kept deterministic `.splitgmproj`, `.yyp`, relationship, and resource ordering by merging parallel results in source order.
- Made fallback sprite collection safe during concurrent exports by collecting files from each resource's stable export prefix.
- Added bounded parallelism to normal category-wide resource exports, not only reconstructed `.yyp` export.
- Each standalone resource export now owns its own short-lived `TextureWorker`, making category exports thread-safe.
