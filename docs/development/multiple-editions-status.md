# Multiple editions — lifecycle and acquisition groundwork

Date: 4 November 2026 (verification completed 3 November)

Branch: `Multiple-Editions`, continued from `855e517b3`. On resuming, the working tree was clean and the Phase 2/3 implementation was already recorded in existing commit `1ed83ece1`. This checkpoint records the November collision, classification, gated search/policy and download-identity groundwork. Acquisition remains disabled. Remote integration is recorded below; older push-status notes describe the earlier checkpoints.

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
- The full application/UI suite and analyzers were not run. No production Radarr configuration or media library was accessed. September’s implementation is now in the existing baseline commit noted above; the November work is included in this checkpoint commit; no push has been performed.

## November collision follow-up

- Destination checks now also inspect unassigned videos on disk. A different extension or case-only basename conflict is rejected before recycling an edition, preventing ambiguous sidecar ownership on subsequent scans. Existing tracked-file ownership checks remain in place.
- Added filesystem-backed regressions that preserve both edition records and the conflicting unassigned video, with no recycle operations.
- Fresh Docker regression runs: **768 passed, 4 skipped, 0 failed on SQLite and PostgreSQL 16**. The four skips remain existing platform-specific tests. Artifacts: `_temp/edition-test-results/phase3-review-sqlite.trx` and `phase3-review-postgres.trx`, with matching logs. The Linux console build passed with zero warnings/errors (`phase3-review-app-build.log`).
- The offline SQLite application/API smoke test passed again on the updated build: two combined editions, file-specific subtitles, scan, rename, housekeeping, two real process restarts and independent upgrades. Results: `_temp/edition-test-results/phase3-review-runtime-results.log`; runtime output: `phase3-runtime.log`. The script removed its temporary configuration and synthetic library. Follow-up Docker containers and their network were removed after verification.
- Fresh restore on 3 November hit warning-as-error failures for existing ImageSharp 3.1.12 NuGet advisories. Follow-up verification uses `NuGetAudit=false` only as a build/test command option, alongside `EnableAnalyzers=false`; package versions and repository audit settings are unchanged. Dependency remediation remains outstanding.

## Classification and scoring groundwork (next gate in progress)

- Added a read-only classifier with `Matched`, `Unconfigured`, `Ambiguous` and `Invalid` results, candidate IDs, evidence attributes, parsed label, reason and matched rule revision. Explicit assignments require movie membership. Matching does not consult monitoring.
- Built-in cut/presentation combinations preserve Theatrical / IMAX / 3D and Director’s Cut / 3D. A missing cut becomes Theatrical, retaining recognized presentation attributes. Targets must match the complete attribute set; absent/duplicate targets never select an arbitrary edition. Presets include bounded-token 3D, SBS, HSBS, HOU, Half-SBS and Half-OU aliases.
- Case-insensitive user rules take precedence and are OR alternatives within one target. Matches across several targets are ambiguous. Invalid expressions, timeouts and exhausted matching budgets fail closed. Unmapped descriptive labels stay unconfigured. Custom attributes and parsed evidence are retained without adding a fixed edition enum.
- Repository inserts/updates validate regex syntax and size/count bounds transactionally. Rule/attribute edits advance `RuleRevision`, including partial updates; ordinary/stale updates cannot reset it. Rules are bounded to 50 ms per match, with a 250 ms matching budget checked between rules.
- Added a read-only evaluation primitive for future search/parser previews. It resolves the chosen profile/current-file context and calculates the profile’s custom-format score without substituting fields on the shared movie or authorizing a download. Unmapped/unconfigured results have a nullable absent score.
- Verification: **161 passed, 0 skipped, 0 failed on each of SQLite and PostgreSQL 16**, using the disposable Docker runner and PostgreSQL tmpfs database with no published database port. Selection: `MovieEdition`, `EditionFileHandlingFixture`, `Housekeeping`, `add_movie_editions`. Coverage includes combined attributes, 3D aliases/word boundaries, conflicting labels, unconfigured targets, overlapping custom rules, invalid/timeout rules, explicit membership, selected-profile scoring, unmapped scores, transaction rollback and partial-update revisions. Build/test options remain `EnableAnalyzers=false`, `NuGetAudit=false`, `RuntimeIdentifiers=linux-x64`. Artifacts: `_temp/edition-test-results/classifier-sqlite.trx`, `classifier-postgres.trx` and matching logs. `git diff --check` passed. Disposable containers/network were removed; no production configuration/media was accessed and no commits/pushes were made.
- **The full classifier/search gate is not yet passed.** The parser/search follow-up below connects classification to gated search/RSS results. Parser-preview API and automatic manual-import assignment remain deferred. Existing unclassified migration attributes are not backfilled. Additional preset vocabulary, persisted-grab evidence reconciliation, edition-aware live specifications/prioritization, queue/history recovery and UI review remain incomplete. Automatic multi-edition acquisition stays disabled.

