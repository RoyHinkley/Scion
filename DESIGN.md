# Scion Design

## Purpose

Scion protects files within selected folders by maintaining recoverable copies with very low routine overhead.

Scion is deliberately narrower than a conventional backup system. It does not provide disk images, bare-metal restoration, synchronization, cloud storage, or historical version browsing. Its purpose is to make current user files readily recoverable.

## Terminology

### Protected folders

Files anywhere within these folders are protected.

### Scions

Temporary copies of recently changed files, preserved in their original folder structure below the protected drive root.

### Recovery tree

The durable tree of merged scions containing the recoverable files.

### Capture

Identifies recently changed files and saves them into scions.

### Merge

Advances the recovery tree by merging recently captured scions.

## Capture configuration model

Each scion-capture configuration represents exactly one NTFS volume and therefore one USN Journal checkpoint.

`ProtectedDrive` identifies that volume. `[ProtectedFolders]` contains full folder paths from the volume root with the drive designation omitted. `ScionFolder` identifies where completed scions are published.

A file qualifies when its resolved path lies beneath any protected folder. Once qualified, mapping does not depend on which protected folder matched: its complete path relative to the volume root is preserved. For example:

```text
C:\Users\Jim\Documents\Letter.docx
    -> scion Users\Jim\Documents\Letter.docx
    -> E:\Recovery\Users\Jim\Documents\Letter.docx
```

This makes overlapping protected folders harmless. Capture nevertheless normalizes `[ProtectedFolders]` by removing duplicates and folders already covered by a parent protected folder, then sorting the list.

Capture also removes any protected folder that contains `ScionFolder`. This prevents recursive capture of Scion's own output. Only `[ProtectedFolders]` is rewritten during normalization; the remainder of the configuration file is preserved.

Multiple protected drives are handled by multiple capture configurations and runs rather than by teaching one capture instance to manage multiple journals. `--config <ini-file>` selects an alternate INI file; its state and log files use the same base path.

## Default protected folders

When a capture configuration is absent, Scion creates one for the Windows system drive. It queries Windows for these Known Folders and includes those physically residing on the protected drive:

```text
Desktop
Documents
Pictures
Music
Videos
Downloads
```

If `\Data` exists on the protected drive, it is also included.

`AppData` and `ProgramData` are not included by default. They contain substantial application, cache, and reproducible state, although selected subfolders may contain important user-specific data. Users should add such application folders explicitly when appropriate.

`ScionFolder` defaults to `\Scion` on the protected drive.

## Capture strategy

scion-capture maintains a durable checkpoint representing the most recent successfully captured journal position.

Normally, changes are identified from the NTFS USN Journal. Journal records are reduced to actionable file candidates, paths are resolved, and only candidates beneath a protected folder are copied.

If the checkpoint no longer lies within retained journal history, Scion performs a recovery scan of every protected folder for files modified since the previous checkpoint. Before scanning it establishes a fresh journal position; after the scan it reads forward from that position. This closes the interval during which the recovery scan was running.

Every copied file is verified using SHA-256. A scion is constructed under a temporary unrecognized directory and published by rename only when construction succeeds. Empty scions are not published.

## Scion structure

Scions are immutable after publication. Paths relative to the protected volume root appear directly beneath the scion root:

```text
Scion
└── scion_2026-08-11_14-00-00
    ├── Data
    ├── Users
    │   └── Jim
    │       └── Documents
    │           └── Letter.docx
    └── topaz.confirmed (not merged)
```

Captured file changes are always stored in folders beneath the scion folder. Top-level files in the scion folder are not are not merged. A scion remains available until the configured number of collectors have created confirmation files in its root.

## Merge strategy

scion-merge reads one or more `[ScionFolders]` and advances a `[RecoveryTree]`. Scions are processed oldest first. Each top-level folder in a scion is duplicated beneath the recovery-tree root, and all files below those folders retain their scion-relative paths. Top-level files in the scion root are ignored.

For an existing destination file, modification time determines whether the incoming file replaces it. Newer wins; an equal timestamp retains the existing file. Replacement uses a verified temporary destination file followed by atomic rename, so a failed copy does not destroy a valid recovery copy.

After every file in a scion has been copied or safely skipped, the collector creates its confirmation file. Any failure prevents confirmation and permits safe retry.

## Capture and merge cadence

Capture and merge intentionally have independent schedules. Capture can run frequently because journal processing scales primarily with the amount of change rather than the total protected tree.

Merge can run less frequently. A useful policy is frequent multiple daily captures with a single daily merge performed when the system is likely to be inactive. If only one capture is performed per merge, then perform the merge first. The recovery tree then retains a somewhat older durable state while newer changes remain available in recent scions. This provides a convenient tolerance for recently discovered mistakes without implementing general version history.

## Recovery-tree placement

A recovery tree should reside on a second physical drive. If no second suitable drive exists, the default merge configuration falls back to the protected/system drive rather than refusing to operate. While same-drive placement provides protection against many file-level losses, it cannot protect against loss or failure of the drive.

A recovery tree must not be protected by a capture configuration whose scions merge back into that same tree, because that creates a feedback path. It may legitimately be protected by a separate configuration whose scions merge to another recovery tree.

## Operating rules

1. Live files are authoritative; Scion does not modify them during normal operation.
2. Published scions are immutable.
3. The recovery tree contains one recoverable copy for each known path/file, not version history.
4. Newest modification time wins during merge. File sizes and hashes are not used. Ties go to the Recovery Tree.
5. Copies are cryptographically verified before scion publication or replacement in the recovery tree.
6. Operations are idempotent and safe to retry.
7. Copy failures do not destroy an existing valid recovery file.
8. ScionFolder must not lie within a folder protected by its own capture configuration.

## Rationale

### Why use the NTFS USN Journal?

The journal identifies files changed since the previous capture. Routine work therefore scales mainly with actual filesystem change instead of requiring repeated traversal of every protected file.

### Why separate capture and merge?

Capture places recent changes into usable temporary scions without requiring the recovery tree to be updated immediately. Merge is left free to operate on a different schedule and, in LAN deployments, on a different machine.

### Why preserve volume-relative paths?

The original folder structure remains recognizable and recoverable without requiring per-source mapping rules. Protected folders determine inclusion only; they do not alter destination paths.

### Why no version history?

Scion's goal is practical recovery of current files. Separating frequent capture from less-frequent merge provides a modest recent-change buffer without introducing generations, retention policies, dependency chains, or version-selection interfaces.

### Why no automatic archive function?

Whether an old, unchanged file remains valuable is subjective. Age or inactivity alone cannot safely determine that a file should be removed from the recovery tree. Therefore, backup and pruning operations on the recovery tree, although recommended, are deliberately left outside the scope of the capture and merge process.

## Manual deployment

The initial deployment model is intentionally manual. Users place the executables, generate and review configurations, and create Windows Task Scheduler jobs. This keeps the current software scope small while providing a concrete specification for a future setup program.
