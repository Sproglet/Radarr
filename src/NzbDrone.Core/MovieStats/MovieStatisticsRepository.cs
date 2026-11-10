using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MovieStats
{
    public interface IMovieStatisticsRepository
    {
        List<MovieStatistics> MovieStatistics();
        List<MovieStatistics> MovieStatistics(int movieId);
    }

    public class MovieStatisticsRepository : IMovieStatisticsRepository
    {
        private readonly IMainDatabase _database;

        public MovieStatisticsRepository(IMainDatabase database)
        {
            _database = database;
        }

        public List<MovieStatistics> MovieStatistics() => Query(null);

        public List<MovieStatistics> MovieStatistics(int movieId) => Query(movieId);

        private List<MovieStatistics> Query(int? movieId)
        {
            var releaseGroups = _database.DatabaseType == DatabaseType.SQLite
                ? "GROUP_CONCAT(\"ReleaseGroup\", '|')"
                : "string_agg(DISTINCT \"ReleaseGroup\", '|')";
            var qualities = _database.DatabaseType == DatabaseType.SQLite
                ? "GROUP_CONCAT(JSON_EXTRACT(\"Quality\", '$.quality'), '|')"
                : "string_agg(DISTINCT \"Quality\"::json->>'quality', '|')";
            var movieFilter = movieId.HasValue ? @"WHERE m.""Id"" = @movieId" : "";
            var fileFilter = movieId.HasValue ? @"WHERE ""MovieId"" = @movieId" : "";
            var editionFilter = movieId.HasValue ? @"WHERE e.""MovieId"" = @movieId" : "";

            // Aggregate files and editions separately, so extra targets cannot multiply bytes or file counts.
            // Retained/unassigned file records count toward deletion/disk totals, never edition availability.
            var sql = $@"SELECT m.""Id"" AS MovieId,
                    COALESCE(f.MovieFileCount, 0) AS MovieFileCount,
                    COALESCE(f.SizeOnDisk, 0) AS SizeOnDisk,
                    f.ReleaseGroupsString, f.MovieFileQualitiesString,
                    COALESCE(e.EditionCount, 0) AS EditionCount,
                    COALESCE(e.EditionFileCount, 0) AS EditionFileCount,
                    CASE WHEN m.""Monitored"" THEN COALESCE(e.MonitoredEditionCount, 0) ELSE 0 END AS MonitoredEditionCount,
                    CASE WHEN m.""Monitored"" THEN COALESCE(e.MissingMonitoredEditionCount, 0) ELSE 0 END AS MissingMonitoredEditionCount
                FROM ""Movies"" m
                LEFT JOIN (
                    SELECT ""MovieId"", COUNT(*) AS MovieFileCount, SUM(COALESCE(""Size"", 0)) AS SizeOnDisk,
                        {releaseGroups} AS ReleaseGroupsString, {qualities} AS MovieFileQualitiesString
                    FROM ""MovieFiles"" {fileFilter} GROUP BY ""MovieId""
                ) f ON f.""MovieId"" = m.""Id""
                LEFT JOIN (
                    SELECT e.""MovieId"", COUNT(*) AS EditionCount, COUNT(current_file.""Id"") AS EditionFileCount,
                        SUM(CASE WHEN e.""Monitored"" THEN 1 ELSE 0 END) AS MonitoredEditionCount,
                        SUM(CASE WHEN e.""Monitored"" AND current_file.""Id"" IS NULL THEN 1 ELSE 0 END) AS MissingMonitoredEditionCount
                    FROM ""MovieEditions"" e
                    LEFT JOIN ""MovieFiles"" current_file ON current_file.""Id"" = e.""MovieFileId""
                        AND current_file.""MovieId"" = e.""MovieId"" AND current_file.""MovieEditionId"" = e.""Id""
                    {editionFilter} GROUP BY e.""MovieId""
                ) e ON e.""MovieId"" = m.""Id""
                {movieFilter}";
            using var connection = _database.OpenConnection();
            return connection.Query<MovieStatistics>(sql, new { movieId }).ToList();
        }
    }
}
