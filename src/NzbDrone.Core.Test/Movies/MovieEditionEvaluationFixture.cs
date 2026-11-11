using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class MovieEditionEvaluationFixture : CoreTest<MovieEditionEvaluationService>
    {
        [TestCase(true)]
        [TestCase(false)]
        public void missing_profile_and_invalid_file_context_have_unknown_score(bool missingProfile)
        {
            var movie = new Movie { Id = 42 };
            Mocker.GetMock<IMovieEditionClassifier>().Setup(c => c.Classify(42, It.IsAny<string>(), null, "3D", null))
                .Returns(new EditionClassification { Status = EditionClassificationStatus.Matched, CandidateEditionIds = new() { 2 }, RuleRevision = 7 });
            Mocker.GetMock<IMovieEditionContextService>().Setup(c => c.Resolve(movie, 2, null))
                .Throws(missingProfile ? new ModelNotFoundException(typeof(QualityProfile), 7) : new InvalidOperationException("Synthetic damaged ownership"));
            var result = Subject.Preview(new RemoteMovie { Movie = movie, Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026-SyntheticGroup" } }, "3D");
            result.Classification.Status.Should().Be(EditionClassificationStatus.Invalid);
            result.Context.Should().BeNull();
            result.CustomFormatScore.Should().BeNull();
        }
        [TestCase(120)]
        [TestCase(-240)]
        public void preview_scores_selected_profile_without_changing_shared_movie(int score)
        {
            var format = new CustomFormat { Id = 1, Name = "Synthetic Format" };
            var original = new QualityProfile { Id = 1 };
            var movie = new Movie { Id = 42, QualityProfileId = 1, QualityProfile = original };
            var edition = new MovieEdition { Id = 2, MovieId = 42, QualityProfileId = 2, RuleRevision = 7 };
            var context = new MovieEditionContext(edition, new QualityProfile
            {
                Id = 2, FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = format, Score = score } }
            }, null, true);
            Mocker.GetMock<IMovieEditionClassifier>().Setup(c => c.Classify(42, It.IsAny<string>(), null, "3D", null))
                .Returns(new EditionClassification { Status = EditionClassificationStatus.Matched, CandidateEditionIds = new List<int> { 2 }, RuleRevision = 7 });
            Mocker.GetMock<IMovieEditionContextService>().Setup(c => c.Resolve(movie, 2, null)).Returns(context);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(c => c.ParseCustomFormat(It.Is<RemoteMovie>(r => r.EditionContext == context), It.IsAny<long>()))
                .Returns(new List<CustomFormat> { format });
            var remote = new RemoteMovie { Movie = movie, Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026-SyntheticGroup" } };

            Subject.Preview(remote, "3D").CustomFormatScore.Should().Be(score);

            movie.QualityProfile.Should().BeSameAs(original);
            movie.QualityProfileId.Should().Be(1);
            remote.EditionContext.Should().BeNull();
            remote.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        public void unconfigured_target_has_no_score_or_context()
        {
            Mocker.GetMock<IMovieEditionClassifier>().Setup(c => c.Classify(42, It.IsAny<string>(), null, "", null))
                .Returns(new EditionClassification { Status = EditionClassificationStatus.Unconfigured });
            var result = Subject.Preview(new RemoteMovie
            {
                Movie = new Movie { Id = 42 }, Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026-SyntheticGroup" }
            }, "");
            result.Context.Should().BeNull();
            result.CustomFormatScore.Should().BeNull();
        }

        [Test]
        public void unmapped_movie_has_no_meaningful_score()
        {
            var result = Subject.Preview(new RemoteMovie(), "");
            result.Classification.Status.Should().Be(EditionClassificationStatus.Invalid);
            result.CustomFormatScore.Should().BeNull();
        }
    }
}
