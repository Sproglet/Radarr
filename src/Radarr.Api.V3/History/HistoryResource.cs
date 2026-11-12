using System;
using System.Collections.Generic;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Api.V3.Movies;
using Radarr.Http.REST;

namespace Radarr.Api.V3.History
{
    public class HistoryResource : RestResource
    {
        public int MovieId { get; set; }
        public int? MovieEditionId { get; set; }
        public int? EditionRuleRevision { get; set; }
        public string MovieEditionName { get; set; }
        public int? QualityProfileId { get; set; }
        public bool EditionContextValid { get; set; }
        public string EditionContextError { get; set; }
        public string SourceTitle { get; set; }
        public List<Language> Languages { get; set; }
        public QualityModel Quality { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int? CustomFormatScore { get; set; }
        public bool? QualityCutoffNotMet { get; set; }
        public DateTime Date { get; set; }
        public string DownloadId { get; set; }

        public MovieHistoryEventType EventType { get; set; }

        public Dictionary<string, string> Data { get; set; }

        public MovieResource Movie { get; set; }
    }

    public static class HistoryResourceMapper
    {
        public static HistoryResource ToResource(
            this MovieHistory model,
            ICustomFormatCalculationService formatCalculator,
            IMovieEditionContextService editionContexts,
            IUpgradableSpecification upgradableSpecification)
        {
            if (model == null)
            {
                return null;
            }

            var resource = new HistoryResource
            {
                Id = model.Id,

                MovieId = model.MovieId,
                MovieEditionId = model.MovieEditionId,
                EditionRuleRevision = model.EditionRuleRevision,
                MovieEditionName = model.Data?.GetValueOrDefault("movieEditionName"),
                SourceTitle = model.SourceTitle,
                Languages = model.Languages,
                Quality = model.Quality,
                CustomFormats = new List<CustomFormatResource>(),

                Date = model.Date,
                DownloadId = model.DownloadId,

                EventType = model.EventType,

                Data = model.Data
            };

            MovieEditionContext context;
            try
            {
                if (model.Movie == null || model.Movie.Id != model.MovieId || !model.MovieEditionId.HasValue)
                {
                    throw new InvalidOperationException("This history row has no valid recorded edition assignment. Its edition score and cutoff are unknown.");
                }

                // Never infer historical intent from the current primary, title or matching rules.
                context = editionContexts.Resolve(model.Movie, model.MovieEditionId.Value);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ModelNotFoundException)
            {
                resource.EditionContextError = ex.Message;
                return resource;
            }

            var customFormats = formatCalculator.ParseCustomFormat(model, model.Movie);
            resource.MovieEditionName ??= context.Edition.Name;
            resource.QualityProfileId = context.QualityProfile.Id;
            resource.CustomFormats = customFormats.ToResource(false);
            resource.CustomFormatScore = context.QualityProfile.CalculateCustomFormatScore(customFormats);
            resource.QualityCutoffNotMet = upgradableSpecification.QualityCutoffNotMet(context.QualityProfile, model.Quality);
            resource.EditionContextValid = true;
            return resource;
        }
    }
}