## Parser evidence and gated search integration

- Added parser-backed evidence extraction from `SimpleReleaseTitle`, where the parser has replaced the matched movie title. Remove the parsed release-group suffix before recognizing presentation/cut labels. Repeated labels are retained for conflict detection; unknown marked labels and Criterion remain unconfigured unless explicitly mapped. A missing parser evidence string produces an invalid result rather than a theatrical guess.
- Search and RSS decision making now classifies releases for movies with multiple configured editions, resolves the matched context and reports its profile-specific score. Every such report remains permanently rejected with `MultiEditionAcquisitionDisabled`, including matched and unconfigured reports. It bypasses legacy movie-scoped queue/history/delay checks; the policy follow-up below runs only converted read-only checks for matched targets. Single-edition decision behavior is retained.
- Release resources expose `movieEditionId`, structured `editionClassification` and nullable `editionCustomFormatScore`. The legacy integer score remains for compatibility; the nullable edition score distinguishes unknown/unconfigured results. No edition target supplied by a client can bypass the existing submission guard.
- Verification: **434 passed, 1 existing skip, 0 failed on each of SQLite and PostgreSQL 16**. Selection: `MovieEdition`, `DecisionEngineTests`, `EditionFileHandlingFixture`, `Housekeeping`, `add_movie_editions`. The pre-existing skipped test is `should_not_allow_download_if_no_episodes_found`. Parser cases cover combined cuts/presentation, title/group false positives, an IMAX tag with an IMAX-named dummy group, conflicting cuts and unknown labels. Search and RSS tests assert matched reports remain acquisition-disabled and unconverted specifications are not evaluated for those reports. The existing broken-report regression now asserts three returned error decisions instead of relying on log counts.
- Linux application/API Release build passed with zero warnings/errors. Verification options: `EnableAnalyzers=false`, `NuGetAudit=false`, `RuntimeIdentifiers=linux-x64`; existing dependency advisories remain unresolved. Artifacts: `_temp/edition-test-results/edition-search-sqlite.trx`, `edition-search-postgres.trx`, matching logs and `edition-search-build.log`. API serialization/UI round trips and a live-indexer workflow were not exercised in this follow-up. `git diff --check` passed. Disposable runner, tmpfs PostgreSQL container and test network were removed; no production configuration or media was used. Changes are included in this checkpoint commit; no push has been performed.
- This is gated classification/scoring visibility, not completed acquisition selection. The converted policy/ranking checks are described below; queue/pending/history persistence and recovery, remaining specifications, explicit edition search filters and UI selectors remain incomplete. No new targets or legacy backfill are created automatically.

## Edition-aware policy and ranking follow-up

- Quality allowance, language, custom-format minimum score, disk cutoff/upgrade allowance, proper and repack checks now use the selected edition’s effective profile/current file. A missing secondary file is treated as missing without falling back to the primary. Existing single-edition call paths keep their legacy semantics.
- Matched search/RSS previews run an explicit allowlist of these converted read-only checks plus monitoring and return their rejection reasons alongside the mandatory acquisition-disabled rejection. Movie and edition monitoring must both be enabled in edition previews, including user-invoked search. Unconverted queue/history/delay checks remain excluded.
- Decision prioritization groups by movie plus edition and compares qualities through the effective profile. Editions with opposite quality preferences rank independently. Unconfigured/invalid edition evaluations remain unranked, rather than inheriting the primary profile’s preferences.
- Verification: **442 passed, 1 existing skip, 0 failed on each of SQLite and PostgreSQL 16**, using disposable Docker databases/configurations. Selection: `DecisionEngineTests`, `MovieEdition`, `EditionFileHandlingFixture`, `Housekeeping`, `add_movie_editions`. New cases cover missing-secondary-file handling, upgrading against only the secondary’s current file while preserving the primary, opposite profile preferences within a shared movie, both monitoring switches, and a profile rejection returned together with the acquisition-disabled gate. Existing legacy decision tests also pass. The skipped test remains `should_not_allow_download_if_no_episodes_found`.
- Linux application/API Release build passed with zero warnings/errors. Options remain `EnableAnalyzers=false`, `NuGetAudit=false`, `RuntimeIdentifiers=linux-x64`; the existing dependency advisory limitation is unchanged. Artifacts: `_temp/edition-test-results/edition-policy-sqlite.trx`, `edition-policy-postgres.trx`, matching logs and `edition-policy-build.log`. `git diff --check` passed. No live-indexer/API/UI workflow was exercised in this follow-up. Disposable containers and their network were removed; no production configuration or media was accessed, and the changes are included in this checkpoint commit; no push has been performed.
- The full acquisition/recovery gate remains closed. Queue/pending/history identity, persisted-grab reconciliation, duplicate/release identity handling, delay behavior, remaining specifications, explicit edition search filters and UI workflows are still incomplete. These previews do not authorize downloads or start imports. No automatic legacy backfill or target creation is enabled.

