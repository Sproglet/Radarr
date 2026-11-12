using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Download
{
    public interface IMovieEditionSubmissionRepository : IBasicRepository<MovieEditionSubmission>
    {
        MovieEditionSubmission Prepare(MovieEditionSubmission submission);
        MovieEditionSubmission Accept(string operationId, string downloadId);
        MovieEditionSubmission Record(string operationId);
        MovieEditionSubmission Settle(string operationId);
    }

    public class MovieEditionSubmissionRepository : BasicRepository<MovieEditionSubmission>, IMovieEditionSubmissionRepository
    {
        public MovieEditionSubmissionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        public MovieEditionSubmission Prepare(MovieEditionSubmission submission)
        {
            submission.OperationId ??= Guid.NewGuid().ToString("D");
            submission.CreatedAt = DateTime.UtcNow;
            submission.UpdatedAt = submission.CreatedAt;
            return Insert(submission);
        }

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, MovieEditionSubmission submission)
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

        public MovieEditionSubmission Settle(string operationId) => Transition(operationId, MovieEditionSubmissionState.Settled);

        private MovieEditionSubmission Transition(string operationId, MovieEditionSubmissionState next, string downloadId = null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

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

            if (next == MovieEditionSubmissionState.Settled && connection.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM ""DownloadHistory"" WHERE ""EditionOperationId"" = @OperationId AND ""MovieId"" = @MovieId
                    AND ""MovieEditionId"" = @MovieEditionId AND ""EditionRuleRevision"" = @EditionRuleRevision
                    AND ""DownloadClientId"" = @DownloadClientId AND ""DownloadId"" = @DownloadId AND ""EventType"" IN (2,3,4)",
                submission,
                transaction) == 0)
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

        private static void RequireGrab(IDbConnection connection, IDbTransaction transaction, string table, MovieEditionSubmission submission, bool movieHistory)
        {
            var sql = $@"SELECT * FROM ""{table}"" WHERE ""EditionOperationId"" = @OperationId AND ""EventType"" = 1";
            bool matches;
            if (movieHistory)
            {
                var grab = connection.QuerySingleOrDefault<MovieHistory>(sql, submission, transaction);
                matches = grab != null && grab.MovieId == submission.MovieId && grab.MovieEditionId == submission.MovieEditionId &&
                    grab.EditionRuleRevision == submission.EditionRuleRevision && grab.DownloadId == submission.DownloadId &&
                    grab.SourceTitle == submission.Release.Title && grab.Data?.GetValueOrDefault("movieEditionName") == submission.EditionName &&
                    int.TryParse(grab.Data?.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), out var clientId) && clientId == submission.DownloadClientId;
            }
            else
            {
                var grab = connection.QuerySingleOrDefault<DownloadHistory>(sql, submission, transaction);
                matches = grab != null && grab.MovieId == submission.MovieId && grab.MovieEditionId == submission.MovieEditionId &&
                    grab.EditionRuleRevision == submission.EditionRuleRevision && grab.DownloadId == submission.DownloadId &&
                    grab.SourceTitle == submission.Release.Title && grab.Data?.GetValueOrDefault("movieEditionName") == submission.EditionName &&
                    grab.DownloadClientId == submission.DownloadClientId;
            }

            if (!matches)
            {
                throw new InvalidOperationException("Recording submission requires both matching durable grab streams.");
            }
        }
    }
}
