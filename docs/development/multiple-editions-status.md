# Multiple editions — Phase 3 file handling and manual imports

Date: 11 September 2026

Branch: `Multiple-Editions`, continued from `855e517b3`. Phase 2 and Phase 3 changes remain uncommitted. The interrupted 10 September session was recovered from its session record, working tree and test artifacts.

## Phase 2 prerequisite

The Phase 2 storage/lifecycle gate passed on 10 September: **168 passed, 1 skipped, 0 failed on each of SQLite and PostgreSQL**, plus disposable application startup and housekeeping checks. The earlier report is retained below as historical context; its then-pending filesystem work is superseded by this Phase 3 report.

## Phase 3 implementation

- Manual import command and reprocess payloads accept `movieEditionId`; manual-import and movie-file resources expose the assigned ID. A context carries the selected edition, profile and current file without substituting the movie's primary projection. A multi-edition movie requires an explicit target unless the existing source path already has a unique owner.
- Imports validate target membership, source ownership and the latest current-file snapshot before replacement. A successful import suppresses only another candidate for the same edition. Repository insertion commits ownership and the edition's current-file pointer transactionally, updating the legacy projection only for the primary edition.
- Upgrades recycle only the selected edition's current file and associated extras. Wrong-owner sources, stale contexts, ambiguous targets and destination collisions are rejected before recycling. Existing-file reprocessing cannot replace a different edition or path and also checks shared basenames across extensions.
- Imports, scans, renames and folder moves share a process-local movie-folder lock. Scanning retains assigned secondary files and leaves ambiguous additional files on disk for manual review. Rename previews and execution validate all selected destinations before moving files.
- `{Managed Edition}` expands to the managed name through the existing filename sanitization. The parsed edition token keeps its meaning. Shared basenames are rejected even across video extensions because their sidecars would collide.
- Subtitle and other sidecar handling uses the actual file owner. Ambiguous extras remain unassigned. File-specific metadata cannot overwrite another file's recorded metadata destination, including destinations written by another provider; shared metadata generation is restricted to the primary edition.
- Download decision and submission guards keep automatic multi-edition acquisition disabled. Custom import scripts are rejected for multi-edition movies until their recovery contract is edition-aware.

## Phase 3 verification

- .NET SDK 8.0.421: final Linux console application build passed with **zero warnings and zero errors**, in Release with `EnableAnalyzers=false` and `RuntimeIdentifiers=linux-x64`.
- SQLite and PostgreSQL 16.15 final focused regressions: **766 passed, 4 skipped, 0 failed on each backend**. Skips are existing platform-specific cases.
- Tests cover actual migration/repository/housekeeping operations, explicit manual imports, independent upgrades, stale-context rejection, ambiguous targets, source ownership, import/rename/shared-basename collisions, file-specific extras, metadata ownership across providers and interrupted transfers before and after copying the replacement.
- Actual SQLite application/API smoke test passed using real generated videos and subtitles: Theatrical / IMAX and Director's Cut / 3D imports, rescan, rename, process restart, housekeeping, both independent upgrades, a second process restart and rescan. Assertions compare the untouched edition's video/subtitle bytes and database IDs and verify combined attributes and the legacy primary API projection. This is a real process restart, in addition to the fixture's repository reload check.
- Runtime checks use the application's real services, dependency injection, disk transfers and ffprobe. PostgreSQL lifecycle behavior is covered by the database-backed service fixtures; the Phase 3 full API/restart smoke test uses SQLite.
- Artifacts: `_temp/edition-test-results/phase3-sqlite.trx`, `phase3-postgres.trx`, their `.log` files, `phase3-app-build.log`, `phase3-runtime.log` and `phase3-runtime-results.log`. All are ignored build artifacts. `git diff --check` passed.
- Disposable runner, PostgreSQL container and both test networks were removed after verification. The runtime script removed its temporary configuration and synthetic library.

The focused filter extends Phase 2 with `EditionFileHandlingFixture`, `MovieImport`, `RenameMovieFileServiceFixture`, `UpgradeMediaFileServiceFixture`, `DiskScanServiceTests`, `OrganizerTests`, `Extras` and `DownloadServiceFixture`.

