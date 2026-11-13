using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Download
{
    public interface IMovieEditionSubmissionRepository : IBasicRepository<MovieEditionSubmission>
    {
        MovieEditionSubmission Prepare(MovieEditionSubmission submission);
        MovieEditionSubmission Accept(string operationId, string downloadId);
        MovieEditionSubmission Record(string operationId);
        MovieEditionSubmission RecordGrabs(string operationId);
        MovieEditionSubmission Settle(string operationId);
        MovieEditionSubmission FindByOperationId(string operationId);
        bool HasUnsettled(int movieId, int? editionId = null);
        List<MovieEditionSubmission> RecentForMovie(int movieId);
        int ReconciliationUpperBound();
        List<MovieEditionSubmissionCandidate> ReconciliationCandidates(int afterId, int throughId, int limit);
    }

    public class MovieEditionSubmissionRepository : BasicRepository<MovieEditionSubmission>, IMovieEditionSubmissionRepository
    {
        private static readonly JsonSerializerOptions ReleaseComparisonOptions = new()
        {
            Converters = { new STJUtcConverter() }
        };

        private readonly HistoryRepository _history;
        private readonly DownloadHistoryRepository _downloads;

        public MovieEditionSubmissionRepository(IMainDatabase database, IEventAggregator eventAggregator, HistoryRepository history, DownloadHistoryRepository downloads)
            : base(database, eventAggregator)
        {
            _history = history;
            _downloads = downloads;
        }

        protected override bool RequiresLifecycleTransaction => true;

        public MovieEditionSubmission FindByOperationId(string operationId) => Query(s => s.OperationId == operationId).SingleOrDefault();

        public int ReconciliationUpperBound()
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<int>(@"SELECT COALESCE(MAX(""Id""), 0) FROM ""MovieEditionSubmissions"" WHERE ""State"" IN (1,2)");
        }

        public List<MovieEditionSubmissionCandidate> ReconciliationCandidates(int afterId, int throughId, int limit)
        {
            if (afterId < 0 || throughId < afterId || limit is < 1 or > 100)
            {
                throw new ArgumentException("Provide an ordered reconciliation range and a batch size from 1 to 100.");
            }

            using var connection = _database.OpenConnection();
            return connection.Query<MovieEditionSubmissionCandidate>(
                @"SELECT s.""Id"", s.""OperationId"",
                    EXISTS (SELECT 1 FROM ""DownloadHistory"" d WHERE d.""EditionOperationId"" = s.""OperationId"" AND d.""EventType"" IN (2,3,4)) AS ""HasTerminalEvidence""
                  FROM ""MovieEditionSubmissions"" s
                  WHERE s.""Id"" > @AfterId AND s.""Id"" <= @ThroughId AND s.""State"" IN (1,2)
                    AND (s.""State"" = 1
                      OR NOT EXISTS (SELECT 1 FROM ""History"" h WHERE h.""EditionOperationId"" = s.""OperationId"" AND h.""EventType"" = 1)
                      OR NOT EXISTS (SELECT 1 FROM ""DownloadHistory"" d WHERE d.""EditionOperationId"" = s.""OperationId"" AND d.""EventType"" = 1)
                      OR EXISTS (SELECT 1 FROM ""DownloadHistory"" d WHERE d.""EditionOperationId"" = s.""OperationId"" AND d.""EventType"" IN (2,3,4)))
                  ORDER BY s.""Id"" LIMIT @Limit",
                new { AfterId = afterId, ThroughId = throughId, Limit = limit }).ToList();
        }

        public List<MovieEditionSubmission> RecentForMovie(int movieId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<MovieEditionSubmission>(
                @"SELECT * FROM ""MovieEditionSubmissions"" WHERE ""MovieId"" = @MovieId
                  AND (""State"" <> 3 OR ""Id"" IN
                    (SELECT ""Id"" FROM ""MovieEditionSubmissions"" WHERE ""MovieId"" = @MovieId AND ""State"" = 3
                     ORDER BY ""CreatedAt"" DESC, ""Id"" DESC LIMIT 100))
                ORDER BY CASE WHEN ""State"" = 3 THEN 1 ELSE 0 END, ""CreatedAt"" DESC, ""Id"" DESC",
                new { MovieId = movieId }).ToList();
        }

        public bool HasUnsettled(int movieId, int? editionId = null)
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<bool>(@"SELECT EXISTS (SELECT 1 FROM ""MovieEditionSubmissions""
                WHERE ""MovieId"" = @MovieId AND ""State"" <> 3 AND (@EditionId IS NULL OR ""MovieEditionId"" = @EditionId))",
                new { MovieId = movieId, EditionId = editionId });
        }

        public MovieEditionSubmission Prepare(MovieEditionSubmission submission)
        {
            submission.OperationId ??= Guid.NewGuid().ToString("D");
            submission.CreatedAt = DateTime.UtcNow;
            submission.UpdatedAt = submission.CreatedAt;
            if (!MovieEditionOperationIdentity.IsValid(submission.OperationId) || submission.Release == null || submission.ParsedMovieInfo == null)
            {
                throw new InvalidOperationException("Provide valid submission identity, release and parsed evidence before preparation.");
            }

            submission.DownloadClient ??= submission.DownloadClientName;
            submission.Languages ??= submission.ParsedMovieInfo.Languages ?? new();
            var message = CreateGrabEvent(submission);
            submission.HistoryData = HistoryService.CreateGrabHistory(message).Data;
            submission.DownloadHistoryData = DownloadHistoryService.CreateGrabHistory(message).Data;
            return Insert(submission);
        }

        private static MovieGrabbedEvent CreateGrabEvent(MovieEditionSubmission submission)
        {
            var edition = new MovieEdition
            {
                Id = submission.MovieEditionId, MovieId = submission.MovieId, RuleRevision = submission.EditionRuleRevision,
                Name = submission.EditionName, Attributes = submission.EditionAttributes, QualityProfileId = submission.QualityProfileId,
                MovieFileId = submission.MovieFileId
            };
            var remote = new RemoteMovie
            {
                Movie = new Movie { Id = submission.MovieId }, Release = submission.Release, ParsedMovieInfo = submission.ParsedMovieInfo,
                Languages = submission.Languages, CustomFormatScore = submission.CustomFormatScore,
                MovieMatchType = submission.MovieMatchType, ReleaseSource = submission.ReleaseSource,
                EditionContext = new MovieEditionContext(edition, null, null, true)
            };
            return new MovieGrabbedEvent(remote, submission.OperationId, submission.CreatedAt)
            {
                DownloadId = submission.DownloadId, DownloadClientId = submission.DownloadClientId,
                DownloadClient = submission.DownloadClient, DownloadClientName = submission.DownloadClientName
            };
        }

        protected override void OnInsertingMany(IDbConnection connection, IDbTransaction transaction, IList<MovieEditionSubmission> submissions)
        {
            MovieEditionGrabGuard.LockParents(connection, transaction, submissions.Select(s => s.MovieId));
        }

        protected override void OnInserting(IDbConnection connection, IDbTransaction transaction, MovieEditionSubmission submission)
        {
            if (submission.OperationId == null || !MovieEditionOperationIdentity.IsValid(submission.OperationId) ||
                submission.State != MovieEditionSubmissionState.Prepared || submission.DownloadId != null ||
                submission.EditionRuleRevision <= 0 || submission.QualityProfileId <= 0 || string.IsNullOrWhiteSpace(submission.MoviePath) ||
                submission.DownloadClientId <= 0 || string.IsNullOrWhiteSpace(submission.DownloadClientName) ||
                string.IsNullOrWhiteSpace(submission.Release?.Title) || submission.ParsedMovieInfo == null ||
                submission.CreatedAt.Kind != DateTimeKind.Utc || submission.UpdatedAt != submission.CreatedAt)
            {
                throw new InvalidOperationException("Provide complete immutable prepared submission intent before client IO.");
            }

            MovieEditionGrabGuard.LockAndValidate(connection, transaction, submission.MovieId, submission.MovieEditionId, submission.OperationId);
            var movie = connection.QuerySingle<Movie>(@"SELECT * FROM ""Movies"" WHERE ""Id"" = @MovieId", submission, transaction);
            var edition = connection.QuerySingle<MovieEdition>(@"SELECT * FROM ""MovieEditions"" WHERE ""Id"" = @MovieEditionId", submission, transaction);
            if (!movie.Monitored || !edition.Monitored || movie.Path != submission.MoviePath || edition.MovieFileId != submission.MovieFileId ||
                edition.QualityProfileId != submission.QualityProfileId || edition.RuleRevision != submission.EditionRuleRevision ||
                edition.Name != submission.EditionName || submission.EditionAttributes == null ||
                edition.Attributes.Count != submission.EditionAttributes.Count ||
                edition.Attributes.Any(a => !submission.EditionAttributes.TryGetValue(a.Key, out var value) || value != a.Value))
            {
                throw new InvalidOperationException("The movie or edition changed before submission intent was prepared.");
            }

            QualityProfileReferenceGuard.Lock(connection, transaction, _database.DatabaseType, submission.QualityProfileId);
            if (submission.MovieFileId.HasValue && connection.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM ""MovieFiles"" WHERE ""Id"" = @MovieFileId AND ""MovieId"" = @MovieId AND ""MovieEditionId"" = @MovieEditionId",
                submission,
                transaction) != 1)
            {
                throw new InvalidOperationException("Repair current-file ownership before preparing submission intent.");
            }
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, MovieEditionSubmission model, List<string> properties)
        {
            throw new InvalidOperationException("Submission intent is immutable; use validated journal transitions.");
        }

        protected override void OnDeleted(IDbConnection connection, IDbTransaction transaction)
        {
            throw new InvalidOperationException("Submission evidence cannot be removed through ordinary repository deletion.");
        }

        public MovieEditionSubmission Accept(string operationId, string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                throw new InvalidOperationException("A client acceptance requires a positive download identity; otherwise retain prepared intent for review.");
            }

            return Transition(operationId, MovieEditionSubmissionState.Accepted, downloadId);
        }

        public MovieEditionSubmission Record(string operationId) => Transition(operationId, MovieEditionSubmissionState.Recorded);

        public MovieEditionSubmission RecordGrabs(string operationId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            // Start with a write: SQLite cannot safely upgrade a stale read snapshot under concurrent repair.
            var parent = connection.Execute(
                @"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" =
                    (SELECT ""MovieId"" FROM ""MovieEditionSubmissions"" WHERE ""OperationId"" = @OperationId)",
                new { OperationId = operationId },
                transaction);
            if (parent != 1)
            {
                throw new InvalidOperationException("Unknown submission operation or missing movie; review the saved intent.");
            }

            var submission = LockSubmission(connection, transaction, operationId);
            if (submission.State is not (MovieEditionSubmissionState.Accepted or MovieEditionSubmissionState.Recorded))
            {
                throw new InvalidOperationException("Grab persistence requires accepted unresolved submission intent.");
            }

            if (submission.HistoryData == null || submission.DownloadHistoryData == null || submission.Languages == null || string.IsNullOrWhiteSpace(submission.DownloadId))
            {
                throw new InvalidOperationException("Submission lacks complete prepared grab payloads; review it manually.");
            }

            var message = CreateGrabEvent(submission);
            var movieGrab = HistoryService.CreateGrabHistory(message);
            var downloadGrab = DownloadHistoryService.CreateGrabHistory(message);
            movieGrab.Data = submission.HistoryData;
            downloadGrab.Data = submission.DownloadHistoryData;
            var existingMovie = connection.QuerySingleOrDefault<MovieHistory>(@"SELECT * FROM ""History"" WHERE ""EditionOperationId"" = @OperationId AND ""EventType"" = 1", submission, transaction);
            var existingDownload = connection.QuerySingleOrDefault<DownloadHistory>(@"SELECT * FROM ""DownloadHistory"" WHERE ""EditionOperationId"" = @OperationId AND ""EventType"" = 1", submission, transaction);
            if (existingMovie == null)
            {
                _history.Insert(connection, transaction, movieGrab);
            }
            else if (!SameGrab(existingMovie, movieGrab))
            {
                throw new InvalidOperationException("Existing movie-history grab conflicts with immutable prepared payload.");
            }

            if (existingDownload == null)
            {
                _downloads.Insert(connection, transaction, downloadGrab);
            }
            else if (!SameGrab(existingDownload, downloadGrab))
            {
                throw new InvalidOperationException("Existing download-history grab conflicts with immutable prepared payload.");
            }

            if (submission.State != MovieEditionSubmissionState.Recorded || existingMovie == null || existingDownload == null)
            {
                submission.State = MovieEditionSubmissionState.Recorded;
                submission.UpdatedAt = DateTime.UtcNow;
                connection.Execute(@"UPDATE ""MovieEditionSubmissions"" SET ""State"" = @State, ""UpdatedAt"" = @UpdatedAt WHERE ""Id"" = @Id", submission, transaction);
            }

            submission = TrySettle(connection, transaction, operationId) ?? submission;
            transaction.Commit();
            return submission;
        }

        public MovieEditionSubmission Settle(string operationId) => Transition(operationId, MovieEditionSubmissionState.Settled);

        internal static MovieEditionSubmission TrySettle(IDbConnection connection, IDbTransaction transaction, string operationId)
        {
            if (operationId == null || !MovieEditionOperationIdentity.IsValid(operationId) || connection.Execute(
                @"UPDATE ""MovieEditionSubmissions"" SET ""State"" = ""State"" WHERE ""OperationId"" = @OperationId",
                new { OperationId = operationId },
                transaction) != 1)
            {
                return null;
            }

            var submission = LockSubmission(connection, transaction, operationId);
            if (submission.State != MovieEditionSubmissionState.Recorded || !HasMatchingTerminal(connection, transaction, submission))
            {
                return null;
            }

            submission.State = MovieEditionSubmissionState.Settled;
            submission.UpdatedAt = DateTime.UtcNow;
            connection.Execute(@"UPDATE ""MovieEditionSubmissions"" SET ""State"" = @State, ""UpdatedAt"" = @UpdatedAt WHERE ""Id"" = @Id", submission, transaction);
            return submission;
        }

        private static bool HasMatchingTerminal(IDbConnection connection, IDbTransaction transaction, MovieEditionSubmission submission)
        {
            if (!TryGetGrabDate(connection, transaction, "History", submission, true, out var movieGrabDate) ||
                !TryGetGrabDate(connection, transaction, "DownloadHistory", submission, false, out var downloadGrabDate))
            {
                return false;
            }

            var terminals = connection.Query<DownloadHistory>(
                @"SELECT * FROM ""DownloadHistory"" WHERE ""EditionOperationId"" = @OperationId AND ""EventType"" IN (2,3,4)",
                submission,
                transaction).ToList();

            // A client may rename a job, so its terminal title need not equal the release title.
            // Shared operation identity, original target/name/client and ordering must still agree.
            return terminals.Count > 0 && terminals.All(terminal => terminal.MovieId == submission.MovieId &&
                terminal.MovieEditionId == submission.MovieEditionId && terminal.EditionRuleRevision == submission.EditionRuleRevision &&
                terminal.DownloadClientId == submission.DownloadClientId && terminal.DownloadId == submission.DownloadId &&
                terminal.Data?.GetValueOrDefault("movieEditionName") == submission.EditionName &&
                MovieEditionGrabGuard.TimestampMicroseconds(terminal.Date) > MovieEditionGrabGuard.TimestampMicroseconds(movieGrabDate) &&
                MovieEditionGrabGuard.TimestampMicroseconds(terminal.Date) > MovieEditionGrabGuard.TimestampMicroseconds(downloadGrabDate));
        }

        private MovieEditionSubmission Transition(string operationId, MovieEditionSubmissionState next, string downloadId = null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            var submission = LockSubmission(connection, transaction, operationId);
            if (submission.State >= next)
            {
                if (downloadId != null && downloadId != submission.DownloadId)
                {
                    throw new InvalidOperationException("Client acceptance disagrees with the saved submission identity.");
                }

                transaction.Commit();
                return submission;
            }

            if ((int)next != (int)submission.State + 1)
            {
                throw new InvalidOperationException("Submission journal transitions cannot skip durable evidence.");
            }

            if (next >= MovieEditionSubmissionState.Recorded)
            {
                RequireGrab(connection, transaction, "History", submission, true);
                RequireGrab(connection, transaction, "DownloadHistory", submission, false);
            }

            if (next == MovieEditionSubmissionState.Settled && !HasMatchingTerminal(connection, transaction, submission))
            {
                throw new InvalidOperationException("Submission settlement requires matching terminal evidence.");
            }

            submission.State = next;
            submission.DownloadId = downloadId ?? submission.DownloadId;
            submission.UpdatedAt = DateTime.UtcNow;
            connection.Execute(@"UPDATE ""MovieEditionSubmissions"" SET ""State"" = @State, ""DownloadId"" = @DownloadId, ""UpdatedAt"" = @UpdatedAt WHERE ""Id"" = @Id", submission, transaction);
            transaction.Commit();
            return submission;
        }

        private static MovieEditionSubmission LockSubmission(IDbConnection connection, IDbTransaction transaction, string operationId)
        {
            // Lock the journal row first; prepared intent already prevents target removal.
            var found = connection.Execute(@"UPDATE ""MovieEditionSubmissions"" SET ""State"" = ""State"" WHERE ""OperationId"" = @OperationId", new { OperationId = operationId }, transaction);
            if (found != 1)
            {
                throw new InvalidOperationException("Unknown submission operation.");
            }

            var submission = connection.QuerySingle<MovieEditionSubmission>(@"SELECT * FROM ""MovieEditionSubmissions"" WHERE ""OperationId"" = @OperationId", new { OperationId = operationId }, transaction);
            if (!Enum.IsDefined(submission.State) || !MovieEditionOperationIdentity.IsValid(submission.OperationId))
            {
                throw new InvalidOperationException("Invalid persisted submission state requires review.");
            }

            return submission;
        }

        private static bool SameData(Dictionary<string, string> left, Dictionary<string, string> right) => left != null && right != null &&
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private static bool SameGrab(MovieHistory stored, MovieHistory expected) => stored.MovieId == expected.MovieId &&
            stored.MovieEditionId == expected.MovieEditionId && stored.EditionRuleRevision == expected.EditionRuleRevision &&
            stored.EditionOperationId == expected.EditionOperationId && stored.DownloadId == expected.DownloadId && stored.SourceTitle == expected.SourceTitle &&
            MovieEditionGrabGuard.TimestampMicroseconds(stored.Date) == MovieEditionGrabGuard.TimestampMicroseconds(expected.Date) &&
            Equals(stored.Quality, expected.Quality) && (stored.Languages ?? new()).SequenceEqual(expected.Languages ?? new()) && SameData(stored.Data, expected.Data);

        private static bool SameGrab(DownloadHistory stored, DownloadHistory expected) => stored.MovieId == expected.MovieId &&
            stored.MovieEditionId == expected.MovieEditionId && stored.EditionRuleRevision == expected.EditionRuleRevision &&
            stored.EditionOperationId == expected.EditionOperationId && stored.DownloadId == expected.DownloadId && stored.SourceTitle == expected.SourceTitle &&
            MovieEditionGrabGuard.TimestampMicroseconds(stored.Date) == MovieEditionGrabGuard.TimestampMicroseconds(expected.Date) &&
            stored.DownloadClientId == expected.DownloadClientId && stored.Protocol == expected.Protocol && stored.IndexerId == expected.IndexerId &&
            SameRelease(stored.Release, expected.Release) && SameData(stored.Data, expected.Data);

        private static bool SameRelease(ReleaseInfo stored, ReleaseInfo expected)
        {
            var left = JsonSerializer.SerializeToNode((object)stored, ReleaseComparisonOptions)?.AsObject();
            var right = JsonSerializer.SerializeToNode((object)expected, ReleaseComparisonOptions)?.AsObject();
            foreach (var name in new[] { nameof(ReleaseInfo.Age), nameof(ReleaseInfo.AgeHours), nameof(ReleaseInfo.AgeMinutes) })
            {
                left?.Remove(name);
                right?.Remove(name);
            }

            return JsonNode.DeepEquals(left, right);
        }

        private static void RequireGrab(IDbConnection connection, IDbTransaction transaction, string table, MovieEditionSubmission submission, bool movieHistory)
        {
            if (!TryGetGrabDate(connection, transaction, table, submission, movieHistory, out _))
            {
                throw new InvalidOperationException("Recording submission requires both matching durable grab streams.");
            }
        }

        private static bool TryGetGrabDate(IDbConnection connection, IDbTransaction transaction, string table, MovieEditionSubmission submission, bool movieHistory, out DateTime date)
        {
            var sql = $@"SELECT * FROM ""{table}"" WHERE ""EditionOperationId"" = @OperationId AND ""EventType"" = 1";
            date = default;
            bool matches;
            if (movieHistory)
            {
                var grab = connection.QuerySingleOrDefault<MovieHistory>(sql, submission, transaction);
                date = grab?.Date ?? default;
                matches = grab != null && grab.MovieId == submission.MovieId && grab.MovieEditionId == submission.MovieEditionId &&
                    grab.EditionRuleRevision == submission.EditionRuleRevision && grab.DownloadId == submission.DownloadId &&
                    grab.SourceTitle == submission.Release.Title && grab.Data?.GetValueOrDefault("movieEditionName") == submission.EditionName &&
                    int.TryParse(grab.Data?.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), out var clientId) && clientId == submission.DownloadClientId;
            }
            else
            {
                var grab = connection.QuerySingleOrDefault<DownloadHistory>(sql, submission, transaction);
                date = grab?.Date ?? default;
                matches = grab != null && grab.MovieId == submission.MovieId && grab.MovieEditionId == submission.MovieEditionId &&
                    grab.EditionRuleRevision == submission.EditionRuleRevision && grab.DownloadId == submission.DownloadId &&
                    grab.SourceTitle == submission.Release.Title && grab.Data?.GetValueOrDefault("movieEditionName") == submission.EditionName &&
                    grab.DownloadClientId == submission.DownloadClientId;
            }

            return matches;
        }
    }
}
