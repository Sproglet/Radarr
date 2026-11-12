using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Movies
{
    internal static class MovieEditionOperationIdentity
    {
        public static bool IsValid(string operationId) => operationId == null ||
            (Guid.TryParseExact(operationId, "D", out var parsed) && operationId == parsed.ToString("D") && parsed != Guid.Empty);

        public static bool HasAmbiguousOperations(IEnumerable<string> operationIds)
        {
            var ids = operationIds.Distinct(StringComparer.Ordinal).ToList();
            return ids.Any(id => !IsValid(id)) || (ids.Any(id => id != null) && ids.Count > 1);
        }
    }
}
