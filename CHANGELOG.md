# Changelog

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