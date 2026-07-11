# Third-party notices

SplitGM-VM Decompiler is a GPL-3.0 application. Its source package downloads and builds against third-party projects and NuGet packages that remain under their own licenses.

## UndertaleModTool / UndertaleModLib

- Project: UndertaleModTool by the UnderminersTeam contributors
- Tested revision: `3faad3b8f33ffad03eab1baf8cb892e90f3aa9db`
- License: GNU General Public License version 3
- Use in SplitGM: GameMaker archive parsing, resource models, version detection, VM disassembly, texture decoding/export, audio-group loading, and decompiler integration.

SplitGM is distributed under GPL-3.0 to comply with this dependency.

## Underanalyzer

- Project: Underanalyzer by the UnderminersTeam contributors
- Revision: the submodule revision referenced by the tested UndertaleModTool commit
- License: Mozilla Public License 2.0
- Use in SplitGM: GameMaker VM analysis and high-level GML reconstruction.

## AvalonEdit

- Package: `AvalonEdit` 6.3.1.120
- License: MIT
- Use in SplitGM: read-only GML, VM assembly, and text-resource display.

## NAudio

- Package: `NAudio` 2.3.0
- License: MIT
- Use in SplitGM: Windows audio output and WAV/MP3 decoding.

## NAudio.Vorbis

- Package: `NAudio.Vorbis` 1.5.0
- License: MIT
- Use in SplitGM: OGG Vorbis decoding through NAudio.

## Magick.NET

- Package: `Magick.NET-Q8-AnyCPU` 14.14.0
- License: Apache License 2.0
- Use in SplitGM: texture decoding, PNG creation, bounded room preview composition, and image transformations. UndertaleModLib also depends on Magick.NET.

The complete license text and notices for each dependency are available in its source repository and NuGet package. This notice does not replace those licenses.
