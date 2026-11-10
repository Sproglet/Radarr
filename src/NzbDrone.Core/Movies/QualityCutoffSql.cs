using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Movies
{
    internal static class QualityCutoffSql
    {
        public static string Where(DatabaseType databaseType, List<QualitiesBelowCutoff> profiles, string profileTable, string fileTable)
        {
            var quality = databaseType == DatabaseType.SQLite
                ? $"JSON_EXTRACT(\"{fileTable}\".\"Quality\", '$.quality')"
                : $"CAST(\"{fileTable}\".\"Quality\"::json->>'quality' AS INTEGER)";
            // Aliases are fixed by callers; only typed integer IDs enter the predicate.
            var clauses = profiles.SelectMany(profile => profile.QualityIds.Select(id =>
                $"(\"{profileTable}\".\"QualityProfileId\" = {profile.ProfileId} AND {quality} = {id})")).ToList();
            return clauses.Count == 0 ? "1 = 0" : "(" + string.Join(" OR ", clauses) + ")";
        }
    }
}
