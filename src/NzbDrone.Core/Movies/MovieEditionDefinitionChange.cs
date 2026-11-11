using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Movies
{
    public class MovieEditionDefinitionChange
    {
        public int MovieId { get; set; }
        public int EditionId { get; set; }
        public string Name { get; set; }
        public Dictionary<string, string> Attributes { get; set; }
        public List<string> MatchRules { get; set; }
        public string SampleTitle { get; set; }
        public bool ExpectedMovieMonitored { get; set; }
        public bool ExpectedEditionMonitored { get; set; }
        public int? ExpectedMovieFileId { get; set; }
        public int ExpectedQualityProfileId { get; set; }
        public int ExpectedRuleRevision { get; set; }
        public string ExpectedName { get; set; }

        public bool Matches(Movie movie, MovieEdition edition) => movie?.Id == MovieId && edition?.Id == EditionId && edition.MovieId == MovieId &&
            movie.Monitored == ExpectedMovieMonitored && edition.Monitored == ExpectedEditionMonitored &&
            edition.MovieFileId == ExpectedMovieFileId && edition.QualityProfileId == ExpectedQualityProfileId &&
            edition.RuleRevision == ExpectedRuleRevision && edition.Name == ExpectedName;

        internal static int NextRuleRevision(MovieEdition stored, MovieEdition proposed)
        {
            var storedAttributes = new Dictionary<string, string>(stored.Attributes ?? new(), System.StringComparer.Ordinal);
            var proposedAttributes = proposed.Attributes ?? new();
            var changed = !(stored.MatchRules ?? new()).SequenceEqual(proposed.MatchRules ?? new()) ||
                storedAttributes.Count != proposedAttributes.Count ||
                proposedAttributes.Any(a => !storedAttributes.TryGetValue(a.Key, out var value) || value != a.Value);
            return changed ? checked(stored.RuleRevision + 1) : stored.RuleRevision;
        }
    }
}
