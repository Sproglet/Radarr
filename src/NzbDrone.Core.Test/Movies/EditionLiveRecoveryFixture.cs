using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class EditionLiveRecoveryFixture : DbTest
    {
        private Movie _movie;
        private MovieEdition _imax;
        private MovieEdition _directors;
        private MovieRepository _movies;
        private MovieEditionRepository _editions;
        private IMovieEditionRecoveryService _recovery;
        private HistoryService _history;
        private DownloadHistoryService _downloads;
        private PendingReleaseRepository _pending;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Language = Language.English });
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew().With(m => m.Title = "Synthetic Voyage").With(m => m.Year = 2026).BuildNew());
            _movies = Mocker.Resolve<MovieRepository>();
            _movie = _movies.Insert(new Movie { MovieMetadataId = metadata.Id, QualityProfileId = profile.Id, Path = TempFolder });
            _editions = Mocker.Resolve<MovieEditionRepository>();
            _imax = _editions.Get(_movie.PrimaryEditionId.Value);
            _imax.Name = "Theatrical IMAX";
            _imax.Attributes = new Dictionary<string, string> { { "cut", "theatrical" }, { "framing", "imax" } };
            _editions.Update(_imax);
            _directors = _editions.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "directors-3d", Name = "Directors Cut 3D", QualityProfileId = profile.Id,
                Attributes = new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } } });
            Mocker.SetConstant<IMovieEditionRepository>(_editions);
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            Mocker.SetConstant<IMovieEditionContextService>(Mocker.Resolve<MovieEditionContextService>());
            _recovery = Mocker.Resolve<MovieEditionRecoveryService>();
            Mocker.SetConstant(_recovery);
            Mocker.SetConstant<IHistoryRepository>(Mocker.Resolve<HistoryRepository>());
            Mocker.SetConstant<IDownloadHistoryRepository>(Mocker.Resolve<DownloadHistoryRepository>());
            _history = Mocker.Resolve<HistoryService>();
            _downloads = Mocker.Resolve<DownloadHistoryService>();
            Mocker.SetConstant<IHistoryService>(_history);
            Mocker.SetConstant<IDownloadHistoryService>(_downloads);
            _pending = Mocker.Resolve<PendingReleaseRepository>();
            Mocker.SetConstant<IPendingReleaseRepository>(_pending);
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids => _movies.Get(ids).ToList());
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<int>()))
                .Returns<ParsedMovieInfo, int>((parsed, id) => new RemoteMovie { Movie = _movies.Get(id), ParsedMovieInfo = parsed });
            var delay = new DelayProfile { PreferredProtocol = DownloadProtocol.Usenet };
            Mocker.GetMock<IDelayProfileService>().Setup(s => s.AllForTags(It.IsAny<HashSet<int>>())).Returns(new List<DelayProfile> { delay });
            Mocker.GetMock<IDelayProfileService>().Setup(s => s.BestForTags(It.IsAny<HashSet<int>>())).Returns(delay);
        }

        private RemoteMovie Remote(MovieEdition edition)
        {
            var title = "Synthetic.Voyage.2026." + (edition.Id == _imax.Id ? "Theatrical.IMAX" : "Directors.Cut.3D") + ".1080p.BluRay-SyntheticGroup";
            return new RemoteMovie { Movie = _movies.Get(_movie.Id), ParsedMovieInfo = Parser.Parser.ParseMovieTitle(title),
                EditionContext = Mocker.Resolve<IMovieEditionContextService>().Resolve(_movie, edition.Id),
                Release = new ReleaseInfo { Title = title, Guid = "synthetic-release-" + edition.Id, IndexerId = 8, Indexer = "Synthetic Indexer", PublishDate = DateTime.UtcNow, DownloadProtocol = DownloadProtocol.Usenet } };
        }

        private void Grab(MovieEdition edition, string id)
        {
            var message = new MovieGrabbedEvent(Remote(edition)) { DownloadId = id, DownloadClientId = 9, DownloadClient = "Synthetic", DownloadClientName = "Synthetic" };
            _history.Handle(message);
            _downloads.Handle(message);
        }

        private TrackedDownloadService ColdService()
        {
            return new TrackedDownloadService(Mocker.GetMock<IParsingService>().Object, _history, _downloads,
                Mocker.GetMock<IRemoteMovieAggregationService>().Object, Mocker.GetMock<ICustomFormatCalculationService>().Object,
                Mocker.GetMock<IEventAggregator>().Object, new CacheManager(), _recovery, TestLogger);
        }

        private TrackedDownload Track(TrackedDownloadService service, string id, string title = "opaque")
        {
            return service.TrackDownload(new DownloadClientDefinition { Id = 9, Protocol = DownloadProtocol.Usenet },
                new DownloadClientItem { DownloadId = id, Title = title, DownloadClientInfo = new DownloadClientItemClientInfo { Id = 9, Name = "Synthetic", Type = "Synthetic", Protocol = DownloadProtocol.Usenet } });
        }

        [TestCase("opaque")]
        [TestCase("Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup")]
        public void cold_cache_recovers_two_editions_after_rule_changes_without_reclassification(string title)
        {
            Grab(_imax, "synthetic-imax");
            Grab(_directors, "synthetic-directors");
            _directors.MatchRules.Add("DifferentSyntheticGroup$");
            _editions.Update(_directors);
            var cold = ColdService();
            Track(cold, "synthetic-imax", title).RemoteMovie.EditionContext.Edition.Id.Should().Be(_imax.Id);
            var secondary = Track(cold, "synthetic-directors", title);
            secondary.EditionRecoveryError.Should().BeNull();
            secondary.RemoteMovie.EditionContext.Edition.Id.Should().Be(_directors.Id);
            cold.GetTrackedDownloads().Should().HaveCount(2);
            cold.Handle(new MovieEditedEvent(_movies.Get(_movie.Id), _movie));
            cold.Find("synthetic-directors").RemoteMovie.EditionContext.Edition.Id.Should().Be(_directors.Id);
            Track(ColdService(), "synthetic-directors", title).RemoteMovie.EditionContext.Edition.Id.Should().Be(_directors.Id);
        }

        [Test]
        public void contradictory_client_evidence_blocks_completed_import()
        {
            Grab(_directors, "synthetic-directors");
            var tracked = Track(ColdService(), "synthetic-directors", "Synthetic.Voyage.2026.Theatrical.IMAX.1080p.BluRay-SyntheticGroup");
            tracked.EditionRecoveryError.Should().Contain("contradicts");
            Mocker.Resolve<CompletedDownloadService>().Check(tracked);
            Mocker.Resolve<CompletedDownloadService>().Import(tracked);
            tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [Test]
        public void missing_target_does_not_fall_back_to_primary()
        {
            Grab(_directors, "synthetic-directors");
            using var connection = Mocker.Resolve<NzbDrone.Core.Datastore.IMainDatabase>().OpenConnection();
            connection.Execute("DELETE FROM \"MovieEditions\" WHERE \"Id\" = @Id", new { _directors.Id });
            var tracked = Track(ColdService(), "synthetic-directors");
            tracked.EditionRecoveryError.Should().NotBeNull();
            tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void completion_and_failure_keep_original_grab_identity_and_name(bool failure)
        {
            Grab(_directors, "synthetic-directors");
            _directors.Name = "Synthetic Renamed Edition";
            _editions.Update(_directors);
            var tracked = Track(ColdService(), "synthetic-directors");
            if (failure)
            {
                _downloads.Handle(new DownloadFailedEvent { MovieId = _movie.Id, DownloadId = "synthetic-directors", SourceTitle = "opaque", TrackedDownload = tracked });
            }
            else
            {
                _downloads.Handle(new DownloadCompletedEvent(tracked, _movie.Id));
            }

            var latest = _downloads.GetLatestDownloadHistoryItem("synthetic-directors");
            latest.MovieEditionId.Should().Be(_directors.Id);
            latest.Data["movieEditionName"].Should().Be("Directors Cut 3D");
        }

        [Test]
        public void completed_download_does_not_become_importable_when_target_is_removed()
        {
            Grab(_directors, "synthetic-directors");
            var tracked = Track(ColdService(), "synthetic-directors");
            _downloads.Handle(new DownloadCompletedEvent(tracked, _movie.Id));
            _editions.Delete(_directors.Id);
            var reloaded = Track(ColdService(), "synthetic-directors");
            reloaded.State.Should().Be(TrackedDownloadState.Imported);
            reloaded.EditionRecoveryError.Should().BeNull();
        }

        [Test]
        public void typed_grab_from_another_client_is_not_reused_even_after_completion()
        {
            Grab(_directors, "synthetic-directors");
            _downloads.Handle(new DownloadCompletedEvent(Track(ColdService(), "synthetic-directors"), _movie.Id));
            var otherClient = ColdService().TrackDownload(new DownloadClientDefinition { Id = 10 },
                new DownloadClientItem { DownloadId = "synthetic-directors", Title = "opaque", DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Synthetic Other" } });
            otherClient.EditionRecoveryError.Should().Contain("another download client");
            otherClient.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [Test]
        public void foreign_client_terminal_event_cannot_mark_an_active_typed_grab_complete()
        {
            Grab(_directors, "synthetic-directors");
            Mocker.Resolve<DownloadHistoryRepository>().Insert(new DownloadHistory
            {
                MovieId = _movie.Id, DownloadClientId = 10, DownloadId = "synthetic-directors", SourceTitle = "opaque",
                Date = DateTime.UtcNow.AddSeconds(1), EventType = DownloadHistoryEventType.DownloadIgnored
            });
            var tracked = Track(ColdService(), "synthetic-directors");
            tracked.EditionRecoveryError.Should().NotBeNull();
            tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private PendingRelease Pending(MovieEdition edition)
        {
            var remote = Remote(edition);
            return _pending.Insert(new PendingRelease { MovieId = _movie.Id, MovieEditionId = edition.Id, EditionRuleRevision = edition.RuleRevision,
                Added = DateTime.UtcNow, Title = remote.Release.Title, Release = remote.Release, ParsedMovieInfo = remote.ParsedMovieInfo });
        }

        [Test]
        public void reconstructed_pending_queue_keeps_both_editions_and_removes_only_selected_target()
        {
            var primary = Pending(_imax);
            Pending(_directors);
            var service = Mocker.Resolve<PendingReleaseService>();
            var queue = service.GetPendingQueue();
            queue.Should().HaveCount(2);
            service.RemovePendingQueueItems(queue.Single(q => q.RemoteMovie.EditionContext.Edition.Id == _directors.Id).Id);
            _pending.All().Should().ContainSingle().Which.Id.Should().Be(primary.Id);
        }

        [Test]
        public void unassigned_multi_edition_pending_work_remains_visible_for_review()
        {
            Pending(_imax);
            Pending(_directors);
            var remote = Remote(_imax);
            _pending.Insert(new PendingRelease { MovieId = _movie.Id, Title = remote.Release.Title, Release = remote.Release,
                ParsedMovieInfo = remote.ParsedMovieInfo, Added = DateTime.UtcNow });
            var service = Mocker.Resolve<PendingReleaseService>();
            var queue = service.GetPendingQueue();
            queue.Should().HaveCount(3);
            queue.Should().ContainSingle(q => q.ErrorMessage != null);
            service.GetPendingRemoteMovies(_movie.Id).Should().HaveCount(2);
            _pending.All().Should().HaveCount(3);
        }

        [Test]
        public void grabbing_secondary_does_not_remove_primary_pending_work()
        {
            var primary = Pending(_imax);
            Pending(_directors);
            Mocker.Resolve<PendingReleaseService>().Handle(new MovieGrabbedEvent(Remote(_directors)));
            _pending.All().Should().ContainSingle().Which.Id.Should().Be(primary.Id);
        }

        private PendingReleaseService PendingService()
        {
            Mocker.GetMock<IIndexerStatusService>().Setup(s => s.GetBlockedProviders()).Returns(new List<IndexerStatus>());
            return Mocker.Resolve<PendingReleaseService>();
        }

        private DownloadDecisionMaker DecisionMaker(PendingReleaseService pending)
        {
            Mocker.SetConstant<IPendingReleaseService>(pending);
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(Array.Empty<IDownloadDecisionEngineSpecification>());
            return Mocker.Resolve<DownloadDecisionMaker>();
        }

        [Test]
        public void rss_reloads_saved_assignment_after_rules_change_instead_of_classifying_again()
        {
            var saved = Pending(_directors);
            _imax.MatchRules.Add("Synthetic");
            _editions.Update(_imax);
            _directors.MatchRules.Add("DifferentSyntheticGroup$");
            _editions.Update(_directors);
            var pending = PendingService();
            var maker = DecisionMaker(pending);
            var decision = maker.GetRssDecision(pending.GetPending()).Single();

            decision.RemoteMovie.EditionContext.Edition.Id.Should().Be(_directors.Id);
            decision.RemoteMovie.EditionEvaluation.Classification.RuleRevision.Should().Be(saved.EditionRuleRevision);
            decision.RemoteMovie.DownloadAllowed.Should().BeFalse();
            decision.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.MultiEditionAcquisitionDisabled);
            Mocker.GetMock<IMovieEditionEvaluationService>().Verify(s => s.PreviewParsed(It.IsAny<RemoteMovie>()), Times.Never());
            pending.Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision> { decision })));
            _pending.Get(saved.Id).MovieEditionId.Should().Be(_directors.Id);
            _pending.Get(saved.Id).EditionRuleRevision.Should().Be(saved.EditionRuleRevision);

            // Exercise persisted event handling with a synthetic event, without a client submission.
            var grab = new MovieGrabbedEvent(decision.RemoteMovie) { DownloadId = "synthetic-pending-grab", DownloadClientId = 9 };
            _history.Handle(grab);
            _downloads.Handle(grab);
            _history.FindByDownloadId(grab.DownloadId).Single().EditionRuleRevision.Should().Be(saved.EditionRuleRevision);
            _downloads.GetLatestGrab(grab.DownloadId).EditionRuleRevision.Should().Be(saved.EditionRuleRevision);
        }

        [Test]
        public void rss_prefers_both_persisted_targets_over_fresh_copies_of_the_same_releases()
        {
            var primary = Pending(_imax);
            var secondary = Pending(_directors);
            var pending = PendingService();
            Mocker.SetConstant<IMakeDownloadDecision>(DecisionMaker(pending));
            Mocker.GetMock<IFetchAndParseRss>().Setup(s => s.Fetch()).ReturnsAsync(new List<ReleaseInfo> { primary.Release, secondary.Release });
            List<DownloadDecision> processed = null;
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .Callback<List<DownloadDecision>>(d => processed = d)
                .ReturnsAsync(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));

            Mocker.Resolve<RssSyncService>().Execute(new RssSyncCommand());

            processed.Should().HaveCount(2);
            processed.Select(d => d.RemoteMovie.EditionContext.Edition.Id).Should().BeEquivalentTo(new[] { _imax.Id, _directors.Id });
            processed.Should().OnlyContain(d => !d.Approved && !d.RemoteMovie.DownloadAllowed);
        }

        [Test]
        public void rss_does_not_reclassify_unassigned_legacy_pending_work()
        {
            var remote = Remote(_imax);
            _pending.Insert(new PendingRelease { MovieId = _movie.Id, Release = remote.Release, Title = remote.Release.Title,
                ParsedMovieInfo = remote.ParsedMovieInfo, Added = DateTime.UtcNow });
            var pending = PendingService();
            var decision = DecisionMaker(pending).GetRssDecision(pending.GetPending()).Single();
            decision.RemoteMovie.EditionContext.Should().BeNull();
            decision.RemoteMovie.EditionEvaluation.Classification.Status.Should().Be(EditionClassificationStatus.Invalid);
            decision.RemoteMovie.EditionRecoveryError.Should().Contain("manual edition review");
            decision.Approved.Should().BeFalse();
            _pending.All().Should().ContainSingle().Which.MovieEditionId.Should().BeNull();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void rss_fails_closed_if_saved_pending_or_its_target_disappears(bool deleteTarget)
        {
            var saved = Pending(_directors);
            var pending = PendingService();
            var reports = pending.GetPending();
            if (deleteTarget)
            {
                using var connection = Mocker.Resolve<NzbDrone.Core.Datastore.IMainDatabase>().OpenConnection();
                connection.Execute("DELETE FROM \"MovieEditions\" WHERE \"Id\" = @Id", new { _directors.Id });
            }
            else
            {
                _pending.Delete(saved.Id);
            }

            var decision = DecisionMaker(pending).GetRssDecision(reports).Single();
            decision.Approved.Should().BeFalse();
            decision.RemoteMovie.EditionContext.Should().BeNull();
            decision.RemoteMovie.EditionRecoveryError.Should().NotBeNullOrEmpty();
            decision.RemoteMovie.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        public void conflicting_pending_targets_require_review_without_deleting_either_record()
        {
            var saved = Pending(_directors);
            _pending.Insert(new PendingRelease { MovieId = _movie.Id, MovieEditionId = _imax.Id, Release = saved.Release,
                Title = saved.Title, ParsedMovieInfo = saved.ParsedMovieInfo, Added = DateTime.UtcNow });
            var pending = PendingService();
            var decisions = DecisionMaker(pending).GetRssDecision(pending.GetPending());
            decisions.Should().HaveCount(2).And.OnlyContain(d => !d.Approved && d.RemoteMovie.EditionContext == null);
            decisions.Should().OnlyContain(d => d.RemoteMovie.EditionRecoveryError.Contains("conflicting pending"));
            _pending.All().Should().HaveCount(2);
        }

        [Test]
        public void pending_insert_deduplicates_repeats_in_one_batch()
        {
            var decision = new DownloadDecision(Remote(_directors));
            PendingService().AddMany(new List<Tuple<DownloadDecision, PendingReleaseReason>>
            {
                Tuple.Create(decision, PendingReleaseReason.Delay), Tuple.Create(decision, PendingReleaseReason.Delay)
            });
            _pending.All().Should().ContainSingle().Which.MovieEditionId.Should().Be(_directors.Id);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_insert_rejects_conflicting_target_without_reassigning_or_deleting(bool existing)
        {
            var primary = Remote(_imax);
            var secondary = Remote(_directors);
            secondary.Release = primary.Release;
            var pending = PendingService();
            var first = Tuple.Create(new DownloadDecision(primary), PendingReleaseReason.Delay);
            var second = Tuple.Create(new DownloadDecision(secondary), PendingReleaseReason.Delay);
            if (existing)
            {
                pending.Add(first.Item1, first.Item2);
            }

            Action add = () => pending.AddMany(existing ? new List<Tuple<DownloadDecision, PendingReleaseReason>> { second }
                : new List<Tuple<DownloadDecision, PendingReleaseReason>> { first, second });
            add.Should().Throw<InvalidOperationException>();
            _pending.All().Should().HaveCount(existing ? 1 : 0);
            if (existing)
            {
                _pending.All().Single().MovieEditionId.Should().Be(_imax.Id);
            }
        }

        [Test]
        public void oldest_pending_is_scoped_to_the_requested_edition()
        {
            var primary = Pending(_imax);
            primary.Release.PublishDate = DateTime.UtcNow.AddDays(-1);
            _pending.Update(primary);
            var secondary = Pending(_directors);
            var pending = PendingService();
            pending.OldestPendingReleaseForEdition(_movie.Id, _directors.Id).Release.Title.Should().Be(secondary.Title);
            pending.OldestPendingReleaseForEdition(_movie.Id, _imax.Id).Release.Title.Should().Be(primary.Title);
        }

        [Test]
        public void cold_queue_recovery_restores_release_identity_for_cross_edition_deduplication()
        {
            Grab(_directors, "synthetic-directors");
            var recovered = Track(ColdService(), "synthetic-directors").RemoteMovie;
            recovered.Release.Guid.Should().Be(Remote(_directors).Release.Guid);
            recovered.Release.IndexerId.Should().Be(8);
            recovered.Release.DownloadProtocol.Should().Be(DownloadProtocol.Usenet);
            Mocker.GetMock<NzbDrone.Core.Queue.IQueueService>().Setup(s => s.GetQueue()).Returns(new List<NzbDrone.Core.Queue.Queue>
            {
                new NzbDrone.Core.Queue.Queue { RemoteMovie = recovered }
            });
            var other = Remote(_imax);
            other.Release = recovered.Release;
            var decision = Mocker.Resolve<QueueSpecification>().IsSatisfiedBy(other, new());
            decision.Accepted.Should().BeFalse();
            decision.Message.Should().Contain("already queued");
        }

        private TorrentInfo Torrent()
        {
            return new TorrentInfo { Title = Remote(_directors).Release.Title, IndexerId = 8, Indexer = "Synthetic Indexer",
                DownloadProtocol = DownloadProtocol.Torrent, InfoHash = "abcdef0123456789abcdef0123456789abcdef01",
                Guid = "synthetic-torrent", MagnetUrl = "magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01", Seeders = 17, Peers = 23 };
        }

        [Test]
        public void pending_torrent_identity_and_availability_survive_database_roundtrip()
        {
            var remote = Remote(_directors);
            remote.Release = Torrent();
            var pending = PendingService();
            pending.Add(new DownloadDecision(remote), PendingReleaseReason.Delay);
            var stored = pending.GetPending().Single().Should().BeOfType<TorrentInfo>().Subject;
            stored.InfoHash.Should().Be(Torrent().InfoHash);
            stored.MagnetUrl.Should().Be(Torrent().MagnetUrl);
            stored.Seeders.Should().Be(17);
            stored.Peers.Should().Be(23);
            var anotherIndexer = Torrent();
            anotherIndexer.IndexerId = 19;
            anotherIndexer.Guid = "synthetic-other-guid";
            anotherIndexer.InfoHash = anotherIndexer.InfoHash.ToUpperInvariant();
            ReleaseIdentity.Matches(stored, anotherIndexer).Should().BeTrue();
        }

        [Test]
        public void recovered_torrent_queue_deduplicates_same_hash_from_another_indexer()
        {
            var remote = Remote(_directors);
            remote.Release = Torrent();
            var grab = new MovieGrabbedEvent(remote) { DownloadId = "synthetic-torrent", DownloadClientId = 9 };
            _history.Handle(grab);
            _downloads.Handle(grab);
            var recovered = Track(ColdService(), grab.DownloadId).RemoteMovie;
            recovered.Release.Should().BeOfType<TorrentInfo>().Which.InfoHash.Should().Be(Torrent().InfoHash);
            Mocker.GetMock<NzbDrone.Core.Queue.IQueueService>().Setup(s => s.GetQueue()).Returns(new List<NzbDrone.Core.Queue.Queue>
            {
                new NzbDrone.Core.Queue.Queue { RemoteMovie = recovered }
            });
            var other = Remote(_imax);
            var torrent = Torrent();
            torrent.IndexerId = 19;
            torrent.Guid = "synthetic-other-guid";
            other.Release = torrent;
            Mocker.Resolve<QueueSpecification>().IsSatisfiedBy(other, new()).Message.Should().Contain("already queued");
        }

        private DownloadFailedEvent CaptureFailure(Action fail)
        {
            DownloadFailedEvent result = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<DownloadFailedEvent>())).Callback<DownloadFailedEvent>(e => result = e);
            fail();
            return result;
        }

        private TrackedDownload TrackOtherClient(TrackedDownloadService service, string id)
        {
            return service.TrackDownload(new DownloadClientDefinition { Id = 10, Protocol = DownloadProtocol.Usenet },
                new DownloadClientItem { DownloadId = id, Title = "opaque", DownloadClientInfo = new DownloadClientItemClientInfo
                { Id = 10, Name = "Synthetic Other Client", Type = "Synthetic", Protocol = DownloadProtocol.Usenet } });
        }

        [Test]
        public void identical_download_ids_on_two_clients_keep_independent_cache_entries()
        {
            Grab(_directors, "synthetic-shared-id");
            var service = ColdService();
            var own = Track(service, "synthetic-shared-id");
            var other = TrackOtherClient(service, "synthetic-shared-id");
            service.GetTrackedDownloads().Should().HaveCount(2);
            service.Find(9, "synthetic-shared-id").Should().BeSameAs(own);
            service.Find(10, "synthetic-shared-id").Should().BeSameAs(other);
            other.EditionRecoveryError.Should().NotBeNull(); // History recovery remains conservative.
            Action ambiguous = () => service.Find("synthetic-shared-id");
            ambiguous.Should().Throw<InvalidOperationException>();
            Track(service, "synthetic-shared-id").RemoteMovie.EditionContext.Edition.Id.Should().Be(_directors.Id);
            service.Find(10, "synthetic-shared-id").Should().BeSameAs(other);
        }

        [Test]
        public void client_specific_stop_and_trackability_refresh_preserve_the_other_client()
        {
            var service = ColdService();
            var first = Track(service, "synthetic-shared-id");
            var other = TrackOtherClient(service, "synthetic-shared-id");
            service.UpdateTrackable(new List<TrackedDownload> { first });
            first.IsTrackable.Should().BeTrue();
            other.IsTrackable.Should().BeFalse();
            service.StopTracking(9, "synthetic-shared-id");
            service.Find(9, "synthetic-shared-id").Should().BeNull();
            service.Find(10, "synthetic-shared-id").Should().BeSameAs(other);
            service.Find("synthetic-shared-id").Should().BeSameAs(other);
        }

        [Test]
        public void ambiguous_bulk_stop_does_not_partially_remove_resolved_items()
        {
            var service = ColdService();
            Track(service, "synthetic-unique-id");
            Track(service, "synthetic-shared-id");
            TrackOtherClient(service, "synthetic-shared-id");
            Action stop = () => service.StopTracking(new List<string> { "synthetic-unique-id", "synthetic-shared-id" });
            stop.Should().Throw<InvalidOperationException>();
            service.GetTrackedDownloads().Should().HaveCount(3);
        }

        [Test]
        public void grabbed_event_evicts_only_its_clients_terminal_entry()
        {
            var service = ColdService();
            Track(service, "synthetic-shared-id").State = TrackedDownloadState.Imported;
            var other = TrackOtherClient(service, "synthetic-shared-id");
            other.State = TrackedDownloadState.Imported;
            service.Handle(new MovieGrabbedEvent(Remote(_imax)) { DownloadId = "synthetic-shared-id", DownloadClientId = 9 });
            service.Find(9, "synthetic-shared-id").Should().BeNull();
            service.Find(10, "synthetic-shared-id").Should().BeSameAs(other);
        }

        [Test]
        public void queue_rows_retain_client_identity_for_colliding_download_ids()
        {
            var service = ColdService();
            var first = Track(service, "synthetic-shared-id");
            var other = TrackOtherClient(service, "synthetic-shared-id");
            var queue = Mocker.Resolve<NzbDrone.Core.Queue.QueueService>();
            try
            {
                queue.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { first, other }));
                queue.GetQueue().Select(q => q.DownloadClientId).Should().BeEquivalentTo(new int?[] { 9, 10 });
                queue.GetQueue().Select(q => q.Id).Distinct().Should().HaveCount(2);
            }
            finally
            {
                queue.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>()));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void manual_history_failure_preserves_target_snapshot_and_releases_only_its_grab(bool skipRedownload)
        {
            Grab(_imax, "synthetic-imax");
            Grab(_directors, "synthetic-directors");
            var original = _history.FindByDownloadId("synthetic-directors").Single();
            _directors.Name = "Synthetic Renamed Edition";
            _directors.MatchRules.Add("DifferentSyntheticGroup$");
            _editions.Update(_directors);
            var failure = CaptureFailure(() => Mocker.Resolve<FailedDownloadService>().MarkAsFailed(original.Id, skipRedownload));

            failure.Should().NotBeNull();
            failure.MovieEditionId.Should().Be(_directors.Id);
            failure.EditionRuleRevision.Should().Be(original.EditionRuleRevision);
            failure.MovieEditionName.Should().Be("Directors Cut 3D");
            failure.DownloadClientId.Should().Be(9);
            failure.SkipRedownload.Should().Be(skipRedownload);
            _history.Handle(failure);
            _downloads.Handle(failure);
            var stored = _history.FindByDownloadId("synthetic-directors").Single(h => h.EventType == MovieHistoryEventType.DownloadFailed);
            stored.MovieEditionId.Should().Be(_directors.Id);
            stored.EditionRuleRevision.Should().Be(original.EditionRuleRevision);
            stored.Data["movieEditionName"].Should().Be("Directors Cut 3D");
            _downloads.GetLatestDownloadHistoryItem("synthetic-directors").EventType.Should().Be(DownloadHistoryEventType.DownloadFailed);
            _downloads.GetLatestDownloadHistoryItem("synthetic-imax").EventType.Should().Be(DownloadHistoryEventType.DownloadGrabbed);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.AutoRedownloadFailed).Returns(true);
            Mocker.Resolve<RedownloadFailedDownloadService>().Handle(failure);
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoviesSearchCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
            _editions.Delete(_directors.Id);
            _editions.GetByMovie(_movie.Id).Should().ContainSingle().Which.Id.Should().Be(_imax.Id);
            _history.FindByDownloadId("synthetic-directors").Should().OnlyContain(h => h.MovieEditionId == _directors.Id);
        }

        [Test]
        public void tracked_failure_uses_original_snapshot_after_rule_and_name_edits()
        {
            Grab(_directors, "synthetic-directors");
            var original = _history.FindByDownloadId("synthetic-directors").Single();
            _directors.Name = "Synthetic Renamed Edition";
            _directors.MatchRules.Add("DifferentSyntheticGroup$");
            _editions.Update(_directors);
            var tracked = Track(ColdService(), "synthetic-directors");
            tracked.DownloadItem.Status = DownloadItemStatus.Failed;
            var service = Mocker.Resolve<FailedDownloadService>();
            service.Check(tracked);
            tracked.State.Should().Be(TrackedDownloadState.FailedPending);
            var failure = CaptureFailure(() => service.ProcessFailed(tracked));
            failure.MovieEditionId.Should().Be(_directors.Id);
            failure.MovieEditionName.Should().Be("Directors Cut 3D");
            failure.EditionRuleRevision.Should().Be(original.EditionRuleRevision);
            tracked.State.Should().Be(TrackedDownloadState.Failed);
            failure.SourceTitle.Should().Be(original.SourceTitle);
        }

        [TestCase("recovery-error")]
        [TestCase("foreign-client")]
        [TestCase("conflicting-grab")]
        [TestCase("removed-target")]
        public void unresolved_tracked_failure_is_blocked_before_events_or_client_cleanup(string problem)
        {
            Grab(_directors, "synthetic-directors");
            var tracked = Track(ColdService(), "synthetic-directors");
            tracked.DownloadItem.Status = DownloadItemStatus.Failed;
            if (problem == "recovery-error")
            {
                tracked.EditionRecoveryError = "Synthetic unresolved identity";
            }
            else if (problem == "foreign-client")
            {
                tracked.DownloadClient = 10;
            }
            else if (problem == "conflicting-grab")
            {
                Grab(_imax, "synthetic-directors");
            }
            else
            {
                using var connection = Mocker.Resolve<NzbDrone.Core.Datastore.IMainDatabase>().OpenConnection();
                connection.Execute("DELETE FROM \"MovieEditions\" WHERE \"Id\" = @Id", new { _directors.Id });
            }

            var service = Mocker.Resolve<FailedDownloadService>();
            var failure = CaptureFailure(() =>
            {
                service.Check(tracked);
                service.ProcessFailed(tracked);
                service.MarkAsFailed(tracked);
            });
            failure.Should().BeNull();
            tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
            tracked.EditionRecoveryError.Should().NotBeNullOrEmpty();
            _history.FindByDownloadId("synthetic-directors").Should().OnlyContain(h => h.EventType == MovieHistoryEventType.Grabbed);
        }

        [Test]
        public void ignoring_edition_uses_original_grab_snapshot_after_rename()
        {
            Grab(_directors, "synthetic-directors");
            _directors.Name = "Synthetic Renamed Edition";
            _directors.MatchRules.Add("DifferentSyntheticGroup$");
            _editions.Update(_directors);
            DownloadIgnoredEvent ignored = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<DownloadIgnoredEvent>())).Callback<DownloadIgnoredEvent>(e => ignored = e);
            Mocker.Resolve<IgnoredDownloadService>().IgnoreDownload(Track(ColdService(), "synthetic-directors")).Should().BeTrue();
            ignored.MovieEditionId.Should().Be(_directors.Id);
            ignored.MovieEditionName.Should().Be("Directors Cut 3D");
            ignored.EditionRuleRevision.Should().BeLessThan(_directors.RuleRevision);
            _history.Handle(ignored);
            _downloads.Handle(ignored);
            var row = _history.FindByDownloadId("synthetic-directors").Single(h => h.EventType == MovieHistoryEventType.DownloadIgnored);
            row.MovieEditionId.Should().Be(_directors.Id);
            row.Data["movieEditionName"].Should().Be("Directors Cut 3D");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unresolved_ignore_cannot_publish_a_terminal_event(bool foreignClient)
        {
            Grab(_directors, "synthetic-directors");
            var tracked = Track(ColdService(), "synthetic-directors");
            if (foreignClient)
            {
                tracked.DownloadClient = 10;
            }
            else
            {
                tracked.EditionRecoveryError = "Synthetic assignment needs review";
            }

            Mocker.Resolve<IgnoredDownloadService>().IgnoreDownload(tracked).Should().BeFalse();
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadIgnoredEvent>()), Times.Never());
        }

        [Test]
        public void failed_torrent_blocklist_follows_release_identity_across_editions()
        {
            var remote = Remote(_directors);
            remote.Release = Torrent();
            var grab = new MovieGrabbedEvent(remote) { DownloadId = Torrent().InfoHash, DownloadClientId = 9 };
            _history.Handle(grab);
            _downloads.Handle(grab);
            var row = _history.FindByDownloadId(grab.DownloadId).Single();
            var failure = CaptureFailure(() => Mocker.Resolve<FailedDownloadService>().MarkAsFailed(row.Id));
            Mocker.SetConstant<IBlocklistRepository>(Mocker.Resolve<BlocklistRepository>());
            var blocklist = Mocker.Resolve<BlocklistService>();
            blocklist.Handle(failure);
            Mocker.SetConstant<IBlocklistService>(blocklist);
            var other = Remote(_imax);
            other.Release = Torrent();
            Mocker.Resolve<BlocklistSpecification>().IsSatisfiedBy(other, new()).Accepted.Should().BeFalse();
            var distinct = Torrent();
            distinct.InfoHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            distinct.Title = Remote(_imax).Release.Title;
            other.Release = distinct;
            Mocker.Resolve<BlocklistSpecification>().IsSatisfiedBy(other, new()).Accepted.Should().BeTrue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void history_only_failure_cannot_end_an_unproven_or_foreign_client_grab(bool foreignClient)
        {
            Grab(_directors, "synthetic-directors");
            var original = _history.FindByDownloadId("synthetic-directors").Single();
            if (foreignClient)
            {
                var grab = _downloads.GetLatestGrab("synthetic-directors");
                grab.DownloadClientId = 10;
                Mocker.Resolve<DownloadHistoryRepository>().Update(grab);
            }
            else
            {
                original.Data.Remove(MovieHistory.DOWNLOAD_CLIENT_ID);
                Mocker.Resolve<HistoryRepository>().Update(original);
            }

            var failure = CaptureFailure(() => Mocker.Resolve<FailedDownloadService>().MarkAsFailed(original.Id));
            _history.Handle(failure);
            _downloads.Handle(failure);
            _downloads.GetLatestDownloadHistoryItem("synthetic-directors").EventType.Should().Be(DownloadHistoryEventType.DownloadGrabbed);
            _history.FindByDownloadId("synthetic-directors").Single(h => h.EventType == MovieHistoryEventType.DownloadFailed)
                .MovieEditionId.Should().Be(_directors.Id);
        }

        [Test]
        public void legacy_single_edition_failure_retains_its_retry_behavior()
        {
            _editions.Delete(_directors.Id);
            var remote = Remote(_imax);
            remote.EditionContext = null;
            var grab = new MovieGrabbedEvent(remote) { DownloadId = "synthetic-legacy", DownloadClientId = 9 };
            _history.Handle(grab);
            _downloads.Handle(grab);
            var tracked = Track(ColdService(), grab.DownloadId);
            tracked.DownloadItem.Status = DownloadItemStatus.Failed;
            var service = Mocker.Resolve<FailedDownloadService>();
            service.Check(tracked);
            var failure = CaptureFailure(() => service.ProcessFailed(tracked));
            failure.Should().NotBeNull();
            failure.MovieEditionId.Should().BeNull();
            Mocker.GetMock<IConfigService>().SetupGet(s => s.AutoRedownloadFailed).Returns(true);
            Mocker.Resolve<RedownloadFailedDownloadService>().Handle(failure);
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.Is<MoviesSearchCommand>(c => c.MovieIds.Count == 1 && c.MovieIds[0] == _movie.Id),
                CommandPriority.Normal, CommandTrigger.Unspecified), Times.Once());
        }
    }
}
