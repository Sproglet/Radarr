using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class EditionDownloadIdentityFixture : DbTest
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _secondary;
        private MovieEditionRepository _editions;
        private PendingReleaseRepository _pending;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<NzbDrone.Core.CustomFormats.ICustomFormatService>().Setup(s => s.All())
                .Returns(new List<NzbDrone.Core.CustomFormats.CustomFormat>());
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Language = NzbDrone.Core.Languages.Language.English });
            _movie = Mocker.Resolve<MovieRepository>().Insert(Builder<Movie>.CreateNew()
                .With(m => m.Id = 0).With(m => m.MovieFileId = 0).With(m => m.PrimaryEditionId = null).With(m => m.QualityProfileId = profile.Id).Build());
            _editions = Mocker.Resolve<MovieEditionRepository>();
            _primary = _editions.Get(_movie.PrimaryEditionId.Value);
            _secondary = _editions.Insert(new MovieEdition { MovieId = _movie.Id, Name = "Directors Cut 3D", EditionKey = "synthetic-directors-3d", QualityProfileId = profile.Id });
            _pending = Mocker.Resolve<PendingReleaseRepository>();
        }

        private PendingRelease Pending(MovieEdition edition)
        {
            return _pending.Insert(new PendingRelease
            {
                MovieId = _movie.Id, MovieEditionId = edition?.Id, EditionRuleRevision = edition?.RuleRevision,
                Title = "Synthetic.Voyage.2026-SyntheticGroup", Added = DateTime.UtcNow,
                ParsedMovieInfo = new ParsedMovieInfo(), Release = new ReleaseInfo(),
                AdditionalInfo = new PendingReleaseAdditionalInfo { EditionName = edition?.Name }
            });
        }

        [Test]
        public void pending_targets_and_legacy_rows_survive_repository_reload_without_cross_selection()
        {
            Pending(_primary);
            var secondary = Pending(_secondary);
            Pending(null);
            var reloaded = Mocker.Resolve<PendingReleaseRepository>();
            reloaded.AllByEdition(_movie.Id, _primary.Id).Should().HaveCount(1);
            reloaded.AllByEdition(_movie.Id, _secondary.Id).Should().ContainSingle().Which.Id.Should().Be(secondary.Id);
            reloaded.AllByEdition(_movie.Id, null).Should().HaveCount(1);
            reloaded.Get(secondary.Id).AdditionalInfo.EditionName.Should().Be("Directors Cut 3D");
            reloaded.Get(secondary.Id).EditionRuleRevision.Should().Be(_secondary.RuleRevision);
        }

        [Test]
        public void should_block_pending_target_deletion_and_reject_foreign_target()
        {
            var pending = Pending(_secondary);
            Action remove = () => _editions.Delete(_secondary.Id);
            remove.Should().Throw<InvalidOperationException>();
            _editions.Get(_secondary.Id).Should().NotBeNull();
            pending.MovieId = _movie.Id + 999;
            Action update = () => _pending.Update(pending);
            update.Should().Throw<InvalidOperationException>();
            _pending.Get(pending.Id).MovieId.Should().Be(_movie.Id);
            _pending.Delete(pending.Id);
            _editions.Delete(_secondary.Id);
        }

        [Test]
        public void both_grab_handlers_persist_target_revision_and_name_snapshot()
        {
            var history = Mocker.Resolve<HistoryRepository>();
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            Mocker.SetConstant<IHistoryRepository>(history);
            Mocker.SetConstant<IDownloadHistoryRepository>(downloads);
            var remote = new RemoteMovie
            {
                Movie = _movie, EditionContext = new MovieEditionContext(_secondary, null, null, true),
                ParsedMovieInfo = Parser.Parser.ParseMovieTitle("Synthetic.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup"),
                Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026-SyntheticGroup", Indexer = "Synthetic Indexer", PublishDate = DateTime.UtcNow }
            };
            var message = new NzbDrone.Core.Download.MovieGrabbedEvent(remote) { DownloadId = "synthetic-download", DownloadClient = "Synthetic Client", DownloadClientName = "Synthetic Client" };
            Mocker.Resolve<HistoryService>().Handle(message);
            Mocker.Resolve<DownloadHistoryService>().Handle(message);
            var record = history.MostRecentForEdition(_movie.Id, _secondary.Id);
            record.EditionRuleRevision.Should().Be(_secondary.RuleRevision);
            record.Data["movieEditionName"].Should().Be(_secondary.Name);
            var download = Mocker.Resolve<DownloadHistoryRepository>().FindByDownloadId("synthetic-download")[0];
            download.MovieEditionId.Should().Be(_secondary.Id);
            download.EditionRuleRevision.Should().Be(_secondary.RuleRevision);
            download.Data["movieEditionName"].Should().Be(_secondary.Name);
            Action remove = () => _editions.Delete(_secondary.Id);
            remove.Should().Throw<InvalidOperationException>();
            download.Id = 0;
            download.EventType = DownloadHistoryEventType.DownloadImported;
            download.Date = DateTime.UtcNow.AddSeconds(1);
            downloads.Insert(download);
            _editions.Delete(_secondary.Id);
            history.Get(record.Id).Data["movieEditionName"].Should().Be("Directors Cut 3D");
        }

        [Test]
        public void recovery_rejects_disagreement_and_does_not_guess_legacy_assignment()
        {
            var recovery = new MovieEditionRecoveryService(Mocker.GetMock<IMovieEditionContextService>().Object, _editions);
            Action unassigned = () => recovery.Recover(_movie, null, null);
            unassigned.Should().Throw<InvalidOperationException>();
            var grab = new MovieHistory { MovieId = _movie.Id, MovieEditionId = _primary.Id, EventType = MovieHistoryEventType.Grabbed, DownloadId = "synthetic-download" };
            var download = new DownloadHistory { MovieId = _movie.Id, MovieEditionId = _secondary.Id, EventType = DownloadHistoryEventType.DownloadGrabbed, DownloadId = "synthetic-download" };
            Action recover = () => recovery.Recover(_movie, grab, download);
            recover.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void grab_identity_and_name_survive_rule_edits_and_target_removal()
        {
            var history = Mocker.Resolve<HistoryRepository>();
            var record = history.Insert(new MovieHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                SourceTitle = "Synthetic.Voyage.2026-SyntheticGroup", DownloadId = "synthetic-download",
                EventType = MovieHistoryEventType.Grabbed, Date = DateTime.UtcNow,
                Quality = new NzbDrone.Core.Qualities.QualityModel(), Languages = new List<NzbDrone.Core.Languages.Language>(),
                Data = new Dictionary<string, string> { { "movieEditionName", _secondary.Name } }
            });
            _secondary.MatchRules.Add("DifferentSyntheticGroup$");
            _secondary.Name = "Synthetic Renamed Edition";
            _editions.Update(_secondary);
            var reloaded = Mocker.Resolve<HistoryRepository>().MostRecentForEdition(_movie.Id, _secondary.Id);
            reloaded.Id.Should().Be(record.Id);
            reloaded.EditionRuleRevision.Should().Be(1);
            reloaded.Data["movieEditionName"].Should().Be("Directors Cut 3D");
            Mocker.SetConstant<IMovieEditionRepository>(_editions);
            Mocker.SetConstant<NzbDrone.Core.MediaFiles.IMediaFileRepository>(Mocker.Resolve<NzbDrone.Core.MediaFiles.MediaFileRepository>());
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            var recovery = new MovieEditionRecoveryService(Mocker.Resolve<MovieEditionContextService>(), _editions);
            recovery.Recover(_movie, reloaded, null).Edition.Id.Should().Be(_secondary.Id);
            _editions.Delete(_secondary.Id);
            Action recover = () => recovery.Recover(_movie, reloaded, null);
            recover.Should().Throw<InvalidOperationException>();
            history.Get(record.Id).Data["movieEditionName"].Should().Be("Directors Cut 3D");
        }
    }
}
