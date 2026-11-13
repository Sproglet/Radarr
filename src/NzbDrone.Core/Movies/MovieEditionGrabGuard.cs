using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;

namespace NzbDrone.Core.Movies
{
    internal static class MovieEditionGrabGuard
    {
        // PostgreSQL stores microseconds; an unchanged in-memory grab can retain sub-microsecond ticks.
        public static long TimestampMicroseconds(DateTime date) => date.Ticks / 10;

        public static int LockParent(IDbConnection connection, IDbTransaction transaction, int movieId) => connection.Execute(
            @"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId",
            new { MovieId = movieId },
            transaction);

        public static void LockParents(IDbConnection connection, IDbTransaction transaction, IEnumerable<int> movieIds)
        {
            foreach (var movieId in movieIds.Where(id => id > 0).Distinct().OrderBy(id => id))
            {
                LockParent(connection, transaction, movieId);
            }
        }

        public static void ValidateSnapshot(List<string> properties, params (string Property, object Stored, object Proposed)[] fields)
            => ValidateSnapshot("grab identity", properties, fields);

        public static void ValidateSnapshot(string identity, List<string> properties, params (string Property, object Stored, object Proposed)[] fields)
        {
            foreach (var field in fields)
            {
                if (properties.Contains(field.Property) && !Equals(field.Stored, field.Proposed))
                {
                    throw new InvalidOperationException($"Persisted edition {identity} cannot be changed through ordinary history updates.");
                }
            }
        }

        public static void LockAndValidate(IDbConnection connection, IDbTransaction transaction, int movieId, int? editionId, string operationId = null)
        {
            if (!MovieEditionOperationIdentity.IsValid(operationId) || (operationId != null && !editionId.HasValue))
            {
                throw new InvalidOperationException("Edition grab operation identity must be a canonical nonempty GUID with an assigned edition.");
            }

            if (!editionId.HasValue)
            {
                return;
            }

            var parent = LockParent(connection, transaction, movieId);
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
