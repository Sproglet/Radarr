using System.Data;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Profiles.Qualities
{
    internal static class QualityProfileReferenceGuard
    {
        public static void Lock(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, int profileId)
        {
            // SQLite's enclosing write transaction serializes deletion. PostgreSQL needs
            // a key-share lock held until the referencing write commits, as with a foreign key.
            var profileLock = databaseType == DatabaseType.PostgreSQL ? " FOR KEY SHARE" : "";
            var found = connection.QuerySingleOrDefault<int?>($@"SELECT ""Id"" FROM ""QualityProfiles""
                WHERE ""Id"" = @ProfileId{profileLock}", new { ProfileId = profileId }, transaction);
            if (!found.HasValue)
            {
                throw new ModelNotFoundException(typeof(QualityProfile), profileId);
            }
        }
    }
}
