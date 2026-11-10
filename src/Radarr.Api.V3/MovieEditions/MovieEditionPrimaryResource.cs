using System.Text.Json.Serialization;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionPrimaryResource
    {
        public int MovieId { get; set; }
        public int ExpectedPrimaryEditionId { get; set; }
        [JsonRequired]
        public int? ExpectedMovieFileId { get; set; }
        public int ExpectedQualityProfileId { get; set; }
    }
}
