using System.Text.Json.Serialization;
using NzbDrone.Core.Movies;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionRemovalResource
    {
        public int MovieId { get; set; }
        [JsonRequired]
        public int ExpectedPrimaryEditionId { get; set; }
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

        public MovieEditionRemovalChange ToChange(int editionId) => new()
        {
            MovieId = MovieId, EditionId = editionId, ExpectedPrimaryEditionId = ExpectedPrimaryEditionId,
            ExpectedMovieMonitored = ExpectedMovieMonitored, ExpectedEditionMonitored = ExpectedEditionMonitored,
            ExpectedMovieFileId = ExpectedMovieFileId, ExpectedQualityProfileId = ExpectedQualityProfileId,
            ExpectedRuleRevision = ExpectedRuleRevision, ExpectedName = ExpectedName
        };
    }
}
