using System.Text.Json.Serialization;
using NzbDrone.Core.Movies;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionQualityProfileResource
    {
        public int MovieId { get; set; }
        [JsonRequired]
        public int QualityProfileId { get; set; }
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

        public MovieEditionQualityProfileChange ToChange(int editionId) => new()
        {
            MovieId = MovieId,
            EditionId = editionId,
            QualityProfileId = QualityProfileId,
            ExpectedMovieMonitored = ExpectedMovieMonitored,
            ExpectedEditionMonitored = ExpectedEditionMonitored,
            ExpectedMovieFileId = ExpectedMovieFileId,
            ExpectedQualityProfileId = ExpectedQualityProfileId,
            ExpectedRuleRevision = ExpectedRuleRevision,
            ExpectedName = ExpectedName
        };
    }
}
