using System;
using System.Collections.Generic;
using System.Data;
using Dapper;

namespace NzbDrone.Core.Movies
{
    internal static class MovieEditionGrabGuard
    {
        // PostgreSQL stores microseconds; an unchanged in-memory grab can retain sub-microsecond ticks.
        public static long TimestampMicroseconds(DateTime date) => date.Ticks / 10;

        public static void ValidateSnapshot(List<string> properties, params (string Property, object Stored, object Proposed)[] fields)
        {
            foreach (var field in fields)
            {
                if (properties.Contains(field.Property) && !Equals(field.Stored, field.Proposed))
                {
                    throw new InvalidOperationException("Persisted edition grab identity cannot be changed through ordinary history updates.");
                }
            }
        }

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
