# Radarr multiple editions — Phase 1 design and audit

Date: 10 September 2026

Source reviewed: Radarr checkout

Commit: `9f4d249956bbe33a48d86705eec8733b16ae3858`

Status: implementation proposal informed by source inspection; no application changes or runtime verification in this phase.

## 1. Product contract

A movie owns independently managed editions. Each edition has one current file, a monitoring preference and a quality profile. Metadata and the movie folder remain shared. Releases without a cut label default to Theatrical, as requested; this does not erase presentation or branding labels.

An edition can combine independent attributes: cut (Theatrical, Director's Cut, Extended, Unrated, Final, Assembly, etc.), stereoscopic presentation (2D or 3D), framing (standard or IMAX), and branding (Criterion, anniversary, collector's, remaster, etc.). For example, Theatrical / 3D / IMAX and Director's Cut / 3D are separate managed targets. IMAX is not intrinsically a different cut, and Criterion is not intrinsically a restoration or cut. Preserve unknown attributes rather than silently mapping them to a known cut.

Use an extensible preset catalogue and user-editable matching rules, not an exhaustive edition enum. Presets must include 3D aliases such as SBS, HSBS, HOU, Half-SBS and Half-OU, with parser regression tests for false positives. Storage uses an open attribute dictionary so additions do not require a schema migration. Codec, resolution and 3D packing remain file characteristics unless explicitly selected as management criteria in a later scope expansion.

Initial scope includes acquisition, upgrades, manual imports and retention of several editions. One edition still represents one desired copy: independently retaining 1080p and 4K of the same cut is deferred. Criterion is supported as a user-defined managed edition, not hardcoded as a distinct cinematic cut.

Core invariants:

1. Every managed file belongs to an edition of its own parent movie.
2. An upgrade can replace only the current file of its assigned edition.
3. One release has at most one automatic target edition.
4. Monitoring never changes classification: an unlabelled release remains Theatrical even when only Director's Cut is monitored.
5. Existing assignments survive rule edits, rescans and restart.
6. Ambiguity never selects an arbitrary file to replace.
7. Acquisition state and comparisons are keyed by edition; metadata identity remains keyed by movie.

## 2. Source audit and implementation impact

Paths below are relative to the reviewed checkout. These are observed dependencies, not a claim that every affected call site has been enumerated.

| Source | Observed assumption | Required change |
|---|---|---|
| `src/NzbDrone.Core/Movies/Movie.cs` | Singular `MovieFile`, `MovieFileId`, `HasFile`; one profile and monitor flag | Editions collection, primary compatibility projection and aggregate availability |
| `src/NzbDrone.Core/Movies/MovieRepository.cs` | Join and missing/cutoff queries depend on the singular pointer | Separate edition queries and movie summaries |
| `src/NzbDrone.Core/Movies/MovieService.cs` | File-added event replaces movie pointer; removal detaches it | Update the affected edition and synchronize the primary projection |
| `src/NzbDrone.Core/MediaFiles/MovieFile.cs` | `Edition` is descriptive text; no managed target identity | Add managed edition association; preserve parsed text |
| `src/NzbDrone.Core/MediaFiles/UpgradeMediaFileService.cs` | Deletes/recycles the movie's selected file before replacement | Resolve and validate target edition's file before filesystem mutation |
| `src/NzbDrone.Core/MediaFiles/MovieImport/ImportApprovedMovie.cs` | Groups by movie; rejects another import once movie ID is seen | Group and deduplicate by edition; carry assignment through events |
| `src/NzbDrone.Core/MediaFiles/MovieImport/Specifications/UpgradeSpecification.cs` | Checks movie's existing file | Evaluate edition's existing file/profile |
| `src/NzbDrone.Core/MediaFiles/MovieImport/Aggregation/Aggregators/AggregateEdition.cs` | Chooses descriptive edition from download, folder, then filename | Keep descriptive aggregation; resolve managed identity separately |
| `src/NzbDrone.Core/DecisionEngine/Specifications/UpgradeDiskSpecification.cs` | Uses movie file and movie profile | Edition evaluation context |
| `src/NzbDrone.Core/DecisionEngine/Specifications/UpgradeAllowedSpecification.cs`, `RepackSpecification.cs`, `RssSync/ProperSpecification.cs`, `RssSync/DelaySpecification.cs` | Revision, proper and delay decisions use movie's file | Scope all comparisons to target edition |
| `src/NzbDrone.Core/DecisionEngine/Specifications/QueueSpecification.cs` | Competing queue entries matched by movie ID | Compare edition ID, retain release-level deduplication |
| `src/NzbDrone.Core/DecisionEngine/DownloadDecisionPriorizationService.cs` | Groups by movie ID | Rank within edition |
| `src/NzbDrone.Core/Download/ProcessDownloadDecisions.cs` | A processed movie suppresses additional decisions | Process each edition independently |
| `src/NzbDrone.Core/Download/Pending/PendingReleaseService.cs` | Pending groups, queue deduplication and fallbacks use movie ID | Persist and group by edition |
| `src/NzbDrone.Core/Housekeeping/Housekeepers/CleanupOrphanedMovieFiles.cs` | Deletes file records not referenced by `Movies.MovieFileId` | Preserve files owned by valid editions; critical prerequisite |
| `src/NzbDrone.Core/Housekeeping/Housekeepers/CleanupOrphanedMovieMovieFileIds.cs` | Repairs single movie pointer | Repair edition links plus primary projection |
| `src/NzbDrone.Core/Housekeeping/Housekeepers/FixWronglyMatchedMovieFiles.cs` | Old repair SQL is commented out | Do not treat as active behavior; keep future repair edition-aware |
| `src/NzbDrone.Core/Extras/*/Existing*Importer.cs` | Some extra-file associations use movie's selected file | Associate by actual imported edition/file |
| `src/NzbDrone.Core/MovieStats/MovieStatisticsRepository.cs` | File count derived from movie pointer | Count edition files without multiplying movie counts |
| `src/Radarr.Api.V3/Movies/MovieResource.cs` | Singular file/profile response | Primary projection plus explicit edition summaries |

