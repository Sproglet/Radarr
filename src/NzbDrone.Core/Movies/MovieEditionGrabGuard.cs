using System;
using System.Data;
using Dapper;

namespace NzbDrone.Core.Movies
{
    internal static class MovieEditionGrabGuard
    {
        public static void LockAndValidate(IDbConnection connection, IDbTransaction transaction, int movieId, int? editionId)
        {
            if (!editionId.HasValue)
            {
                return;
            }

            var parent = connection.Execute(
                @"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId",
                new { MovieId = movieId },
                transaction);
            if (parent != 1 || connection.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM ""MovieEditions"" WHERE ""Id"" = @EditionId AND ""MovieId"" = @MovieId",
                new { MovieId = movieId, EditionId = editionId },
                transaction) != 1)
            {
                throw new InvalidOperationException("Grab edition must belong to its movie.");
            }
        }
    }
}
