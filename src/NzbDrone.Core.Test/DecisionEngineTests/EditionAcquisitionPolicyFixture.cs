using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    public class EditionAcquisitionPolicyFixture : CoreTest<QueueSpecification>
    {
        private Movie _movie;
        private QualityProfile _profile;
        private RemoteMovie _subject;
        private DelayProfile _delay;

        [SetUp]
        public void Setup()
        {
            _profile = new QualityProfile { Id = 2, Name = "Synthetic Secondary", Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.Bluray1080p.Id, UpgradeAllowed = true };
            _movie = new Movie { Id = 1, QualityProfile = new QualityProfile { Id = 1, Cutoff = Quality.Bluray1080p.Id, Items = Qualities.QualityFixture.GetDefaultQualities() },
                MovieFileId = 10, MovieFile = new MovieFile { Id = 10, Quality = new QualityModel(Quality.Bluray1080p) } };
            _subject = Remote(2, "synthetic-secondary", Quality.Bluray1080p);
            _delay = new DelayProfile { UsenetDelay = 120, PreferredProtocol = DownloadProtocol.Usenet };
            Mocker.GetMock<IDelayProfileService>().Setup(s => s.BestForTags(It.IsAny<HashSet<int>>())).Returns(_delay);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<MovieHistory>(), It.IsAny<Movie>())).Returns(new List<CustomFormat>());
            Mocker.SetConstant(Mocker.Resolve<UpgradableSpecification>());
        }

        private RemoteMovie Remote(int editionId, string guid, Quality quality)
        {
            return new RemoteMovie
            {
                Movie = _movie,
                EditionContext = new MovieEditionContext(new MovieEdition { Id = editionId, MovieId = _movie.Id }, _profile, null, true),
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(quality) },
                ReleaseSource = ReleaseSourceType.ReleasePush,
                Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup", IndexerId = 5, Indexer = "Synthetic Indexer",
                    Guid = guid, PublishDate = DateTime.UtcNow, DownloadProtocol = DownloadProtocol.Usenet }
            };
        }

        private void Queue(RemoteMovie remote, TrackedDownloadState state = TrackedDownloadState.Downloading)
        {
            var queue = new List<Queue.Queue> { new Queue.Queue { RemoteMovie = remote, TrackedDownloadState = state } };
            Mocker.GetMock<IQueueService>().Setup(s => s.GetQueue()).Returns(queue);
            Mocker.GetMock<IPendingReleaseService>().Setup(s => s.GetPendingQueue()).Returns(queue);
        }

        [Test]
        public void other_edition_does_not_compete_in_queue_or_pending()
        {
            Queue(Remote(1, "synthetic-primary", Quality.Bluray1080p));
            Subject.IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
            Mocker.Resolve<PendingSpecification>().IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void same_edition_still_competes_in_queue_and_pending()
        {
            Queue(Remote(2, "synthetic-other-release", Quality.Bluray1080p));
            Subject.IsSatisfiedBy(_subject, new()).Accepted.Should().BeFalse();
            Mocker.Resolve<PendingSpecification>().IsSatisfiedBy(_subject, new()).Accepted.Should().BeFalse();
        }

        [Test]
        public void same_indexer_guid_is_suppressed_across_editions()
        {
            Queue(Remote(1, _subject.Release.Guid, Quality.DVD));
            Subject.IsSatisfiedBy(_subject, new()).Message.Should().Contain("already queued");
        }

        [Test]
        public void equal_guid_from_different_indexers_is_not_a_shared_release()
        {
            var other = Remote(1, _subject.Release.Guid, Quality.Bluray1080p);
            other.Release.IndexerId++;
            Queue(other);
            Subject.IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void failed_pending_duplicate_does_not_block_replacement()
        {
            Queue(Remote(2, _subject.Release.Guid, Quality.Bluray1080p), TrackedDownloadState.FailedPending);
            Subject.IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void queue_uses_selected_profile_without_mutating_movie_default()
        {
            _profile.UpgradeAllowed = false;
            _movie.QualityProfile.UpgradeAllowed = true;
            Queue(Remote(2, "synthetic-previous", Quality.DVD));
            Subject.IsSatisfiedBy(_subject, new()).Reason.Should().Be(DownloadRejectionReason.QueueCutoffMet);
            _movie.QualityProfile.UpgradeAllowed.Should().BeTrue();
        }

        [Test]
        public void primary_pending_age_cannot_bypass_secondary_delay()
        {
            var old = Remote(1, "synthetic-older", Quality.DVD);
            old.Release.PublishDate = DateTime.UtcNow.AddDays(-1);
            Mocker.GetMock<IPendingReleaseService>().Setup(s => s.OldestPendingRelease(_movie.Id)).Returns(old);
            Mocker.Resolve<DelaySpecification>().IsSatisfiedBy(_subject, new()).Reason.Should().Be(DownloadRejectionReason.MinimumAgeDelay);
            Mocker.GetMock<IPendingReleaseService>().Verify(s => s.OldestPendingReleaseForEdition(_movie.Id, 2), Times.Once());
            Mocker.GetMock<IPendingReleaseService>().Verify(s => s.OldestPendingRelease(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void selected_pending_age_can_bypass_delay()
        {
            var old = Remote(2, "synthetic-older", Quality.DVD);
            old.Release.PublishDate = DateTime.UtcNow.AddDays(-1);
            Mocker.GetMock<IPendingReleaseService>().Setup(s => s.OldestPendingReleaseForEdition(_movie.Id, 2)).Returns(old);
            Mocker.Resolve<DelaySpecification>().IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void missing_secondary_does_not_bypass_delay_as_a_proper_of_primary()
        {
            _subject.ParsedMovieInfo.Quality = new QualityModel(Quality.Bluray1080p, new Revision(version: 2));
            Mocker.GetMock<IUpgradableSpecification>().Setup(s => s.IsRevisionUpgrade(It.IsAny<QualityModel>(), It.IsAny<QualityModel>())).Returns(true);
            Mocker.Resolve<DelaySpecification>().IsSatisfiedBy(_subject, new()).Reason.Should().Be(DownloadRejectionReason.MinimumAgeDelay);
            Mocker.GetMock<IUpgradableSpecification>().Verify(s => s.IsRevisionUpgrade(It.IsAny<QualityModel>(), It.IsAny<QualityModel>()), Times.Never());
        }

        [Test]
        public void delay_highest_quality_bypass_uses_selected_profile()
        {
            _profile.Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.DVD, Allowed = true } };
            _subject.ParsedMovieInfo.Quality = new QualityModel(Quality.DVD);
            _delay.BypassIfHighestQuality = true;
            Mocker.Resolve<DelaySpecification>().IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void history_of_other_edition_does_not_block_secondary()
        {
            Mocker.GetMock<IHistoryService>().Setup(s => s.MostRecentForMovie(_movie.Id)).Returns(new MovieHistory
            {
                MovieEditionId = 1, EventType = MovieHistoryEventType.Grabbed, Date = DateTime.UtcNow, Quality = new QualityModel(Quality.Bluray1080p)
            });
            Mocker.Resolve<HistorySpecification>().IsSatisfiedBy(_subject, new()).Accepted.Should().BeTrue();
            Mocker.GetMock<IHistoryService>().Verify(s => s.MostRecentForEdition(_movie.Id, 2), Times.Once());
            Mocker.GetMock<IHistoryService>().Verify(s => s.MostRecentForMovie(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void same_edition_history_uses_selected_profile()
        {
            _profile.UpgradeAllowed = false;
            _movie.QualityProfile.UpgradeAllowed = true;
            Mocker.GetMock<IHistoryService>().Setup(s => s.MostRecentForEdition(_movie.Id, 2)).Returns(new MovieHistory
            {
                MovieEditionId = 2, EventType = MovieHistoryEventType.Grabbed, Date = DateTime.UtcNow, Quality = new QualityModel(Quality.DVD)
            });
            Mocker.Resolve<HistorySpecification>().IsSatisfiedBy(_subject, new()).Reason.Should().Be(DownloadRejectionReason.HistoryRecentCutoffMet);
            _movie.QualityProfile.UpgradeAllowed.Should().BeTrue();
        }
    }
}