File repository, scan, rename and media-info services already contain list-based operations. These are useful foundations but do not establish end-to-end multi-file support. Existing metadata separation also helps; the movie metadata uniqueness constraint should remain intact.

Follow-through audits during implementation must include manual import/reprocess, blocklist, download-history recovery, library moves, notifications, metadata naming, custom scripts, import lists, movie deletion and backup/restore. Each is a required integration checkpoint, not an optional later enhancement.

## 3. Proposed persistence model

### MovieEditions

| Column | Proposed representation |
|---|---|
| Id | Integer primary key |
| MovieId | Required parent reference |
| EditionKey | Stable identity; unique with MovieId; not regenerated when display name changes |
| Name | Editable display name |
| Monitored | Boolean |
| QualityProfileId | Required reference; copied from movie default on creation |
| MovieFileId | Nullable current-file reference |
| LastSearchTime | Nullable timestamp |
| MatchRules | JSON array of full-title regular expressions |
| Attributes | JSON dictionary of independent cut, stereoscopic presentation, framing and branding values; absence means not yet classified |
| RuleRevision | Integer incremented on classification-affecting changes |

Use stable keys such as `theatrical`, `directors-cut`, `extended`, and generated keys for user definitions. No global template table is required initially: built-in presets can be code-defined, with custom rules stored per movie edition. Global reusable templates can be added later.

Add `PrimaryEditionId` to Movies. Keep movie-level Monitored as the master acquisition switch and QualityProfileId as the new-edition default. Keep legacy MovieFileId temporarily as a synchronized primary-edition projection, never as the authority for acquisition or replacement.

Add MovieEditionId to MovieFiles. Preserve existing Edition text as descriptive metadata. Both file-to-edition ownership and edition-to-current-file pointers must be checked for same-movie consistency. Repository transactions maintain their consistency; conditional uniqueness and foreign-key implementation must follow supported SQLite/PostgreSQL migration conventions. Do not blindly add a unique MovieFiles.MovieEditionId constraint: legacy duplicate records need a preservation path and replacement can temporarily involve two records.

Pending releases receive a nullable MovieEditionId and classification revision. New accepted grabs persist edition ID and name snapshot in history, using a typed nullable edition field for lookup and existing history Data for explanatory snapshots. Propagate through download history/recovery where required. Historical rows can remain unassigned; do not retroactively guess their intended edition.

Edition deletion with queued downloads or pending work is rejected until that work is resolved. Historical name snapshots remain readable after deletion.

## 4. Classification and evaluation services

Introduce an edition classifier with a structured result: Matched, Unconfigured, Ambiguous or Invalid. Include candidate edition IDs, parsed label, rule revision and reason. Avoid encoding uncertainty as a null that downstream code silently turns into Theatrical.

Resolution sequence:

