# Third-party notices

SplitGM-VM Decompiler is a GPL-3.0 application. Its source package downloads and builds against third-party projects and NuGet packages that remain under their own licenses.

## UndertaleModTool / UndertaleModLib

- Project: UndertaleModTool by the UnderminersTeam contributors
- Tested revision: `2b6fe69722cec25219f1ae21f8111907c2a15629`
- License: GNU General Public License version 3
- Use in SplitGM: exact `UndertaleIO.Read` loading path, GameMaker resource models and version detection, `UndertaleCode.Disassemble`, cached `TextureWorker` texture recovery/export, audio-group loading, room setup/layout behavior, and decompiler integration.
- v0.5.1.0 note: `UmtNativePipeline`, the room raster path, and the read-only room-viewer behavior directly call or adapt GPL-3.0 UMT APIs/algorithms. SplitGM removes editor mutation/save-back behavior and retains GPL-3.0 licensing and source availability.

SplitGM is distributed under GPL-3.0 to comply with this dependency.

## Underanalyzer

- Project: Underanalyzer by the UnderminersTeam contributors
- Revision: the submodule revision referenced by the tested UndertaleModTool commit
- License: Mozilla Public License 2.0
- Use in SplitGM: shared `GlobalDecompileContext`, per-entry `DecompileContext`, parallel GameMaker VM analysis, and high-level GML reconstruction matching UMT's decompiler path.

## AvalonEdit

- Package: `AvalonEdit` 6.3.1.120
- License: MIT
- Use in SplitGM: read-only GML, VM assembly, and text-resource display.

## NAudio

- Package: `NAudio` 2.3.0
- License: MIT
- Use in SplitGM: Windows audio output, WAV/MP3 decoding, and actual-sample waveform analysis.

## NAudio.Vorbis

- Package: `NAudio.Vorbis` 1.5.0
- License: MIT
- Use in SplitGM: OGG Vorbis decoding through NAudio.

## Magick.NET

- Package: `Magick.NET-Q8-AnyCPU` 14.15.0
- License: Apache License 2.0
- Use in SplitGM: texture decoding, PNG creation, bounded room preview composition, and image transformations. UndertaleModLib also depends on Magick.NET.

The complete license text and notices for each dependency are available in its source repository and NuGet package. This notice does not replace those licenses.
