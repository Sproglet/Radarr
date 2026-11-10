using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.History;

namespace NzbDrone.Api.Test.v3.History
{
    [TestFixture]
    public class HistoryEditionResourceFixture
    {
        private Mock<ICustomFormatCalculationService> _formats;
        private Mock<IMovieEditionContextService> _contexts;
        private Mock<IUpgradableSpecification> _upgradable;
        private MovieHistory _history;
        private MovieEditionContext _context;

        [SetUp]
        public void Setup()
        {
            _formats = new Mock<ICustomFormatCalculationService>();
            _contexts = new Mock<IMovieEditionContextService>();
            _upgradable = new Mock<IUpgradableSpecification>();
            var format = new CustomFormat { Id = 5, Name = "Synthetic Format" };
            var profile = new QualityProfile
            {
                Id = 2,
                FormatItems = new List<ProfileFormatItem> { new() { Format = format, Score = 77 } }
            };
            _history = new MovieHistory
            {
                Id = 10,
                MovieId = 1,
                MovieEditionId = 2,
                EditionRuleRevision = 4,
                Movie = new Movie { Id = 1, QualityProfileId = 1, QualityProfile = new QualityProfile { Id = 1 } },
                Quality = new QualityModel(Quality.SDTV),
                SourceTitle = "Synthetic.Lunar.Voyage.2026.Directors.Cut.3D-SyntheticGroup",
                Data = new Dictionary<string, string> { ["movieEditionName"] = "Synthetic Recorded Name" }
            };
            _context = new MovieEditionContext(new MovieEdition { Id = 2, MovieId = 1, Name = "Synthetic Current Name" }, profile, null, true);
            _contexts.Setup(c => c.Resolve(_history.Movie, 2, null)).Returns(_context);
            _formats.Setup(c => c.ParseCustomFormat(_history, _history.Movie)).Returns(new List<CustomFormat> { format });
            _upgradable.Setup(c => c.QualityCutoffNotMet(profile, _history.Quality)).Returns(true);
        }

        private HistoryResource Map() => _history.ToResource(_formats.Object, _contexts.Object, _upgradable.Object);

        [Test]
        public void should_score_recorded_target_and_preserve_snapshot_without_mutating_movie()
        {
            var resource = Map();
            resource.MovieEditionId.Should().Be(2);
            resource.EditionRuleRevision.Should().Be(4);
            resource.MovieEditionName.Should().Be("Synthetic Recorded Name");
            resource.QualityProfileId.Should().Be(2);
            resource.CustomFormatScore.Should().Be(77);
            resource.QualityCutoffNotMet.Should().BeTrue();
            resource.EditionContextValid.Should().BeTrue();
            _history.Movie.QualityProfileId.Should().Be(1);
            _history.Movie.QualityProfile.Id.Should().Be(1);
            _history.Data["movieEditionName"].Should().Be("Synthetic Recorded Name");
        }

        [Test]
        public void should_use_current_name_only_without_recorded_snapshot()
        {
            _history.Data.Clear();
            Map().MovieEditionName.Should().Be("Synthetic Current Name");
            _history.Data.Should().BeEmpty();
        }

        [TestCase("unassigned")]
        [TestCase("no-movie")]
        [TestCase("foreign-movie")]
        [TestCase("invalid-links")]
        [TestCase("missing-profile")]
        public void should_preserve_identity_and_report_unknown_without_fallback(string invalid)
        {
            switch (invalid)
            {
                case "unassigned": _history.MovieEditionId = null; break;
                case "no-movie": _history.Movie = null; break;
                case "foreign-movie": _history.Movie.Id = 99; break;
                case "invalid-links":
                    _contexts.Setup(c => c.Resolve(_history.Movie, 2, null)).Throws(new InvalidOperationException("Synthetic invalid ownership"));
                    break;
                case "missing-profile":
                    _contexts.Setup(c => c.Resolve(_history.Movie, 2, null)).Throws(new ModelNotFoundException(typeof(QualityProfile), 2));
                    break;
            }

            var resource = Map();
            resource.MovieEditionId.Should().Be(_history.MovieEditionId);
            resource.EditionRuleRevision.Should().Be(4);
            resource.MovieEditionName.Should().Be("Synthetic Recorded Name");
            resource.EditionContextValid.Should().BeFalse();
            resource.EditionContextError.Should().NotBeNullOrEmpty();
            resource.CustomFormatScore.Should().BeNull();
            resource.QualityProfileId.Should().BeNull();
            resource.QualityCutoffNotMet.Should().BeNull();
            resource.CustomFormats.Should().BeEmpty();
            _formats.Verify(c => c.ParseCustomFormat(It.IsAny<MovieHistory>(), It.IsAny<Movie>()), Times.Never());
            _upgradable.Verify(c => c.QualityCutoffNotMet(It.IsAny<QualityProfile>(), It.IsAny<QualityModel>()), Times.Never());
        }
    }
}