1. Valid explicit manual assignment wins.
2. A persisted grab assignment remains the target at import. An absent label in an obfuscated filename does not invalidate it. Contradictory explicit evidence triggers review.
3. Exactly one matching user rule wins over built-in labels; two matching custom editions are ambiguous.
4. Recognized built-in labels produce a combination of attributes and select the corresponding configured target. Labels in different dimensions (such as Director's Cut and 3D) combine; incompatible values in one dimension are ambiguous.
5. Non-empty parsed edition text without a configured mapping is unconfigured, not theatrical.
6. Default a missing cut to Theatrical while preserving other recognized attributes. A release marked only 3D must not fall into the ordinary theatrical/2D target. If the resulting target is absent, return unconfigured; do not create a monitored target automatically.

Several patterns within one edition are OR alternatives. Matching is case-insensitive. Validate expressions at save time and bound matching time; a timeout produces a classification failure rather than theatrical fallback. Built-in aliases start with the existing edition parser/test vocabulary, reviewed individually rather than broad substring matching.

Store evidence separately from managed identity. A custom Criterion edition can match a Director's Cut release and retain that cut description. Users wanting both Criterion and another Criterion-overlapping target must make their rules exclusive or manually assign.

Add edition context to RemoteMovie and LocalMovie: target edition, effective profile, existing file and classification result. Never temporarily overwrite shared Movie.MovieFile or Movie.QualityProfile to reuse old code: concurrent editions would contaminate each other's decisions.

The score calculation can continue summing matching formats, using the edition profile. A parser test with no mapped movie has no edition/profile context; expose that fact rather than implying a meaningful zero score. A future optional edition ID for parser preview is included in the API work.

Movie identity resolution remains separate. A token before the year can prevent existing movie matching; the edition feature does not promise to fix all title parsing. Add regression coverage around supported edition labels and show unmapped-movie results explicitly.

## 5. Search, queue and filesystem behavior

A movie-wide search fetches results once per existing indexer workflow and distributes them to edition evaluation. Edition searches filter the same movie search results; indexers do not need a new edition API. RSS likewise classifies once, then applies target monitoring/profile rules.

All selection, pending-delay and upgrade competition uses edition identity. Release identity deduplication remains across editions using available indexer GUID/download identity. An edition ID must survive client submission, restart, failure and completed-download recovery.

Before upgrading, validate target membership, latest current-file pointer and destination collision. Serialize imports per edition, with movie-folder collision protection across editions. Keep existing recycle-bin and import recovery behavior; document that filesystem and database operations are not one atomic transaction. Test failure between removing the old file, moving the new file and updating records.

New `{Managed Edition}` naming token expands to a sanitized label. The existing parsed-edition token keeps its meaning. Adding a second edition requires a naming preview that proves distinct destinations or an actionable collision error. Existing filenames are not migrated automatically.

Unmanaged additional files found on disk appear as import candidates/conflicts; scanning must not silently delete them. File-specific subtitles and sidecars follow file ownership. Movie-level artwork remains shared; competing edition-specific metadata destinations must be resolved explicitly.

## 6. Migration and compatibility decisions

Migration runs before workers and housekeeping start. Create a default target for each movie, copy monitoring/profile/search settings and attach its current file. Classify an existing file from its recorded edition text: preserve recognized/non-empty names, otherwise Theatrical. Empty movies receive Theatrical. Do not enable a new theatrical acquisition for a movie that previously owned only a labelled cut.

For additional existing file rows, attach uniquely classifiable files to unmonitored editions. Retain conflicting rows with an unassigned/conflict state until user review. Housekeeping must preserve such valid parent-owned records. Broken current-file pointers create a missing edition and a diagnostic rather than deleting recoverable files.

Implementation staging: the initial storage migration preserves the primary file's recorded label verbatim (or names it Theatrical when empty). It does not run today's release parser or infer 2D/standard framing. Attributes remain unclassified, and additional file rows remain unassigned until the combination-aware classifier and review flow exist. This conservative first step avoids making irreversible guesses from incomplete legacy metadata. It is not yet a completed edition-classification migration.

Primary edition initially corresponds to the migrated current file, or Theatrical for an empty movie. Adding another edition does not change primary. Switching primary updates the legacy projection transactionally.

For compatibility, keep legacy `movieFile`, `movieFileId` and `hasFile` internally consistent with the primary edition. This refines the earlier plan's suggestion to redefine `hasFile` globally. Add `hasAnyEditionFile`, `editionCount`, `monitoredEditionCount` and `missingMonitoredEditionCount` for aggregate semantics. Existing movie endpoints continue returning one row per movie.

Legacy movie monitoring changes the master switch; legacy quality-profile edits update the movie default and primary edition, preserving historical single-edition behavior. Other edition profiles change only through explicit edition edits or an apply-to-all option. The new UI must explain that distinction.

Old file-ID operations remain file-specific. Movie-wide delete retains its documented all-files meaning. Ambiguous destructive operations lacking an edition/file target fail validation. Keep old wanted endpoints movie-based, listing each movie at most once if any monitored edition qualifies; add edition-based endpoints for the new UI.

Compatibility risks remain for external consumers that assume movie-level file fields describe the whole library. Document primary projection semantics and test representative API/notification contracts; do not claim all third-party clients are compatible without testing.

## 7. Proposed API contract

Routes are proposed extensions to v3, to be checked against controller conventions during implementation.

| Operation | Contract |
|---|---|
| GET `/api/v3/movieedition?movieId=42` | List edition resources with state, profile, file and matching rules |
| POST `/api/v3/movieedition` | Create edition under existing movie; no implicit download |
| PUT `/api/v3/movieedition/{id}` | Change name, profile, monitoring or rules; never auto-reassign files |
| DELETE `/api/v3/movieedition/{id}?deleteFiles=false` | Explicit removal; block primary/in-flight target until replacement/resolution; block retained-file ambiguity |
| Movie update with `primaryEditionId` | Select validated member edition as primary |
| Existing search command plus `movieEditionIds` | Search selected targets; reject inconsistent movie IDs |
| Existing release query plus `movieEditionId` | Edition-scoped results including assignment and rejection reasons |
| Import/reprocess/grab payload plus `movieEditionId` | Explicit validated target; server reruns classification/acceptance checks |
| GET `/api/v3/wanted/editionmissing` and `/editioncutoff` | Paged edition rows with parent movie information |
| Parse preview plus optional `movieEditionId` | Preview classification and scoring in explicit context without changing stored state |

Edition resources include ID, parent ID, key, name, monitored, effective monitored, qualityProfileId, current file, state, rules and revision. Distinguish invalid request (400), missing resource (404) and conflicting assignment/state (409), subject to established API error conventions.

Removal with deleteFiles=false must not orphan an owned file: require reassignment to another valid target or an explicit unmanaged-retention flow before removal. Do not silently transfer it to Theatrical.

## 8. UI outline

Movie detail gains an Editions table: Name | Monitor | Profile | File/quality | Score | Status | Actions. Shared movie metadata stays above it. Display a count such as “2 of 3 monitored editions available.”

Add Edition offers presets and Custom. Custom opens a name and full-title rule editor with a classification preview. Include concise help: “Releases without an edition label are treated as Theatrical.” Show ambiguity and profile-specific score separately.

Search results display Assigned Edition and filter by target. Manual import adds an edition selector and conflicting-evidence message. Wanted pages show one row per missing/cutoff edition. Removal distinguishes unmonitoring, deleting a file, and removing its managed target. Rename preview displays edition and destination.

Implementation entry points include frontend movie-detail components, `InteractiveSearch`, `InteractiveImport/Interactive/InteractiveImportRow.tsx`, `Wanted/Missing`, `Wanted/CutoffUnmet`, `Parse/ParseResult.tsx`, shared movie typings and state selectors. New user-facing strings belong in the English localization source.

## 9. Delivery order and gates

1. Persistence, migration, ownership validation, housekeeping and primary projections. Gate: migrated single-edition library survives startup and cleanup on both databases.
2. Edition contexts and file lifecycle, including extras and conflict handling. Gate: two manually imported editions survive scan/rename/restart and an upgrade replaces only its own edition.
3. Classifier, search and scoring. Gate: unlabelled theatrical routing, explicit labels, ambiguity and unconfigured cases behave consistently.
4. Queue, pending, grab history and recovery. Gate: two edition downloads coexist across restart; same-edition duplicates remain suppressed.
5. Complete UI, API contracts, wanted/statistics, notifications and documentation. Gate: end-to-end workflows and legacy single-edition behavior pass.

Do not enable multi-edition creation before gates 1 and 2 are complete. Branch commits may be staged for review, but intermediate builds must not expose partially implemented multi-edition management.

Required regression matrix uses synthetic titles/groups: default theatrical; labelled non-theatrical; one/multiple custom matches; rule timeout; unknown label; no monitored target; different profiles; concurrent imports; shared filename; interrupted replacement; missing file; sidecar retention; rule edit while downloading; removed target; failed download; primary switch; additional legacy rows; SQLite/PostgreSQL migration; housekeeping after migration; backup restore; legacy API projection.

## 10. Phase 1 conclusion

The design is actionable for Phase 2 with the defaults above. The strongest newly identified prerequisites are orphan-file cleanup, batch import deduplication and processed/pending download grouping. These must change alongside the storage model.

No further user choice is required to begin foundational implementation. Maintainer feedback may change API compatibility or schema direction before an upstream submission. The remaining work includes implementation-time exhaustive call-site conversion and runtime verification; neither has been performed in this design phase.

Planning allowance remains approximately 6–10 engineering weeks for the full feature, with significant uncertainty around integrations and migration edge cases. Phase 1 establishes dependencies and decisions; it does not validate that delivery estimate experimentally.
