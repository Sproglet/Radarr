using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.History
{
    public interface IHistoryRepository : IBasicRepository<MovieHistory>
    {
        List<QualityModel> GetBestQualityInHistory(int movieId);
        MovieHistory MostRecentForMovie(int movieId);
        MovieHistory MostRecentForEdition(int movieId, int? editionId);
        List<MovieHistory> FindByMovieId(int movieId);
        MovieHistory MostRecentForDownloadId(string downloadId);
        List<MovieHistory> FindByDownloadId(string downloadId);
        List<MovieHistory> FindDownloadHistory(int movieId, QualityModel quality);
        List<MovieHistory> GetByMovieId(int movieId, MovieHistoryEventType? eventType);
        void DeleteForMovies(List<int> movieIds);
        List<MovieHistory> Since(DateTime date, MovieHistoryEventType? eventType);
        PagingSpec<MovieHistory> GetPaged(PagingSpec<MovieHistory> pagingSpec, int[] languages, int[] qualities);
    }

    public class HistoryRepository : BasicRepository<MovieHistory>, IHistoryRepository
    {
        public HistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, MovieHistory history)
        {
            if (history.EventType == MovieHistoryEventType.Grabbed)
            {
                MovieEditionGrabGuard.LockAndValidate(connection, transaction, history.MovieId, history.MovieEditionId, history.EditionOperationId);
            }
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, MovieHistory history, List<string> properties)
        {
            var stored = connection.QuerySingle<MovieHistory>(@"SELECT * FROM ""History"" WHERE ""Id"" = @Id", history, transaction);
            if (stored.MovieEditionId.HasValue && (stored.EventType == MovieHistoryEventType.Grabbed || stored.EditionOperationId != null))
            {
                MovieEditionGrabGuard.ValidateSnapshot(
                    properties,
                    (nameof(MovieHistory.MovieId), stored.MovieId, history.MovieId),
                    (nameof(MovieHistory.MovieEditionId), stored.MovieEditionId, history.MovieEditionId),
                    (nameof(MovieHistory.EditionRuleRevision), stored.EditionRuleRevision, history.EditionRuleRevision),
                    (nameof(MovieHistory.EditionOperationId), stored.EditionOperationId, history.EditionOperationId),
                    (nameof(MovieHistory.DownloadId), stored.DownloadId, history.DownloadId),
                    (nameof(MovieHistory.SourceTitle), stored.SourceTitle, history.SourceTitle),
                    (nameof(MovieHistory.Date), MovieEditionGrabGuard.TimestampMicroseconds(stored.Date), MovieEditionGrabGuard.TimestampMicroseconds(history.Date)),
                    (nameof(MovieHistory.EventType), stored.EventType, history.EventType),
                    (nameof(MovieHistory.Data), stored.Data?.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), history.Data?.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID)),
                    (nameof(MovieHistory.Data), stored.Data?.GetValueOrDefault("movieEditionName"), history.Data?.GetValueOrDefault("movieEditionName")));
                return;
            }

            var eventType = properties.Contains(nameof(MovieHistory.EventType)) ? history.EventType : stored.EventType;
            if (eventType == MovieHistoryEventType.Grabbed)
            {
                var movieId = properties.Contains(nameof(MovieHistory.MovieId)) ? history.MovieId : stored.MovieId;
                var editionId = properties.Contains(nameof(MovieHistory.MovieEditionId)) ? history.MovieEditionId : stored.MovieEditionId;
                var operationId = properties.Contains(nameof(MovieHistory.EditionOperationId)) ? history.EditionOperationId : stored.EditionOperationId;
                MovieEditionGrabGuard.LockAndValidate(connection, transaction, movieId, editionId, operationId);
            }
        }

        public List<QualityModel> GetBestQualityInHistory(int movieId)
        {
            var history = Query(x => x.MovieId == movieId);

            return history.Select(h => h.Quality).ToList();
        }

        public MovieHistory MostRecentForMovie(int movieId)
        {
            return Query(x => x.MovieId == movieId).MaxBy(h => h.Date);
        }

        public MovieHistory MostRecentForEdition(int movieId, int? editionId)
        {
            return Query(x => x.MovieId == movieId && x.MovieEditionId == editionId).MaxBy(h => h.Date);
        }

        public List<MovieHistory> FindByMovieId(int movieId)
        {
            return Query(h => h.MovieId == movieId)
                .OrderByDescending(h => h.Date)
                .ToList();
        }

        public MovieHistory MostRecentForDownloadId(string downloadId)
        {
            return FindByDownloadId(downloadId).MaxBy(h => h.Date);
        }

        public List<MovieHistory> FindByDownloadId(string downloadId)
        {
            return Query(x => x.DownloadId == downloadId);
        }

        public List<MovieHistory> FindDownloadHistory(int movieId, QualityModel quality)
        {
            var allowed = new[] { (int)MovieHistoryEventType.Grabbed, (int)MovieHistoryEventType.DownloadFailed, (int)MovieHistoryEventType.DownloadFolderImported };

            return Query(h => h.MovieId == movieId &&
                         h.Quality == quality &&
                         allowed.Contains((int)h.EventType));
        }

        public List<MovieHistory> GetByMovieId(int movieId, MovieHistoryEventType? eventType)
        {
            var builder = new SqlBuilder(_database.DatabaseType)
                .Join<MovieHistory, Movie>((h, m) => h.MovieId == m.Id)
                .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
                .Where<MovieHistory>(h => h.MovieId == movieId);

            if (eventType.HasValue)
            {
                builder.Where<MovieHistory>(h => h.EventType == eventType);
            }

            return PagedQuery(builder).OrderByDescending(h => h.Date).ToList();
        }

        public void DeleteForMovies(List<int> movieIds)
        {
            Delete(c => movieIds.Contains(c.MovieId));
        }

        public List<MovieHistory> Since(DateTime date, MovieHistoryEventType? eventType)
        {
            var builder = new SqlBuilder(_database.DatabaseType)
                .Join<MovieHistory, Movie>((h, m) => h.MovieId == m.Id)
                .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
                .Where<MovieHistory>(x => x.Date >= date);

            if (eventType.HasValue)
            {
                builder.Where<MovieHistory>(h => h.EventType == eventType);
            }

            return PagedQuery(builder).OrderBy(h => h.Date).ToList();
        }

        public PagingSpec<MovieHistory> GetPaged(PagingSpec<MovieHistory> pagingSpec, int[] languages, int[] qualities)
        {
            pagingSpec.Records = GetPagedRecords(PagedBuilder(languages, qualities), pagingSpec, PagedQuery);

            var countTemplate = $"SELECT COUNT(*) FROM (SELECT /**select**/ FROM \"{TableMapping.Mapper.TableNameMapping(typeof(MovieHistory))}\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/) AS \"Inner\"";
            pagingSpec.TotalRecords = GetPagedRecordCount(PagedBuilder(languages, qualities).Select(typeof(MovieHistory)), pagingSpec, countTemplate);

            return pagingSpec;
        }

        private SqlBuilder PagedBuilder(int[] languages, int[] qualities)
        {
            var builder = Builder()
                .Join<MovieHistory, Movie>((h, m) => h.MovieId == m.Id)
                .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
                .LeftJoin<Movie, MovieMetadata>((m, mm) => m.MovieMetadataId == mm.Id);

            if (languages is { Length: > 0 })
            {
                builder.Where($"({BuildLanguageWhereClause(languages)})");
            }

            if (qualities is { Length: > 0 })
            {
                builder.Where($"({BuildQualityWhereClause(qualities)})");
            }

            return builder;
        }

        protected override IEnumerable<MovieHistory> PagedQuery(SqlBuilder builder) =>
            _database.QueryJoined<MovieHistory, Movie, QualityProfile, MovieFile>(
                builder.LeftJoin<Movie, MovieFile>((m, f) => m.MovieFileId == f.Id && m.Id == f.MovieId), (hist, movie, profile, file) =>
            {
                hist.Movie = movie;
                hist.Movie.QualityProfile = profile;
                hist.Movie.MovieFile = file;
                return hist;
            });

        private string BuildLanguageWhereClause(int[] languages)
        {
            var clauses = new List<string>();

            foreach (var language in languages)
            {
                // There are 4 different types of values we should see:
                // - Not the last value in the array
                // - When it's the last value in the array and on different OSes
                // - When it was converted from a single language

                clauses.Add($"\"{TableMapping.Mapper.TableNameMapping(typeof(MovieHistory))}\".\"Languages\" LIKE '[% {language},%]'");
                clauses.Add($"\"{TableMapping.Mapper.TableNameMapping(typeof(MovieHistory))}\".\"Languages\" LIKE '[% {language}' || CHAR(13) || '%]'");
                clauses.Add($"\"{TableMapping.Mapper.TableNameMapping(typeof(MovieHistory))}\".\"Languages\" LIKE '[% {language}' || CHAR(10) || '%]'");
                clauses.Add($"\"{TableMapping.Mapper.TableNameMapping(typeof(MovieHistory))}\".\"Languages\" LIKE '[{language}]'");
            }

            return $"({string.Join(" OR ", clauses)})";
        }

        private string BuildQualityWhereClause(int[] qualities)
        {
            var clauses = new List<string>();

            foreach (var quality in qualities)
            {
                clauses.Add($"\"{TableMapping.Mapper.TableNameMapping(typeof(MovieHistory))}\".\"Quality\" LIKE '%_quality_: {quality},%'");
            }

            return $"({string.Join(" OR ", clauses)})";
        }
    }
}
