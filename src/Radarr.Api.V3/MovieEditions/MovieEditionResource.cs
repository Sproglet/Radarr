using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using Radarr.Http.REST;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionResource : RestResource
    {
        public int MovieId { get; set; }
        public string EditionKey { get; set; }
        public string Name { get; set; }
        public bool IsPrimary { get; set; }
        public bool Monitored { get; set; }
        public bool EffectiveMonitored { get; set; }
        public int QualityProfileId { get; set; }
        public string QualityProfileName { get; set; }
        public int? MovieFileId { get; set; }
        public string RelativePath { get; set; }
        public QualityModel Quality { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public Dictionary<string, string> Attributes { get; set; }
        public List<string> MatchRules { get; set; }
        public int RuleRevision { get; set; }
        public string State { get; set; }
        public string RejectionReason { get; set; }
    }

    public static class MovieEditionResourceMapper
    {
        public static MovieEditionResource ToResource(this MovieEdition edition, Movie movie, IMovieEditionContextService contexts)
        {
            var resource = new MovieEditionResource
            {
                Id = edition.Id,
                MovieId = edition.MovieId,
                EditionKey = edition.EditionKey,
                Name = edition.Name,
                IsPrimary = edition.Id == movie.PrimaryEditionId,
                Monitored = edition.Monitored,
                EffectiveMonitored = movie.Monitored && edition.Monitored,
                QualityProfileId = edition.QualityProfileId,
                MovieFileId = edition.MovieFileId,
                LastSearchTime = edition.LastSearchTime,
                Attributes = edition.Attributes,
                MatchRules = edition.MatchRules,
                RuleRevision = edition.RuleRevision
            };

            try
            {
                var context = contexts.Resolve(movie, edition.Id);
                resource.QualityProfileName = context.QualityProfile.Name;
                resource.RelativePath = context.CurrentFile?.RelativePath;
                resource.Quality = context.CurrentFile?.Quality;
                resource.State = context.CurrentFile == null ? "missing" : "present";
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ModelNotFoundException)
            {
                resource.State = "invalid";
                resource.RejectionReason = ex.Message;
            }

            return resource;
        }
    }
}
