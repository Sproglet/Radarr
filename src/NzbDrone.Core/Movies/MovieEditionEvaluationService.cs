using System;
using System.Linq;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Movies
{
    public class MovieEditionEvaluation
    {
        public EditionClassification Classification { get; set; }
        public MovieEditionContext Context { get; set; }
        public int? CustomFormatScore { get; set; }
    }

    public interface IMovieEditionEvaluationService
    {
        MovieEditionEvaluation Preview(RemoteMovie release, string presentationEvidence, int? explicitEditionId = null);
        MovieEditionEvaluation PreviewParsed(RemoteMovie release);
    }

    // A search/parser preview primitive. Never authorizes acquisition or mutates a shared movie.
    public class MovieEditionEvaluationService : IMovieEditionEvaluationService
    {
        private readonly IMovieEditionClassifier _classifier;
        private readonly IMovieEditionContextService _contexts;
        private readonly ICustomFormatCalculationService _formats;

        public MovieEditionEvaluationService(IMovieEditionClassifier classifier, IMovieEditionContextService contexts, ICustomFormatCalculationService formats)
        {
            _classifier = classifier;
            _contexts = contexts;
            _formats = formats;
        }

        public MovieEditionEvaluation PreviewParsed(RemoteMovie release)
        {
            if (release.Movie == null || release.Release == null)
            {
                return new MovieEditionEvaluation { Classification = new EditionClassification { Status = EditionClassificationStatus.Invalid, Reason = "Movie identity and release are required for edition scoring." } };
            }

            var evidence = MovieEditionEvidence.FromParsed(release.ParsedMovieInfo);
            if (!evidence.Valid)
            {
                return new MovieEditionEvaluation { Classification = new EditionClassification { Status = EditionClassificationStatus.Invalid, Reason = "Parser evidence is unavailable; review the release manually." } };
            }

            var classification = _classifier.Classify(release.Movie.Id, release.Release.Title, evidence.Label, evidence.Presentation);
            return Evaluate(release, classification);
        }

        public MovieEditionEvaluation Preview(RemoteMovie release, string presentationEvidence, int? explicitEditionId = null)
        {
            var result = new MovieEditionEvaluation();
            if (release.Movie == null || release.Release == null)
            {
                result.Classification = new EditionClassification { Status = EditionClassificationStatus.Invalid, Reason = "Movie identity and release are required for edition scoring." };
                return result;
            }

            return Evaluate(release, _classifier.Classify(release.Movie.Id, release.Release.Title, release.ParsedMovieInfo?.Edition, presentationEvidence, explicitEditionId));
        }

        private MovieEditionEvaluation Evaluate(RemoteMovie release, EditionClassification classification)
        {
            var result = new MovieEditionEvaluation { Classification = classification };
            if (result.Classification.Status != EditionClassificationStatus.Matched)
            {
                return result;
            }

            try
            {
                result.Context = _contexts.Resolve(release.Movie, result.Classification.CandidateEditionIds.Single());
                if (result.Context.Edition.RuleRevision != result.Classification.RuleRevision)
                {
                    throw new InvalidOperationException("Edition rules changed during evaluation. Retry the preview.");
                }

                var preview = new RemoteMovie
                {
                    Movie = release.Movie, EditionContext = result.Context, Release = release.Release,
                    ParsedMovieInfo = release.ParsedMovieInfo, Languages = release.Languages,
                    ReleaseSource = release.ReleaseSource, MovieRequested = release.MovieRequested
                };
                var formats = _formats.ParseCustomFormat(preview, release.Release.Size);
                result.CustomFormatScore = result.Context.QualityProfile.CalculateCustomFormatScore(formats);
            }
            catch (InvalidOperationException ex)
            {
                result.Context = null;
                result.Classification.Status = EditionClassificationStatus.Invalid;
                result.Classification.Reason = ex.Message;
            }

            return result;
        }
    }
}
