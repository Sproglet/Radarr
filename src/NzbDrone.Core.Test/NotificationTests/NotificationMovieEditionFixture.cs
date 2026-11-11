using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class NotificationMovieEditionFixture : CoreTest<NotificationMovieEditionService>
    {
        private MovieEdition _edition;
        private MovieFile _file;

        [SetUp]
        public void Setup()
        {
            _edition = new MovieEdition { Id = 2, MovieId = 1, Name = "Synthetic Directors 3D", EditionKey = "synthetic-directors-3d",
                QualityProfileId = 7, RuleRevision = 12, Attributes = new Dictionary<string, string>
                { { "cut", "directors-cut" }, { "stereoscopic", "3d" }, { "branding", "Synthetic Collector" } } };
            _file = new MovieFile { Id = 20, MovieId = 1, MovieEditionId = 2,
                Movie = new Movie { Id = 1, PrimaryEditionId = 1, QualityProfileId = 1, MovieFileId = 10 } };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns(_edition);
        }

        [Test]
        public void should_resolve_secondary_metadata_after_its_current_file_is_removed()
        {
            _edition.MovieFileId.Should().BeNull();
            var result = Subject.ForFile(_file);
            result.IsResolved.Should().BeTrue();
            result.Id.Should().Be(2);
            result.QualityProfileId.Should().Be(7);
            result.RuleRevision.Should().Be(12);
            result.Attributes.Should().BeEquivalentTo(_edition.Attributes);
            _file.Movie.QualityProfileId.Should().Be(1);
            _file.Movie.MovieFileId.Should().Be(10);
        }

        [Test]
        public void should_copy_attributes_for_an_event_snapshot()
        {
            var result = Subject.ForFile(_file);
            _edition.Attributes["branding"] = "Synthetic Changed";
            result.Attributes["branding"].Should().Be("Synthetic Collector");
        }

        [Test]
        public void should_not_infer_an_unassigned_files_primary_edition()
        {
            _file.MovieEditionId = null;
            Subject.ForFile(_file).Should().BeNull();
            Mocker.GetMock<IMovieEditionRepository>().Verify(r => r.Find(It.IsAny<int>()), Times.Never());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_preserve_damaged_identity_without_foreign_or_default_metadata(bool missing)
        {
            if (missing) Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns((MovieEdition)null);
            else _edition.MovieId = 999;
            var result = Subject.ForFile(_file);
            result.Id.Should().Be(2);
            result.MovieId.Should().Be(1);
            result.IsResolved.Should().BeFalse();
            result.Error.Should().NotBeNullOrWhiteSpace();
            result.Name.Should().BeNull();
            result.QualityProfileId.Should().BeNull();
            result.Attributes.Should().BeNull();
        }

        [Test]
        public void should_use_explicit_release_context_without_reclassifying_or_loading_a_primary()
        {
            var release = new RemoteMovie { Movie = _file.Movie,
                EditionContext = new MovieEditionContext(_edition, new QualityProfile { Id = 7 }, null, true) };
            Subject.ForRelease(release).Id.Should().Be(2);
            Subject.ForRelease(release).RuleRevision.Should().Be(12);
            Mocker.GetMock<IMovieEditionRepository>().Verify(r => r.Find(It.IsAny<int>()), Times.Never());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_not_resolve_a_foreign_release_context(bool missingMovie)
        {
            var release = new RemoteMovie { Movie = missingMovie ? null : new Movie { Id = 999 },
                EditionContext = new MovieEditionContext(_edition, new QualityProfile { Id = 7 }, null) };
            Subject.ForRelease(release).IsResolved.Should().BeFalse();
            Subject.ForRelease(release).QualityProfileId.Should().BeNull();
        }

        [Test]
        public void should_leave_a_legacy_release_unassigned()
        {
            Subject.ForRelease(new RemoteMovie { Movie = _file.Movie }).Should().BeNull();
            Subject.ForRelease(null).Should().BeNull();
            Subject.ForFile(null).Should().BeNull();
        }
    }
}
