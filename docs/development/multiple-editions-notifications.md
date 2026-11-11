# Edition identity in notifications

Webhook payloads and custom **notification** scripts expose the assigned target
separately from shared movie metadata. Automatic multi-edition acquisition,
edition retries and custom **import** scripts remain disabled by their existing
safety gates. These notification fields do not authorize acquisition or imports.

## Webhooks

- Grab, Download and ManualInteractionRequired payloads include `movieEdition`
  when the event has an explicit assignment.
- Imported, replaced/deleted and renamed file objects include `movieEditionId`
  and a `movieEdition` metadata snapshot. Each renamed file has its own entry.
- ManualInteractionRequired also includes `editionRecoveryError` when available.

An edition snapshot contains `id`, `movieId`, `name`, `editionKey`,
`qualityProfileId`, `ruleRevision`, `attributes`, `isResolved` and `error`.
Attributes retain combined cut/presentation and user-defined labels.

For a release, metadata comes from the event's explicit edition context; it is
never inferred from the title or monitoring. For a file, metadata is read from
its recorded target at notification time. This works after deletion clears the
current-file pointer and for a superseded file being reported during an upgrade.
These are event-time metadata snapshots, not permanent history records.

`isResolved` means the target metadata belongs to the file/release's parent
movie. It does not validate profile existence, current-file links or acquisition
eligibility. Missing/foreign targets retain their recorded ID with
`isResolved: false` and a diagnostic, without exposing foreign/default metadata.
Unassigned legacy events remain unassigned. Null JSON properties can be omitted
by the existing serializer.

Existing movie fields keep their shared/legacy meanings; consumers should use
the event's explicit file and edition fields for file-specific operations.
Custom-format scores retain their existing event-time meaning. Adding these
fields does not establish compatibility with every external consumer.

## Custom notification scripts

Grab, Download, MovieFileDelete and ManualInteractionRequired environments add:

- `Radarr_MovieEdition_Id`, `Radarr_MovieEdition_Name`
- `Radarr_MovieEdition_QualityProfileId`, `Radarr_MovieEdition_RuleRevision`
- `Radarr_MovieEdition_IsResolved`, `Radarr_MovieEdition_Error`
- `Radarr_MovieEdition`: the full snapshot as JSON, or an empty string

ManualInteractionRequired also adds `Radarr_MovieEdition_RecoveryError`.
Unknown/unassigned fields are empty strings; an unresolved recorded ID is still
present. Attribute values are available in the JSON rather than a delimited name
list. Environment variable casing follows the existing process-provider behavior.

Download upgrades add `Radarr_DeletedMovieFile_Editions`, a JSON array of
`movieFileId`, nullable `movieEditionId` and `movieEdition` snapshot entries.
Rename adds `Radarr_MovieFile_Editions` with the same shape, plus
`Radarr_MovieFile_MovieEditionIds`, a comma-separated list aligned with the
existing `Radarr_MovieFile_Ids`. Unassigned entries have an empty position;
consumers must preserve those positions. The JSON file IDs avoid relying on
delimiter handling for names or attributes.

## Verification

`verify-edition-notifications.py [--postgres]` runs the actual application with
synthetic media and profiles. It captures HTTP notifications on loopback and
executes a generated script that records only its environment in the disposable
test directory. It checks independent upgrades, replaced-file/deletion payloads,
combined attributes, profile-specific scores, multi-file rename alignment and
restart/scan/housekeeping preservation. Unit coverage includes explicit/legacy
release assignments, unassigned files, missing/foreign targets, metadata after
pointer clearing and recovery errors. Acquisition is never enabled to run these
checks; grab payloads are exercised by unit tests. The shared webhook builder is
also used by Notifiarr, but no external Notifiarr service is contacted.
