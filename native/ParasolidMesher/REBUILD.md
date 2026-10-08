# Rebuilding and replacing helper libraries

The helper uses dynamically linked OCCT 7.9.3 and LibRaw 0.21.5 libraries.
You may modify those libraries and replace them with ABI-compatible builds.
The helper does not verify the installed DLL hashes at runtime or prohibit
reverse engineering for debugging modifications to the LGPL libraries.
The LGPL texts and the OCCT header exception are in `../licenses`.

`Sources` includes the original source archives, LibRaw's CMake build sources,
the FreeImage and LibRaw conda-forge build recipes (including patches), and
the CadQuery OCP build-system sources. `parasolid-sources.json` records the
download URLs and SHA-256 hashes. The recipe script licences are retained.
FreeImage is supplied under the FreeImage Public License.

For OCCT, extract OCCT-V7_9_3.tar.gz and the OCP build-system archive. Use the
Windows OCCT build action and environment files from that build system with
Visual Studio, CMake and the listed third-party dependencies. Use the same
OCCT build options and x64 architecture as the OCP bindings.

For LibRaw, extract LibRaw-0.21.5.tar.gz and the matching LibRaw-cmake archive.
Follow `libraw/info/recipe/build.bat` and the rendered recipe's dependency
versions. For FreeImage, extract FreeImage3180.zip, apply the patches in
`freeimage/info/recipe/meta.yaml`, and use its CMakeLists.txt and bld.bat.

Close Files before replacing DLLs. They reside in
`../Lib/site-packages/cadquery_ocp_novtk.libs`. The wheel's DLL names have
delvewheel hash suffixes and its import tables reference those names.
Keep the filenames expected by the installed bindings and make the replacement
DLLs refer to the same dependency filenames. Alternatively rebuild and repair
the OCP wheel with delvewheel, then replace the OCP package and its DLL directory
together. The OCP build system documents the repair command. Its Apache-2.0
binding licence and the delvewheel MIT notice are in `../licenses`.

Rebuilding requires development tools; using the prepared preview helper does
not require a CAD installation, Python installation, or those build tools.
