using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using Radarr.Api.V3.CustomFormats;
using Radarr.Api.V3.Movies;
using Radarr.Http;
using Radarr.Http.REST;

namespace Radarr.Api.V3.Parse
{
    [V3ApiController]
    public class ParseController : Controller
    {
        private readonly IParsingService _parsingService;
        private readonly IConfigService _configService;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IMovieEditionEvaluationService _editionEvaluation;
        private readonly IMovieEditionRepository _editions;

        public ParseController(IParsingService parsingService,
                               IConfigService configService,
                               IRemoteMovieAggregationService aggregationService,
                               ICustomFormatCalculationService formatCalculator,
                               IMovieEditionEvaluationService editionEvaluation,
                               IMovieEditionRepository editions)
        {
            _parsingService = parsingService;
            _configService = configService;
            _aggregationService = aggregationService;
            _formatCalculator = formatCalculator;
            _editionEvaluation = editionEvaluation;
            _editions = editions;
        }

        [HttpGet]
        [Produces("application/json")]
        public Ok<ParseResource> Parse(string title, int? movieEditionId = null)
        {
            if (title?.Length > 16384)
            {
                throw new BadRequestException("Use a release title of up to 16384 characters.");
            }

            if (movieEditionId <= 0)
            {
                throw new BadRequestException("Provide a positive edition ID, or omit it for automatic classification.");
            }

            var selected = movieEditionId.HasValue ? _editions.Find(movieEditionId.Value) ?? throw new NotFoundException("Edition not found.") : null;
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var parsedMovieInfo = Parser.ParseMovieTitle(title);

            if (parsedMovieInfo == null)
            {
                return TypedResults.Ok(new ParseResource
                {
                    Title = title,
                    EditionSelectionExplicit = movieEditionId.HasValue,
                    EditionClassification = new EditionClassification { Status = EditionClassificationStatus.Invalid, Reason = "Unable to parse the release title." }
                });
            }

            var remoteMovie = _parsingService.Map(parsedMovieInfo, "", 0);

            if (remoteMovie != null)
            {
                if (remoteMovie.Movie != null && selected != null && selected.MovieId != remoteMovie.Movie.Id)
                {
                    throw new NotFoundException("The selected edition does not belong to the matched movie.");
                }

                _aggregationService.Augment(remoteMovie);

                // Parser previews carry title evidence only. Never reuse a release's
                // assignment or authorize a download while testing its formats.
                var preview = new RemoteMovie
                {
                    Movie = remoteMovie.Movie, ParsedMovieInfo = remoteMovie.ParsedMovieInfo,
                    Release = new ReleaseInfo { Title = title }, Languages = remoteMovie.Languages
                };
                var evaluation = _editionEvaluation.PreviewParsed(preview, movieEditionId);
                var formats = evaluation.CustomFormats ?? _formatCalculator.ParseCustomFormat(preview, 0);

                return TypedResults.Ok(new ParseResource
                {
                    Title = title,
                    ParsedMovieInfo = remoteMovie.ParsedMovieInfo,
                    Movie = remoteMovie.Movie.ToResource(_configService.AvailabilityDelay),
                    Languages = remoteMovie.Languages,
                    CustomFormats = formats?.ToResource(false),
                    CustomFormatScore = evaluation.CustomFormatScore,
                    MovieEditionId = evaluation.Context?.Edition.Id,
                    MovieEditionName = evaluation.Context?.Edition.Name,
                    QualityProfileId = evaluation.Context?.QualityProfile.Id,
                    QualityProfileName = evaluation.Context?.QualityProfile.Name,
                    EditionSelectionExplicit = movieEditionId.HasValue,
                    EditionClassification = evaluation.Classification
                });
            }
            else
            {
                return TypedResults.Ok(new ParseResource
                {
                    Title = title,
                    ParsedMovieInfo = parsedMovieInfo,
                    EditionSelectionExplicit = movieEditionId.HasValue,
                    EditionClassification = new EditionClassification { Status = EditionClassificationStatus.Invalid, Reason = "No movie is matched; an edition profile score is unavailable." }
                });
            }
        }
    }
}
