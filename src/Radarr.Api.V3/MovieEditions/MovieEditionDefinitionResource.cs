using System.Collections.Generic;
using System.Text.Json.Serialization;
using NzbDrone.Core.Movies;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionDefinitionResource
    {
        public int MovieId { get; set; }
        [JsonRequired]
        public string Name { get; set; }
        [JsonRequired]
        public Dictionary<string, string> Attributes { get; set; }
        [JsonRequired]
        public List<string> MatchRules { get; set; }
        public string SampleTitle { get; set; }
        [JsonRequired]
        public bool ExpectedMovieMonitored { get; set; }
        [JsonRequired]
        public bool ExpectedEditionMonitored { get; set; }
        [JsonRequired]
        public int? ExpectedMovieFileId { get; set; }
        [JsonRequired]
        public int ExpectedQualityProfileId { get; set; }
        [JsonRequired]
        public int ExpectedRuleRevision { get; set; }
        [JsonRequired]
        public string ExpectedName { get; set; }

        public MovieEditionDefinitionChange ToChange(int editionId) => new()
        {
            MovieId = MovieId, EditionId = editionId, Name = Name, Attributes = Attributes, MatchRules = MatchRules, SampleTitle = SampleTitle,
            ExpectedMovieMonitored = ExpectedMovieMonitored, ExpectedEditionMonitored = ExpectedEditionMonitored,
            ExpectedMovieFileId = ExpectedMovieFileId, ExpectedQualityProfileId = ExpectedQualityProfileId,
            ExpectedRuleRevision = ExpectedRuleRevision, ExpectedName = ExpectedName
        };
    }
}
