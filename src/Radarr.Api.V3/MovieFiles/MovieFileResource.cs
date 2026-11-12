using System;
using System.Collections.Generic;
using System.IO;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Http.REST;

namespace Radarr.Api.V3.MovieFiles
{
    public class MovieFileResource : RestResource
    {
        public int MovieId { get; set; }
        public int? MovieEditionId { get; set; }
        public string MovieEditionName { get; set; }
        public int? QualityProfileId { get; set; }
        public bool? EditionContextValid { get; set; }
        public string EditionContextError { get; set; }
        public string RelativePath { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime DateAdded { get; set; }
        public string SceneName { get; set; }
        public string ReleaseGroup { get; set; }
        public string Edition { get; set; }
        public List<Language> Languages { get; set; }
        public QualityModel Quality { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int? CustomFormatScore { get; set; }
        public int? IndexerFlags { get; set; }
        public MediaInfoResource MediaInfo { get; set; }

        public string OriginalFilePath { get; set; }
        public bool QualityCutoffNotMet { get; set; }
    }

    public static class MovieFileResourceMapper
    {
        private static MovieFileResource ToResource(this MovieFile model)
        {
            if (model == null)
            {
                return null;
            }

            return new MovieFileResource
            {
                Id = model.Id,

                MovieId = model.MovieId,
                MovieEditionId = model.MovieEditionId,
                RelativePath = model.RelativePath,

                // Path
                Size = model.Size,
                DateAdded = model.DateAdded,
                SceneName = model.SceneName,
                IndexerFlags = (int)model.IndexerFlags,
                Quality = model.Quality,
                Languages = model.Languages,
                ReleaseGroup = model.ReleaseGroup,
                Edition = model.Edition,
                MediaInfo = model.MediaInfo.ToResource(model.SceneName),
                OriginalFilePath = model.OriginalFilePath
            };
        }

        public static MovieFileResource ToResource(this MovieFile model, NzbDrone.Core.Movies.Movie movie, IUpgradableSpecification upgradableSpecification, ICustomFormatCalculationService formatCalculationService, IMovieEditionContextService editionContexts = null)
        {
            if (model == null)
            {
                return null;
            }

            if (editionContexts != null)
            {
                try
                {
                    if (model.MovieId != movie.Id || !model.MovieEditionId.HasValue)
                    {
                        throw new InvalidOperationException("The file has no valid managed edition assignment. Review its ownership before scoring.");
                    }

                    model.EditionContext = editionContexts.Resolve(movie, model.MovieEditionId.Value);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ModelNotFoundException)
                {
                    var invalid = model.ToResource();
                    invalid.Path = model.MovieId == movie.Id ? Path.Combine(movie.Path, model.RelativePath) : null;
                    invalid.EditionContextValid = false;
                    invalid.EditionContextError = ex.Message;
                    invalid.CustomFormats = new List<CustomFormatResource>();
                    return invalid;
                }
            }

            var profile = model.EditionContext?.QualityProfile ?? movie.QualityProfile;
            var resource = new MovieFileResource
            {
                Id = model.Id,

                MovieId = model.MovieId,
                MovieEditionId = model.MovieEditionId,
                MovieEditionName = model.EditionContext?.Edition.Name,
                QualityProfileId = profile?.Id,
                EditionContextValid = model.EditionContext != null ? true : null,
                RelativePath = model.RelativePath,
                Path = Path.Combine(movie.Path, model.RelativePath),
                Size = model.Size,
                DateAdded = model.DateAdded,
                SceneName = model.SceneName,
                Quality = model.Quality,
                Languages = model.Languages,
                Edition = model.Edition,
                ReleaseGroup = model.ReleaseGroup,
                MediaInfo = model.MediaInfo.ToResource(model.SceneName),
                QualityCutoffNotMet = upgradableSpecification?.QualityCutoffNotMet(profile, model.Quality) ?? false,
                OriginalFilePath = model.OriginalFilePath,
                IndexerFlags = (int)model.IndexerFlags
            };

            if (formatCalculationService != null)
            {
                model.Movie = movie;
                var customFormats = formatCalculationService?.ParseCustomFormat(model, model.Movie);
                var customFormatScore = profile?.CalculateCustomFormatScore(customFormats) ?? 0;

                resource.CustomFormats = customFormats.ToResource(false);
                resource.CustomFormatScore = customFormatScore;
            }

            return resource;
        }
    }
}
