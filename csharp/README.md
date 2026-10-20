# Stash C#

A C# Windows Forms port of the Python Stash image-folder organizer.

## Requirements

- Visual Studio 2022 with the .NET desktop development workload
- .NET Framework 4.8 Developer Pack / targeting pack

This project targets .NET Framework 4.8 to match the Visual Studio 2022 installation available when the project was created. The Python Stash repository remains the behavior reference.

## Open

Open Stash.sln in Visual Studio 2022 and run Stash.WinForms.

## Behavior carried over

- Recursively scans folders beneath a selected root for direct image files.
- Matches folders containing 1 through the configured threshold (default 10).
- Keeps each source folder together when planning output batches (default target 500).
- Uses natural sorting and Stash global/source numbering.
- Continues output-folder and image numbering from existing output.
- Reports existing-output integrity warnings without repairing anything.
- Preflights all destinations and never overwrites an existing file.
- Rejects output paths containing Windows junctions, symbolic links or other reparse points, including linked ancestors. Checks again before copying and source cleanup.
- Copies and verifies every image before any optional source cleanup.
- Deletes a source folder only when it contains exactly the planned files and those files remain unchanged after copy.
- Shows a scan preview in the activity log and asks for confirmation before copying.
## Safety checks

Build and run the Stash.SafetyChecks project in Visual Studio, or run its generated executable from Stash.SafetyChecks\bin\Debug\net48. The checks use isolated temporary folders and do not touch personal image collections.
## Debug build for desktop testing

Build with Visual Studio 2022 using the Debug configuration, or run:

```powershell
dotnet msbuild Stash.sln /restore /t:Build /p:Configuration=Debug
```

Run `Stash.WinForms\bin\Debug\net48\Stash.exe`. The desktop requires .NET Framework 4.8. When copying a build to another PC, copy the complete output folder, including `Stash.exe.config` and `Stash.pdb`, or extract the supplied Debug ZIP first. The PDB contains debugging symbols.

The activity log has a draggable divider above it. Drag upward to enlarge the log; the settings can scroll while the action buttons remain visible.
