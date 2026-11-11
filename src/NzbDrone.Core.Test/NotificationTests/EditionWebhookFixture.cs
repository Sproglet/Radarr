using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class EditionWebhookFixture : CoreTest<Webhook>
    {
        private Movie _movie;
        private MovieEdition _edition;
        private MovieFile _file;
        private RemoteMovie _remote;
        private WebhookPayload _payload;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Path = "/synthetic/movies", PrimaryEditionId = 1, MovieFileId = 10, QualityProfileId = 1,
                MovieMetadata = new MovieMetadata { Title = "Synthetic Lunar Voyage", Year = 2026, TmdbId = -1000001 } };
            _edition = new MovieEdition { Id = 2, MovieId = 1, Name = "Synthetic Directors 3D", QualityProfileId = 7, RuleRevision = 12,
                Attributes = new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } } };
            _file = new MovieFile { Id = 20, MovieId = 1, MovieEditionId = 2, Movie = _movie, RelativePath = "Synthetic-SyntheticGroup.mkv",
                Quality = new QualityModel(Quality.Bluray1080p) };
            _remote = new RemoteMovie { Movie = _movie, Release = new ReleaseInfo { Title = "Synthetic.Release-SyntheticGroup" },
                ParsedMovieInfo = new ParsedMovieInfo { Quality = _file.Quality },
                EditionContext = new MovieEditionContext(_edition, new QualityProfile { Id = 7 }, null, true) };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns(_edition);
            Mocker.SetConstant<INotificationMovieEditionService>(new NotificationMovieEditionService(Mocker.GetMock<IMovieEditionRepository>().Object));
            Mocker.GetMock<ITagRepository>().Setup(r => r.GetTags(It.IsAny<HashSet<int>>())).Returns(new List<Tag>());
            Mocker.GetMock<IWebhookProxy>().Setup(p => p.SendWebhook(It.IsAny<WebhookPayload>(), It.IsAny<WebhookSettings>()))
                .Callback<WebhookPayload, WebhookSettings>((payload, settings) => _payload = payload);
            Subject.Definition = new NotificationDefinition { Settings = new WebhookSettings() };
        }

        [Test]
        public void should_identify_import_and_replaced_file_as_the_secondary_edition()
        {
            Subject.OnDownload(new DownloadMessage { Movie = _movie, MovieFile = _file,
                MovieInfo = new LocalMovie { Movie = _movie }, OldMovieFiles = new List<DeletedMovieFile> { new DeletedMovieFile(_file, null) } });
            var payload = (WebhookImportPayload)_payload;
            payload.MovieEdition.Id.Should().Be(2);
            payload.MovieEdition.QualityProfileId.Should().Be(7);
            payload.MovieFile.MovieEditionId.Should().Be(2);
            payload.DeletedFiles[0].MovieEditionId.Should().Be(2);
            payload.DeletedFiles[0].MovieEdition.Attributes.Should().BeEquivalentTo(_edition.Attributes);
            _movie.MovieFileId.Should().Be(10);
            _movie.QualityProfileId.Should().Be(1);
        }

        [Test]
        public void should_keep_assigned_and_unassigned_files_distinct_in_one_rename()
        {
            var other = new MovieFile { Id = 21, MovieId = 1, Movie = _movie, RelativePath = "SyntheticUnassigned.mkv", Quality = _file.Quality };
            Subject.OnMovieRename(_movie, new List<RenamedMovieFile> { new RenamedMovieFile { MovieFile = _file }, new RenamedMovieFile { MovieFile = other } });
            var files = ((WebhookRenamePayload)_payload).RenamedMovieFiles;
            files[0].MovieEditionId.Should().Be(2);
            files[0].MovieEdition.RuleRevision.Should().Be(12);
            files[1].MovieEditionId.Should().BeNull();
            files[1].MovieEdition.Should().BeNull();
        }

        [Test]
        public void should_resolve_delete_metadata_after_pointer_clearing()
        {
            Subject.OnMovieFileDelete(new MovieFileDeleteMessage { Movie = _movie, MovieFile = _file, Reason = DeleteMediaFileReason.Upgrade });
            var file = ((WebhookMovieFileDeletePayload)_payload).MovieFile;
            file.MovieEditionId.Should().Be(2);
            file.MovieEdition.Name.Should().Be(_edition.Name);
            file.MovieEdition.IsResolved.Should().BeTrue();
        }

        [Test]
        public void should_expose_missing_targets_as_unresolved_without_primary_metadata()
        {
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns((MovieEdition)null);
            Subject.OnMovieFileDelete(new MovieFileDeleteMessage { Movie = _movie, MovieFile = _file });
            var file = ((WebhookMovieFileDeletePayload)_payload).MovieFile;
            file.MovieEditionId.Should().Be(2);
            file.MovieEdition.IsResolved.Should().BeFalse();
            file.MovieEdition.QualityProfileId.Should().BeNull();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_only_identify_explicitly_assigned_grabs(bool assigned)
        {
            if (!assigned) _remote.EditionContext = null;
            Subject.OnGrab(new GrabMessage { Movie = _movie, RemoteMovie = _remote, Quality = _file.Quality });
            var edition = ((WebhookGrabPayload)_payload).MovieEdition;
            if (assigned) edition.Id.Should().Be(2);
            else edition.Should().BeNull();
        }

        [Test]
        public void should_include_recovery_errors_without_inventing_an_assignment()
        {
            _remote.EditionContext = null;
            Subject.OnManualInteractionRequired(new ManualInteractionRequiredMessage { Movie = _movie, RemoteMovie = _remote, Quality = _file.Quality,
                TrackedDownload = new TrackedDownload { DownloadItem = new DownloadClientItem(), EditionRecoveryError = "Synthetic ambiguous assignment" } });
            var payload = (WebhookManualInteractionPayload)_payload;
            payload.MovieEdition.Should().BeNull();
            payload.EditionRecoveryError.Should().Be("Synthetic ambiguous assignment");
        }
    }
}
