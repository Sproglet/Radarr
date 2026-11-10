using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionWantedRepository
    {
        PagingSpec<MovieEditionWanted> Missing(PagingSpec<MovieEditionWanted> page, bool monitored);
        PagingSpec<MovieEditionWanted> Cutoff(PagingSpec<MovieEditionWanted> page, bool monitored, List<QualitiesBelowCutoff> profiles);
    }

    public class MovieEditionWantedRepository : IMovieEditionWantedRepository
    {
        private readonly IMainDatabase _database;
        private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "metadata.\"SortTitle\"",
            ["year"] = "metadata.\"Year\"",
            ["editionName"] = "e.\"Name\"",
            ["lastSearchTime"] = "e.\"LastSearchTime\""
        };

        private const string Joins = @" FROM ""MovieEditions"" e
            JOIN ""Movies"" m ON m.""Id"" = e.""MovieId""
            JOIN ""MovieMetadata"" metadata ON metadata.""Id"" = m.""MovieMetadataId""
            LEFT JOIN ""QualityProfiles"" profile ON profile.""Id"" = e.""QualityProfileId""
            LEFT JOIN ""MovieFiles"" f ON f.""Id"" = e.""MovieFileId""
                AND f.""MovieId"" = e.""MovieId"" AND f.""MovieEditionId"" = e.""Id"" ";

        public MovieEditionWantedRepository(IMainDatabase database)
        {
            _database = database;
        }

        public PagingSpec<MovieEditionWanted> Missing(PagingSpec<MovieEditionWanted> page, bool monitored) =>
            Query(page, monitored, "f.\"Id\" IS NULL");

        public PagingSpec<MovieEditionWanted> Cutoff(PagingSpec<MovieEditionWanted> page, bool monitored, List<QualitiesBelowCutoff> profiles) =>
            Query(page, monitored, "profile.\"Id\" IS NOT NULL AND f.\"Id\" IS NOT NULL AND " +
                QualityCutoffSql.Where(_database.DatabaseType, profiles, "e", "f"));

        private PagingSpec<MovieEditionWanted> Query(PagingSpec<MovieEditionWanted> page, bool monitored, string predicate)
        {
            if (page == null || page.Page < 1 || page.PageSize < 1 || page.PageSize > 100 ||
                page.SortKey == null || !SortColumns.TryGetValue(page.SortKey, out var sortColumn) ||
                !Enum.IsDefined(page.SortDirection))
            {
                throw new ArgumentException("Use a positive page, a page size from 1 to 100 and a supported edition sort order.");
            }

            var direction = page.SortDirection == SortDirection.Descending ? "DESC" : "ASC";
            // monitored=false is the complement of effective monitoring, including a disabled edition under an enabled parent.
            var where = " WHERE (m.\"Monitored\" AND e.\"Monitored\") = @Monitored AND (" + predicate + ")";
            var parameters = new { Monitored = monitored, Limit = page.PageSize, Offset = ((long)page.Page - 1) * page.PageSize };
            using var connection = _database.OpenConnection();
            page.TotalRecords = connection.QuerySingle<int>("SELECT COUNT(*)" + Joins + where, parameters);
            page.Records = connection.Query<MovieEditionWanted>(@"SELECT e.*,
                metadata.""Title"" AS ""MovieTitle"", metadata.""Year"" AS ""Year"", metadata.""TmdbId"" AS ""TmdbId"",
                m.""Monitored"" AS ""MovieMonitored"", m.""PrimaryEditionId"" AS ""PrimaryEditionId"",
                (profile.""Id"" IS NOT NULL) AS ""ProfileExists"", profile.""Name"" AS ""QualityProfileName"",
                f.""Id"" AS ""ResolvedMovieFileId"", f.""RelativePath"" AS ""RelativePath"", f.""Quality"" AS ""Quality""" +
                Joins + where + $" ORDER BY {sortColumn} {direction} NULLS LAST, e.\"Id\" ASC LIMIT @Limit OFFSET @Offset", parameters).ToList();
            return page;
        }
    }
}
