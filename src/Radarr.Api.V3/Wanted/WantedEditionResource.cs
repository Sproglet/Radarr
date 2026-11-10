using NzbDrone.Core.Movies;
using Radarr.Api.V3.MovieEditions;

namespace Radarr.Api.V3.Wanted
{
    public class WantedEditionResource : MovieEditionResource
    {
        public string Title { get; set; }
        public int Year { get; set; }
        public int TmdbId { get; set; }
        public bool MovieMonitored { get; set; }
    }

    public static class WantedEditionResourceMapper
    {
        public static WantedEditionResource ToResource(this MovieEditionWanted edition)
        {
            var invalidProfile = !edition.ProfileExists;
            var invalidFile = edition.MovieFileId.HasValue && !edition.ResolvedMovieFileId.HasValue;
            return new WantedEditionResource
            {
                Id = edition.Id,
                MovieId = edition.MovieId,
                Title = edition.MovieTitle,
                Year = edition.Year,
                TmdbId = edition.TmdbId,
                MovieMonitored = edition.MovieMonitored,
                EditionKey = edition.EditionKey,
                Name = edition.Name,
                IsPrimary = edition.Id == edition.PrimaryEditionId,
                Monitored = edition.Monitored,
                EffectiveMonitored = edition.MovieMonitored && edition.Monitored,
                QualityProfileId = edition.QualityProfileId,
                QualityProfileName = edition.QualityProfileName,
                MovieFileId = edition.MovieFileId,
                RelativePath = edition.RelativePath,
                Quality = edition.Quality,
                LastSearchTime = edition.LastSearchTime,
                Attributes = edition.Attributes,
                MatchRules = edition.MatchRules,
                RuleRevision = edition.RuleRevision,
                State = invalidProfile || invalidFile ? "invalid" : edition.ResolvedMovieFileId.HasValue ? "present" : "missing",
                RejectionReason = invalidProfile ? "The edition's quality profile is unavailable." :
                    invalidFile ? "The edition's current file has invalid ownership or is unavailable." : null
            };
        }
    }
}
