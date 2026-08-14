# <img src="artwork/scion.svg" alt="scion logo" height="24px" width="auto"> Scion

**scion** ("sigh-uhn") - A small twig or bud taken from a plant to be grafted onto another plant root.

Scion provides lightweight Windows file protection that captures changes and maintains a recovery tree. It guards against file loss, accidental deletions, and overwrites, while imposing very little routine system load. It uses the NTFS USN Journal to identify changed files, captures those files into temporary scions, which are later merged into a durable **recovery tree**.

Scion maintains ordinary files in ordinary folders. It is intended for file recovery, not disk imaging, system restoration, synchronization, or version-history browsing.

See `DESIGN.md` for the system design and rationale.

## Installation

1. Copy the executables. Put `scion-capture.exe` in a program folder (e.g., `\Programs\Scion`) on each computer to be **protected**. Copy `scion-merge.exe` to each **collector** (computers that will merge the captured scions into a recovery tree). When only a single computer is to be protected, both programs can go in the same folder.

2. Run each executable once to generate a default configuration file. Review and edit the generated `.ini` files according to your needs.

3. Create Windows Task Scheduler jobs for the capture and merge operations. Capture requires administrative access to the NTFS journal.

## Special Terms
- **Protected** - Describes file storage monitored by Scion for changes. Changes to files anywhere beneath a protected folder are detected and the altered files are copied into scions. Protected computers are those that periodically run `scion-capture`. Each protected drive has an associated configuration file in which its protected folders are identified. Protected files are those within or beneath protected folders.

- **Scions** - Temporary copies of recently changed files, preserved in their original folder structure.

- **Recovery tree** - The durable tree of merged scions containing all of the recoverable files.

- **Capture** - The `scion-capture` program runs on protected computers to identify recently changed files and save copies of them into scions.

- **Merge** - The `scion-merge` program merges scions from protected computers into a durable recovery tree.

- **Collector** - A computer that uses `scion-merge` to maintain a recovery tree.

## Building
The `Scion` 'solution' contains three Visual Studio projects for building two programs:

- **Scion.Capture**: Source code for the `scion-capture` program, which identifies recently changed files and saves them into scions.
- **Scion.Merge**: The `scion-merge` program, which advances a recovery tree by merging recently captured scions.
- **Scion.Common** A small custom library with shared naming, configuration, logging, path, hashing, and verified-copy mechanisms.

Open `Scion.sln` in Visual Studio 2022 or later with the .NET 9 SDK installed to build, customize, debug, and publish the `scion-capture` and `scion-merge` programs.

If dotnet is installed (automatically with Visual Studio), you can build the programs from a Command Prompt at the Scion solution folder with:
```text
dotnet build Scion.sln
```
*Building* generates "thin" versions of the programs in the Debug or Release folders, along with the all of the required dependencies (dlls, etc.).

To make portable, single-file executables instead, run:
```text
dotnet publish
```
This generates the two standalone executables, placing them in the `Scion\Publish` folder. You can also publish from within Visual Studio by right-clicking on each program project in Solution Explorer and selecting "Publish..."

## scion-capture

When scion-capture is run with no command-line configuration specified, it assumes these default companion files:

```text
scion-capture.ini
scion-capture.state
scion-capture.log
```

The `.ini` file contains the configuration for a protected drive. At the end of each run, the program records a summary of the operation in the `.log` file. The program also maintains a `.state` file with checkpoint information, so it can later pick up where it left off.

An alternate configuration (for a different drive) can be specified on the command line:

```text
scion-capture --config driveE.ini
```

The state and log files share the same base path (`driveE.state` and `driveE.log`). Each protected NTFS drive requires its own configuration.

On first execution, scion-capture creates any missing configuration and log files. The default `ProtectedDrive` is the Windows system drive. The generated protected-folder list contains Desktop, Documents, Pictures, Music, Videos, and Downloads when those Windows **Known Folders** reside on the protected drive. If a `\Data` folder exists on the drive, it is included as well. `ScionFolder` defaults to `\Scion` on the protected drive.

Example:

```ini
ProtectedDrive=C:
ScionFolder=C:\Scion

# Full paths from the protected drive root, without the drive designation.
[ProtectedFolders]
\Data
\Users\Jim\Desktop
\Users\Jim\Documents
\Users\Jim\Downloads
\Users\Jim\Music
\Users\Jim\Pictures
\Users\Jim\Videos

[Settings]
ConfirmationsRequired=1
stdout=normal
log=normal
```

Files anywhere beneath a protected folder are eligible for capture. The complete folder structure below the protected drive root is preserved. Thus `C:\Users\Jim\Documents\Letter.docx` is stored in a scion as `Users\Jim\Documents\Letter.docx` and, for example, ultimately appears as `E:\ScionRecovery\Users\Jim\Documents\Letter.docx` when `RecoveryTree` is `E:\ScionRecovery`.

Set ConfirmationsRequired to the number of **collectors** that merge the protected files into their recovery trees. Maintaining multiple, separate recovery options on other drives and computers significantly reduces the probability of an unrecoverable loss.

### Protected-folder normalization

Whenever capture loads its configuration, it normalizes only the `[ProtectedFolders]` section of the `.ini` file. It removes duplicate entries, removes folders already covered by another protected folder, removes any folder that would cause `ScionFolder` itself to be protected, and sorts the resulting list. If normalization changes the list, that section is rewritten. Other configuration content is left unchanged.

`ScionFolder`, or any folder containing it, must not be protected by the same capture configuration; otherwise Scion would capture its own output.

Scion does not protect `AppData` or `ProgramData` by default. Some applications store valuable user-specific information there, so users should add appropriate application folders when needed rather than protecting those entire trees indiscriminately.

Normal operation uses the NTFS USN Journal. If the stored checkpoint has expired, Scion scans each protected folder for files modified since the previous checkpoint and then performs an immediate journal catch-up pass so changes occurring during the scan are not missed.

## scion-merge

Companion files:

```text
scion-merge.ini
scion-merge.log
```

Example:

```ini
[ScionFolders]
C:\Scion

[RecoveryTree]
E:\Recovery

[Settings]
CollectorName=topaz
stdout=normal
log=normal
```

On first execution, merge chooses a second NTFS drive for the `RecoveryTree`, if one is available; otherwise it falls back to `\ScionRecovery` on the Windows system drive. A same-drive recovery tree provides some protection against many accidental deletions, overwrites, and similar mistakes, but it does not provide protection against failure or loss of the drive.

Scion folders are processed in listed order. LAN shares (like '\\\ServerA\Data') are supported, enabling multiple protected computers to be merged into a single recovery tree. This is particularly convenient when LAN computers share a common local data storage convention. Scions are processed oldest first. Each top-level folder within a scion is duplicated beneath the recovery-tree root, preserving the path below the protected drive's root. Only files contained within or beneath folders under the scion folder are merged; top-level files in the scion folder, such as collector confirmation files, are ignored. When merging, the newest modification timestamp wins. Exact timestamp ties retain the file already present.

## Exit codes

Both programs use:

- `0` — successful run
- `1` — fatal startup, configuration, or program error
- `2` — one or more recoverable source or destination failures; completed work was retained and may be retried

## License

Scion is free software licensed under the GNU General Public License, version 3 (GPLv3). See LICENSE for the full license text.