# Consysto Files

A file manager for Windows 11 — a fork of [Files](https://github.com/files-community/Files) with the things a mechanical designer keeps needing: CAD drawing previews, custom collections, a terminal tab, torrents and folder synchronisation.

The project is free and open. This is an early version: it works, but rough edges are still there, and reports about them are welcome.

Русская версия этого текста — [README.md](README.md).

## What is added to Files

**Drawings and models.** DWG, DXF and Autodesk Inventor files (parts, assemblies, drawings, presentations) are shown in the preview pane without starting a CAD system. Columns carry the properties that matter: part code, material, mass, and the version of the program the file was last saved in. The same properties can be filtered on.

**Collections.** Libraries built on top of folders — books, pictures, music, drawings. Files stay where they are; only an index is built. Indexing shows its progress and the folder it is working through. Duplicates are found by sampling file contents, not by name alone.

**Books.** Covers and details from FB2, EPUB, PDF and DjVu. An OPDS client for other people's catalogues, and an OPDS server for your own collection, which any reader app on a phone can open.

**A terminal in a tab.** A real Windows console inside the window, next to the folders.

**Torrents.** Downloads run inside the program, with their own section: a list, filters, files, peers and trackers. Magnet links and `.torrent` files can be opened straight into it.

**Synchronisation and backups.** Comparison of two folders, copying of the differences, and backup plans with a preview of exactly what will be copied and what will be deleted.

**Two panes.** A header above each pane with drives and free space, swapping the panes, moving through folders in step, and comparing the two open folders.

## The portable build

Unpack the archive anywhere and run `Files.exe`. No installation, no administrator rights.

Everything the program keeps — settings, collection indexes, thumbnails — lives in a `data` folder next to it. Nothing is written to the Windows registry or to your profile: delete the folder and nothing of it remains.

What the system needs: Windows 11, or Windows 10 version 1809 or newer. Nothing has to be installed alongside; every library ships in the folder. The one exception is the terminal, which needs the WebView2 component: Windows 11 always has it, Windows 10 may not.

What the portable build does not do: replace File Explorer on Win+E, show Windows notifications, or update itself through the Store. Updating means replacing the folder — keep your `data` folder, your settings are in it.

## Building from source

Visual Studio 2022 or newer with the Windows development workload, and the .NET 10 SDK.

```powershell
build\consysto\Build-ConsystoPortable.ps1
```

The script builds the portable version and puts the folder and a `.zip` into `artifacts\ConsystoFiles`. For an installable package there is `build\consysto\Build-ConsystoFiles.ps1`, which needs a signing certificate.

## Licences and credits

The foundation is [Files](https://github.com/files-community/Files) by Files Community, under MPL-2.0 (see `LICENSE-MPL`); some parts are under MIT (`LICENSE-MIT`). My changes are released under the same licences.

This project is not affiliated with Files Community and is not supported by them. Every rough edge in this build is mine, not theirs. The name and the icon of this fork are its own; the Files name and logo are not used.

Thanks to the authors of Files and of the libraries everything rests on: ACadSharp, MonoTorrent, OpenMcdf, Win2D, CommunityToolkit.

## Feedback

Found a bug — please open an issue. It helps a lot to attach `data\Local\debug.log`, or, in the installed version, the report that Settings → About → "Save an error report" puts on your desktop.
