# Parasolid preview helper — integration in progress

The helper uses only the built-in profiles shipped with parasolid-kit. Vendor
schema catalogs, local CAD installations, development models and local diagnostic
catalogs are not part of the runtime.

Pinned components:

- parasolid-kit 0.3.7: MIT AND Apache-2.0. The wheel includes the MIT text and
  `LICENSES/Apache-2.0.txt`; both must accompany redistribution.
- CPython 3.14.8 embeddable x64: the Python distribution includes `LICENSE.txt`
  covering Python and its bundled dependencies.
- cadquery-ocp-novtk 7.9.3.1.1 and cadquery-ocp-proxy 7.9.3.1.1: Apache-2.0 bindings.
- Open CASCADE Technology 7.9.3: LGPL-2.1 with the OCCT exception; dynamically
  linked libraries must remain replaceable, with the applicable licence and
  corresponding source information provided.

The OCP wheel also bundles FreeImage, FreeType, OpenEXR, Imath, libjpeg-turbo,
LittleCMS, LERC, XZ/liblzma, libpng, libwebp/libsharpyuv, OpenJPEG, OpenJPH,
LibRaw, libtiff, zlib, zstd and Microsoft runtime DLLs. The binding's Apache
licence does not replace these components' licences.

The `licenses` directory contains the dependency licence texts. FreeImage is
used under the FreeImage Public License (not its alternative GPL licence);
FreeType is used under the FreeType Project License (FTL). Portions of this
software are copyright 2022 The FreeType Project (https://freetype.org).
All rights reserved. libjpeg-turbo retains its combined IJG, BSD and zlib terms.
The helper makes use of Open CASCADE Technology software.

The runtime preparation script includes original FreeImage, LibRaw and OCCT
source archives and their build recipes in `Sources`, with pinned hashes in
`parasolid-sources.json`. Source files are not loaded by the preview worker.
See `Sources/REBUILD.md` for library replacement and build instructions.
This is a source-package requirement, not an additional runtime dependency.

Microsoft runtime DLLs retain their original valid Microsoft signatures.
Their distribution is governed by the Visual Studio licence and its
Distributable Code list, not by the open-source licences of the CAD helper:
https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution
https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/

Packaging is still under integration testing; completed source packaging
does not establish CAD compatibility or successful sandbox operation.

Upstream source and build references:

- https://github.com/monozukuri-ai/parasolid-kit
- https://github.com/CadQuery/OCP
- https://github.com/CadQuery/ocp-build-system
- https://github.com/Open-Cascade-SAS/OCCT
- https://www.python.org/downloads/release/python-3148/
