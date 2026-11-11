using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class EditionClientRemovalFixture : CoreTest<IgnoredDownloadService>
    {
        private TrackedDownload _tracked;
        private DownloadHistory _grab;
        private MovieEditionContext _context;

        [SetUp]
        public void Setup()
        {
            var movie = new Movie { Id = 1 };
            _context = new MovieEditionContext(new MovieEdition { Id = 2, MovieId = 1, RuleRevision = 8 }, new QualityProfile { Id = 7 }, null, true);
            _tracked = new TrackedDownload { DownloadClient = 41, DownloadItem = new DownloadClientItem
                { DownloadId = "synthetic-real-client-job", Title = "SyntheticOpaque", DownloadClientInfo = new DownloadClientItemClientInfo() },
                RemoteMovie = new RemoteMovie { Movie = movie, EditionContext = _context, ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.Bluray1080p) } } };
            _grab = new DownloadHistory { MovieId = 1, MovieEditionId = 2, EditionRuleRevision = 4, DownloadClientId = 41, DownloadId = _tracked.DownloadItem.DownloadId,
                Data = new Dictionary<string, string> { { "movieEditionName", "Synthetic Recorded Directors 3D" } } };
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestGrab(41, _tracked.DownloadItem.DownloadId)).Returns(() => _grab);
            Mocker.GetMock<IMovieEditionRecoveryService>().Setup(s => s.Recover(movie, null, _grab)).Returns(_context);
        }

        [Test]
        public void settles_only_saved_client_edition_intent_and_preserves_historical_revision_and_name()
        {
            Subject.IgnoreRemovedDownload(_tracked).Should().BeTrue();
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.Is<DownloadIgnoredEvent>(e => e.MovieId == 1 && e.MovieEditionId == 2 &&
                e.EditionRuleRevision == 4 && e.MovieEditionName == "Synthetic Recorded Directors 3D" && e.DownloadId == _tracked.DownloadItem.DownloadId &&
                e.TrackedDownload.DownloadClient == 41 && e.Message == "Removed from download client")), Times.Once());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void legacy_unknown_or_unassigned_removal_does_not_infer_a_target(bool noGrab)
        {
            if (noGrab) _grab = null;
            else _grab.MovieEditionId = null;
            _tracked.RemoteMovie = null;
            Subject.IgnoreRemovedDownload(_tracked).Should().BeTrue();
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadIgnoredEvent>()), Times.Never());
            Mocker.GetMock<IMovieEditionRecoveryService>().Verify(s => s.Recover(It.IsAny<Movie>(), It.IsAny<NzbDrone.Core.History.MovieHistory>(), It.IsAny<DownloadHistory>()), Times.Never());
        }

        [TestCase("client")]
        [TestCase("movie")]
        [TestCase("target")]
        [TestCase("review")]
        [TestCase("changed-grab")]
        public void inconsistent_or_unresolved_saved_intent_cannot_publish_a_terminal_record(string damage)
        {
            if (damage == "client") _grab.DownloadClientId = 99;
            if (damage == "movie") _grab.MovieId = 99;
            if (damage == "target") _tracked.RemoteMovie.EditionContext = new MovieEditionContext(new MovieEdition { Id = 99 }, new QualityProfile(), null);
            if (damage == "review") _tracked.EditionRecoveryError = "Synthetic review required";
            if (damage == "changed-grab") Mocker.GetMock<IDownloadHistoryService>()
                .SetupSequence(s => s.GetLatestGrab(41, _tracked.DownloadItem.DownloadId))
                .Returns(_grab).Returns(new DownloadHistory { MovieId = 1, MovieEditionId = 3, DownloadClientId = 41 });
            Subject.IgnoreRemovedDownload(_tracked).Should().BeFalse();
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadIgnoredEvent>()), Times.Never());
            _tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }
    }
}