Runtime reproduction: `python3 /src/docs/development/verify-phase3.py` inside a disposable SDK container with the checkout mounted at `/src`, built application, Python 3, ffmpeg/ffprobe and external networking disabled. The script creates and removes a unique `/tmp/radarr-phase3-*` configuration/library. Movie and edition definitions are seeded offline because edition CRUD is deferred; manual imports, scanning, naming configuration, renaming and housekeeping use the real running API. The tiny videos are generated locally and are not movie content. Every release group is synthetic.

## Recovery limits and remaining work

- Filesystem operations and database writes are not atomic. A failed replacement can leave the selected edition missing after recycling its old file. Tests cover failures before transfer and after copying the replacement: the other edition survives, the source and recycled files remain available, and an unrecorded destination remains on disk for review. Automatic rollback, crash journaling and exhaustive failures around database commit/sidecar updates remain unimplemented. Recycle recovery depends on configuring a recycle bin; no production safety claim is made for permanently deleted files.
- Folder locking is process-local; multiple Radarr processes sharing one library are not protected by it. Broad concurrency, folder-move failures, all metadata-provider combinations and production-scale performance remain unverified.
- Explicit assignments preserve combined Theatrical / IMAX and Director's Cut / 3D attributes. Automatic combination-aware classification, aliases, custom-rule ambiguity/timeouts and additional-legacy-file review are later work. An absent cut still defaults to Theatrical in foundational creation; this phase does not claim automatic filename routing for 3D/IMAX.
- Edition CRUD/selector UI, classification-aware acquisition, queue/pending/history recovery, aggregate wanted/statistics, notifications/custom scripts and backup/restore integration remain deferred. Do not enable multi-edition acquisition on the strength of the manual-import gate.
- The full application/UI suite and analyzers were not run. No production Radarr configuration or media library was accessed. Nothing has been committed or pushed. The pre-existing deleted archive fixtures and untracked `dockerfile` are preserved.

---

# Historical Phase 2 report (10 September 2026)

The following describes the verified state before Phase 3 implementation, including the gaps outstanding at that time.

## Implemented

- Migration 243 retains the storage groundwork: one default edition per existing movie, preserved recorded labels/settings, nullable file ownership, open attributes/rules, and no guesses about additional legacy files.
- Movie insertion and bulk insertion now create the default edition and primary projection in the same transaction. An empty movie receives Theatrical; recorded labels remain intact when recovering legacy data.
- Legacy profile and search updates synchronize the primary edition, including bulk updates and partial field updates. Other edition profiles remain independent. Movie monitoring remains the master switch.
- Ordinary movie updates retain the stored primary/file selection instead of replaying stale pointers. Movie queries join the selected primary file, including title searches and list queries; an available secondary file does not make the legacy primary `hasFile` true.
- Legacy file insertion commits the file, edition ownership and primary projection together. Assignment errors propagate to the caller instead of being swallowed by event handling. This path accepts only the primary target; multi-edition import remains disabled.
- File metadata updates preserve ownership and reject moving an owned file to another movie. Current-file deletion clears the matching edition and legacy pointers transactionally; deleting a secondary or superseded file preserves the other current file.
- Movie deletion removes its editions and releases retained file ownership. Housekeeping repairs missing/cross-movie edition ownership, invalid current-file pointers and missing primary selections without deleting recoverable parent-owned files. Repeated repair is idempotent.
- Primary selection and explicit file assignment share parent-row locking and transactional validation. A stale expected current-file value rolls back the claim. Edition deletion rejects primary targets and targets that still own files; ordinary edition updates cannot bypass current-file ownership validation.
- Repository transaction hooks cover single, bulk and partial updates. Unrelated repositories retain their existing single-write and bulk behavior.
- Fixed a compile failure in the groundwork test namespace, which shadowed `NzbDrone.Core.Movies` in existing indexer fixtures.

## Classification and safety gates

Theatrical remains the default for an absent cut. Recorded 3D/IMAX/custom labels and the open attribute dictionary are preserved; combined attributes and extensible keys round-trip through storage. The combination-aware classifier is still pending. Storage migration/repair does not infer 2D, standard framing, or a cut from filenames, and existing file descriptions are not rewritten into managed classifications.

