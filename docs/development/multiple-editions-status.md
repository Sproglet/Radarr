# Multiple editions — Phase 2 storage groundwork

Date: 10 September 2026

## Implemented in the Radarr checkout

- Migration 243 creates MovieEditions, a unique per-movie edition key, primary-edition links and nullable file ownership.
- Existing primary files retain their recorded labels, profiles, monitoring and search timestamps. Empty labels receive the display name Theatrical. Attributes remain unclassified: no inference that an existing file is 2D or non-IMAX.
- Additional files remain unassigned and preserved. Broken or cross-movie legacy pointers are cleared without deleting file records or moving anything on disk.
- The edition model stores open-ended attributes and matching rules, allowing cut/presentation/framing/branding combinations without a closed enum.
- A transactional repository assignment operation validates movie ownership, rejects another edition's file, checks the expected previous file, and updates the legacy pointer only for the primary edition. It does not delete files or perform filesystem operations.
- Orphan-file housekeeping now checks whether the parent movie exists, rather than whether the single legacy pointer selects the file.
- Added migration and repository test fixtures; expanded housekeeping regression coverage. New fixtures use synthetic file names.

## Verification

- `git diff --check`: passed.
- Direct SQLite smoke test of the four migration data statements and housekeeping SQL: eight assertions passed, covering label preservation, settings, missing/broken/cross-movie pointers, extra-file retention, orphan removal and linked primary ownership.
- Not run: C# compilation, NUnit fixtures, actual FluentMigrator schema creation, PostgreSQL or application startup. Neither .NET nor Docker is available in this environment. The SQLite smoke check is not a substitute for these tests.

## Remaining Phase 2 gate

This is an intermediate storage slice, not a completed Phase 2 or a usable multi-edition build. The new assignment operation is deliberately not connected to import/upgrade flows yet. Existing movie/file lifecycle operations do not yet maintain every new edition field; migrated edition rows are not yet the runtime authority.

Next work must wire default-edition creation for newly added movies, synchronize legacy profile/search/file edits, handle deletion and dangling edition links, and establish transactional primary-projection updates. Then run migration/ownership/housekeeping tests on SQLite and PostgreSQL and check startup against a disposable copy of a library. Do not test against the main library database; migration rollback is not implemented by Radarr's migration base.

Multi-edition creation and acquisition remain unexposed. File replacement, scanning, extras and naming require the following lifecycle gate before enabling them. Classification of legacy 3D/IMAX combinations remains pending the combination-aware classifier and review flow; the migration intentionally does not guess from filenames.

## Continue development on another machine

Read [the design](multiple-editions-design.md) first, then complete the remaining Phase 2 gate above. The work is intentionally incomplete. Keep test titles and release groups synthetic, and use disposable library data for migration and runtime checks.