## Download identity persistence groundwork (recovery gate in progress)

- Migration 244 adds nullable `MovieEditionId` and `EditionRuleRevision` to pending releases, movie history and download history. Legacy rows remain unassigned even when their titles contain recognizable labels. No grab intent is inferred from current rules or filenames.
- Pending insertion and both grab-history handlers retain target identity/revision; grab histories retain `movieEditionName` snapshots in their data, and pending additional info retains `EditionName`. Imported-file history writes retain the imported file’s edition. Movie-history and pending repositories add edition-scoped queries; null selects legacy unassigned rows rather than every edition.
- Active pending/grab writes validate same-movie target membership under a parent-row lock. Pending metadata updates cannot reassign ownership. Edition deletion rolls back when assigned pending rows or unfinished download grabs still reference the target. Later complete/fail/ignore download-history events release the in-flight guard; historical identity/name snapshots remain readable after target deletion.
- Added a conservative recovery primitive that accepts typed grab records only for the same movie/download, rejects disagreeing assignments and missing targets, and preserves a recorded target after matching-rule edits. Unassigned grabs for movies without exactly one target require manual review. This primitive does not reclassify an obfuscated filename or replace persisted intent with today’s matching rules.
- Download-ID inference during multi-edition file import now considers only history assigned to the selected edition. Unassigned legacy grabs are not silently attached to another edition’s import.
- Verification: **483 passed, 1 existing skip, 0 failed on each of SQLite and PostgreSQL 16**, using disposable Docker databases/configurations. Selection includes `edition_download_identity`, `EditionDownloadIdentity`, `MovieEdition`, `PendingRelease`, `HistoryTests`, `TrackedDownload`, `DecisionEngineTests`, `EditionFileHandlingFixture`, `Housekeeping`, `add_movie_editions`. Actual migration 244 tests preserve unassigned legacy pending/history/download rows. New database-backed tests verify scoped reloads, name/revision snapshots from both real grab handlers, pending ownership/deletion rollback, unfinished-grab deletion blocking followed by completion, persisted intent after rule edits, mismatched records and removed targets. Existing legacy tests pass; the skip remains `should_not_allow_download_if_no_episodes_found`.
- Linux application/API Release build passed with zero warnings/errors. Options remain `EnableAnalyzers=false`, `NuGetAudit=false`, `RuntimeIdentifiers=linux-x64`; existing dependency advisories remain unresolved. Artifacts: `_temp/edition-test-results/edition-identity-sqlite.trx`, `edition-identity-postgres.trx`, matching logs and `edition-identity-build.log`. `git diff --check` passed. Disposable containers and their network were removed. No production data, actual download client, live-indexer workflow or process-restart queue recovery was used or verified. The changes are included in this checkpoint commit; no push has been performed.
- **The recovery/acquisition gate remains incomplete and closed.** This slice supplies persistence and a recovery primitive; it does not yet wire that primitive throughout tracked-download cache refresh, live queue/pending reconstruction or completed-download import. Pending grouping/removal, release deduplication, delay/history specifications, contradictory-evidence review, failure/completion event propagation and queue/history API fields still need conversion. Grab-history writes to the two history streams are separate transactions. Repository reload tests are not proof of live queue recovery across a process restart. Acquisition stays disabled and no production configurations or media are used.

## Remote integration (4 November 2026)

The push was blocked by diverged history: the local branch had two edition commits absent from the remote, and the remote had 40 commits absent locally. Merged `origin/Multiple-Editions` at `6f79323bb`, preserving both histories. The remote includes .NET SDK 10.0.402, updated FluentMigrator and other upstream changes; the shared filename-builder changes merge cleanly with managed-edition naming.

The newer PostgreSQL identity mode exposed two compatibility issues during verification. Synthetic migration fixtures now use parameterized inserts with `OVERRIDING SYSTEM VALUE` on PostgreSQL. Edition lifecycle parent-row locks now use `Monitored = Monitored` instead of updating the generated identity column. The locking operation preserves values and continues to serialize parent mutations on both databases. No migration identity mode or production data was altered.

Final merged-branch verification under SDK 10.0.402: **483 passed, 1 existing skip, 0 failed on each of SQLite and PostgreSQL 16** using the identity/lifecycle regression selection recorded above. The Linux application/API Release build passed with zero warnings/errors. Flags remain `EnableAnalyzers=false`, `NuGetAudit=false`, `RuntimeIdentifiers=linux-x64`; this is not an analyzer/security-audit or full UI/recovery gate. Artifacts: `_temp/edition-test-results/edition-merge-sqlite.trx`, `edition-merge-postgres.trx`, matching logs and `edition-merge-build.log`. Acquisition remains disabled. Disposable containers/network were removed after verification. The merge retains both branch histories and permits a normal fast-forward push without rewriting the remote.


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
