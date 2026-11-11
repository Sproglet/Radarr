using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using Radarr.Api.V3.Parse;
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Parse
{
    [TestFixture]
    public class EditionParseFixture
    {
        private const string Title = "Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup";
        private Mock<IParsingService> _parsing;
        private Mock<ICustomFormatCalculationService> _formats;
        private Mock<IMovieEditionEvaluationService> _evaluation;
        private Mock<IMovieEditionRepository> _editions;
        private RemoteMovie _remote;
        private ParseController _controller;

        [SetUp]
        public void Setup()
        {
            _parsing = new Mock<IParsingService>();
            _formats = new Mock<ICustomFormatCalculationService>();
            _evaluation = new Mock<IMovieEditionEvaluationService>();
            _editions = new Mock<IMovieEditionRepository>();
            _remote = new RemoteMovie
            {
                Movie = new Movie { Id = 1, QualityProfileId = 1, QualityProfile = new QualityProfile { Id = 1 },
                    MovieMetadata = new MovieMetadata { Title = "Synthetic Lunar Voyage", Year = 2026, TmdbId = -1000001 } },
                ParsedMovieInfo = Parser.ParseMovieTitle(Title), CustomFormatScore = 123
            };
            _parsing.Setup(p => p.Map(It.IsAny<ParsedMovieInfo>(), "", 0)).Returns(() => _remote);
            _formats.Setup(f => f.ParseCustomFormat(It.IsAny<RemoteMovie>(), 0)).Returns(new List<CustomFormat> { new() { Id = 5, Name = "Synthetic Match" } });
            _evaluation.Setup(e => e.PreviewParsed(It.IsAny<RemoteMovie>(), It.IsAny<int?>())).Returns(new MovieEditionEvaluation
            { Classification = new EditionClassification { Status = EditionClassificationStatus.Invalid, Reason = "Synthetic unknown context" } });
            _editions.Setup(e => e.Find(2)).Returns(new MovieEdition { Id = 2, MovieId = 1 });
            _controller = new ParseController(_parsing.Object, Mock.Of<IConfigService>(), Mock.Of<IRemoteMovieAggregationService>(), _formats.Object, _evaluation.Object, _editions.Object);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void reports_resolved_profile_score_without_modifying_the_shared_movie_or_remote(bool explicitSelection)
        {
            var context = new MovieEditionContext(new MovieEdition { Id = 2, MovieId = 1, Name = "Synthetic Directors 3D", QualityProfileId = 7 }, new QualityProfile { Id = 7 }, null, true);
            _evaluation.Setup(e => e.PreviewParsed(It.Is<RemoteMovie>(r => r.EditionContext == null && !r.DownloadAllowed && r.Release.Size == 0), explicitSelection ? 2 : null))
                .Returns(new MovieEditionEvaluation { Context = context, CustomFormatScore = 77, CustomFormats = new(),
                    Classification = new EditionClassification { Status = EditionClassificationStatus.Matched, CandidateEditionIds = new() { 2 } } });
            var resource = _controller.Parse(Title, explicitSelection ? 2 : null).Value;
            resource.CustomFormatScore.Should().Be(77);
            resource.MovieEditionId.Should().Be(2);
            resource.MovieEditionName.Should().Be("Synthetic Directors 3D");
            resource.QualityProfileId.Should().Be(7);
            resource.EditionSelectionExplicit.Should().Be(explicitSelection);
            resource.Movie.QualityProfileId.Should().Be(1);
            _remote.CustomFormatScore.Should().Be(123);
            _remote.EditionContext.Should().BeNull();
            _remote.Movie.QualityProfileId.Should().Be(1);
        }

        [TestCase(EditionClassificationStatus.Ambiguous)]
        [TestCase(EditionClassificationStatus.Unconfigured)]
        [TestCase(EditionClassificationStatus.Invalid)]
        public void unresolved_context_retains_format_matches_but_has_no_profile_or_fabricated_zero(EditionClassificationStatus status)
        {
            _evaluation.Setup(e => e.PreviewParsed(It.IsAny<RemoteMovie>(), null)).Returns(new MovieEditionEvaluation
                { Classification = new EditionClassification { Status = status, Reason = "Synthetic unresolved" } });
            var result = _controller.Parse(Title).Value;
            result.CustomFormatScore.Should().BeNull();
            result.QualityProfileId.Should().BeNull();
            result.MovieEditionId.Should().BeNull();
            result.CustomFormats.Should().ContainSingle();
            result.EditionClassification.Status.Should().Be(status);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void selected_edition_does_not_turn_an_unmapped_title_into_a_matched_movie(bool selected)
        {
            _remote.Movie = null;
            var result = _controller.Parse(Title, selected ? 2 : null).Value;
            result.Movie.Should().BeNull();
            result.MovieEditionId.Should().BeNull();
            result.CustomFormatScore.Should().BeNull();
            result.CustomFormats.Should().ContainSingle();
        }

        [Test]
        public void unparseable_title_has_no_meaningful_score()
        {
            var result = _controller.Parse("???").Value;
            result.ParsedMovieInfo.Should().BeNull();
            result.CustomFormatScore.Should().BeNull();
            result.EditionClassification.Status.Should().Be(EditionClassificationStatus.Invalid);
            _evaluation.Verify(e => e.PreviewParsed(It.IsAny<RemoteMovie>(), It.IsAny<int?>()), Times.Never());
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void invalid_selection_is_rejected(int id)
        {
            System.Action parse = () => _controller.Parse(Title, id);
            parse.Should().Throw<BadRequestException>();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_or_foreign_edition_cannot_provide_a_profile(bool foreign)
        {
            if (foreign) _editions.Setup(e => e.Find(2)).Returns(new MovieEdition { Id = 2, MovieId = 99 });
            else _editions.Setup(e => e.Find(2)).Returns((MovieEdition)null);
            System.Action parse = () => _controller.Parse(Title, 2);
            parse.Should().Throw<NotFoundException>();
            _evaluation.Verify(e => e.PreviewParsed(It.IsAny<RemoteMovie>(), It.IsAny<int?>()), Times.Never());
        }
    }
}