No edition-management UI/API or multi-edition acquisition is exposed. The validated repository operations are storage primitives, not proof that two editions can safely be acquired or imported. Filesystem replacement, scanning, renaming, extras, collision protection, classification, queue/history recovery and edition-aware decisions remain behind the later gates.

## Verification

All runs use disposable configurations and databases. The Docker runner mounts this source checkout; PostgreSQL uses a dedicated Docker network, no published port and tmpfs data storage. No production Radarr configuration or media library is used.

- .NET SDK 8.0.421: core test project, Linux console application and Linux platform assembly build in Release with zero warnings/errors (`EnableAnalyzers=false`, `RuntimeIdentifiers=linux-x64`).
- SQLite 3.53.4 final regression selection: **168 passed, 1 skipped, 0 failed**. The skipped existing test requires case-insensitive filesystem behavior and is excluded on Linux.
- PostgreSQL 16.15 final regression selection: **168 passed, 1 skipped, 0 failed** (the same Linux-only skip).
- Coverage includes actual FluentMigrator migration 243, repository ownership/rollback, single/bulk default creation, primary-only settings, stale metadata updates, primary query projections, deletion, dangling-link repair, housekeeping and legacy service regressions. Concurrent expected-file assignment and primary-switch/assignment races are included.
- NUnit TRX artifacts: `_temp/edition-test-results/sqlite.trx` and `_temp/edition-test-results/postgres.trx` (ignored build artifacts).
- Application startup: `/ping` returns `OK` on fresh SQLite and PostgreSQL databases. A synthetic SQLite library with two movies and two file records passes explicit housekeeping, preserves its recorded IMAX/3D label and extra unassigned file, repairs a broken pointer, and retains identical legacy API projections after another restart. PostgreSQL application startup uses an empty disposable library; populated-library migration and repair are covered by the NUnit fixtures.
- Both startup smoke tests have external networking disabled. Expected network-dependent health-check warnings are present; no UI or download workflow was exercised. Startup logs are retained alongside the TRX artifacts.
- `git diff --check`: passed.

The final test filter is:

```text
FullyQualifiedName~MovieEdition|FullyQualifiedName~add_movie_editions|FullyQualifiedName~BasicRepositoryFixture|FullyQualifiedName~MovieRepositoryFixture|FullyQualifiedName~MediaFileRepositoryFixture|FullyQualifiedName~Housekeeping|FullyQualifiedName~MovieServiceTests|FullyQualifiedName~MediaFileServiceTests
```

Build/test commands inside the disposable SDK container:

```sh
dotnet build src/NzbDrone.Core.Test/Radarr.Core.Test.csproj -c Release -p:EnableAnalyzers=false -p:RuntimeIdentifiers=linux-x64
dotnet test src/NzbDrone.Core.Test/Radarr.Core.Test.csproj --no-build --no-restore -c Release -p:EnableAnalyzers=false -p:RuntimeIdentifiers=linux-x64 --filter "$EDITION_TEST_FILTER"
```

For PostgreSQL, only the test invocation receives `Radarr__Postgres__Host`, `Radarr__Postgres__Port`, `Radarr__Postgres__User` and `Radarr__Postgres__Password`, pointing to the disposable server. The NUnit database harness creates and drops uniquely named databases per test. Do not point these variables at an existing Radarr server.

## Still incomplete

- Full Phase 2/later lifecycle gate: two manually imported editions surviving scan/rename/restart, edition-specific upgrades, sidecars/extras and interrupted filesystem operations.
- Combination-aware classification and review of ambiguous/additional legacy files, including unlabelled-cut routing that preserves 3D/IMAX.
- Edition-aware acquisition, queue/pending/history recovery, API/UI, wanted/statistics, naming, notifications and backup/restore integration.
- Full application/UI regression suite, analyzers, and production-scale concurrency/performance testing. Passing the focused database tests does not establish those gates.

Disposable Docker containers and networks are removed after verification. The pre-existing deleted archive fixtures and untracked `dockerfile` are preserved. Nothing has been committed or pushed.
