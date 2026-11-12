using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
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
            var message = new NzbDrone.Core.Download.MovieGrabbedEvent(remote) { DownloadId = "synthetic-download", DownloadClientId = 41, DownloadClient = "Synthetic Client", DownloadClientName = "Synthetic Client" };
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
        public void unrelated_or_incomplete_settlement_cannot_release_target_deletion(
            [Values(DownloadHistoryEventType.DownloadImported, DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType eventType,
            [Values("movie", "target", "unassigned", "revision", "unknown-revision", "client", "download", "older")] string mismatch)
        {
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            var grabbed = DateTime.UtcNow;
            downloads.Insert(new DownloadHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                SourceTitle = "Synthetic.Voyage.2026-SyntheticGroup", DownloadClientId = 41, DownloadId = "synthetic-shared", Date = grabbed,
                EventType = DownloadHistoryEventType.DownloadGrabbed
            });
            var terminal = new DownloadHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                SourceTitle = "Synthetic.Voyage.2026-SyntheticGroup", DownloadClientId = 41, DownloadId = "synthetic-shared", Date = grabbed.AddMinutes(1), EventType = eventType
            };
            if (mismatch == "movie") terminal.MovieId += 999;
            if (mismatch == "target") terminal.MovieEditionId = _primary.Id;
            if (mismatch == "unassigned") terminal.MovieEditionId = null;
            if (mismatch == "revision") terminal.EditionRuleRevision = 2;
            if (mismatch == "unknown-revision") terminal.EditionRuleRevision = null;
            if (mismatch == "client") terminal.DownloadClientId = 42;
            if (mismatch == "download") terminal.DownloadId = "synthetic-other";
            if (mismatch == "older") terminal.Date = grabbed.AddMinutes(-1);
            downloads.Insert(terminal);

            Action remove = () => _editions.Delete(_secondary.Id);
            remove.Should().Throw<InvalidOperationException>();
            _editions.Get(_secondary.Id).MovieId.Should().Be(_movie.Id);
            _editions.Get(_secondary.Id).RuleRevision.Should().Be(1);
            downloads.All().Should().HaveCount(2);
        }

        [TestCase(DownloadHistoryEventType.DownloadImported)]
        [TestCase(DownloadHistoryEventType.DownloadFailed)]
        [TestCase(DownloadHistoryEventType.DownloadIgnored)]
        public void matching_saved_settlement_allows_deletion_after_rule_edits(DownloadHistoryEventType eventType)
        {
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            var saved = downloads.Insert(new DownloadHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                SourceTitle = "Synthetic.Voyage.2026-SyntheticGroup", DownloadClientId = 41, DownloadId = "synthetic-shared", Date = DateTime.UtcNow,
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                Data = new Dictionary<string, string> { { "movieEditionName", _secondary.Name } }
            });
            _secondary.MatchRules.Add("ChangedSyntheticGroup$");
            _editions.Update(_secondary);
            _editions.Get(_secondary.Id).RuleRevision.Should().Be(2);
            saved.Id = 0;
            saved.EventType = eventType;
            downloads.Insert(saved); // Same timestamp; the later identity determines event order.

            _editions.Delete(_secondary.Id);

            _editions.Find(_secondary.Id).Should().BeNull();
            downloads.FindByDownloadId(41, "synthetic-shared").Should().HaveCount(2)
                .And.OnlyContain(h => h.MovieEditionId == _secondary.Id && h.EditionRuleRevision == 1 && h.Data["movieEditionName"] == "Directors Cut 3D");
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

        [TestCase(1, 2)]
        [TestCase(null, 1)]
        [TestCase(1, null)]
        public void typed_grab_streams_with_different_saved_revisions_require_review(int? historyRevision, int? downloadRevision)
        {
            var recovery = new MovieEditionRecoveryService(Mocker.GetMock<IMovieEditionContextService>().Object, _editions);
            var grab = new MovieHistory { MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = historyRevision,
                EventType = MovieHistoryEventType.Grabbed, DownloadId = "synthetic-shared" };
            var download = new DownloadHistory { MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = downloadRevision,
                EventType = DownloadHistoryEventType.DownloadGrabbed, DownloadId = "synthetic-shared" };

            Action recover = () => recovery.Recover(_movie, grab, download);

            recover.Should().Throw<InvalidOperationException>().WithMessage("*saved rule revision*");
            Mocker.GetMock<IMovieEditionContextService>().Verify(s => s.Resolve(_movie, _secondary.Id, null), Moq.Times.Never());
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
                Data = new Dictionary<string, string> { { "movieEditionName", _secondary.Name }, { MovieHistory.DOWNLOAD_CLIENT_ID, "41" } }
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
            Mocker.Resolve<DownloadHistoryRepository>().Insert(new DownloadHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                DownloadClientId = 41, DownloadId = record.DownloadId, SourceTitle = record.SourceTitle,
                Date = record.Date.AddSeconds(1), EventType = DownloadHistoryEventType.DownloadImported
            });
            _editions.Delete(_secondary.Id);
            Action recover = () => recovery.Recover(_movie, reloaded, null);
            recover.Should().Throw<InvalidOperationException>();
            history.Get(record.Id).Data["movieEditionName"].Should().Be("Directors Cut 3D");
        }

        private MovieHistory HistoryGrab(bool knownClient = true)
        {
            var history = new MovieHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                SourceTitle = "Synthetic.Voyage.2026.Directors.Cut.3D-SyntheticGroup", DownloadId = "synthetic-history-only",
                EventType = MovieHistoryEventType.Grabbed, Date = DateTime.UtcNow,
                Quality = new NzbDrone.Core.Qualities.QualityModel(), Languages = new List<NzbDrone.Core.Languages.Language>(),
                Data = new Dictionary<string, string> { { "movieEditionName", _secondary.Name } }
            };
            if (knownClient)
            {
                history.Data[MovieHistory.DOWNLOAD_CLIENT_ID] = "41";
            }

            return history;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void typed_movie_history_insert_rejects_missing_or_foreign_owner(bool foreignMovie)
        {
            var history = Mocker.Resolve<HistoryRepository>();
            var grab = HistoryGrab();
            if (foreignMovie)
            {
                grab.MovieId += 999;
            }
            else
            {
                grab.MovieEditionId += 999;
            }

            Action insert = () => history.Insert(grab);

            insert.Should().Throw<InvalidOperationException>();
            history.All().Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task grab_insert_waits_for_parent_lock_and_rejects_a_concurrently_removed_target(bool downloadHistory)
        {
            var history = Mocker.Resolve<HistoryRepository>();
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            var grab = HistoryGrab();
            using var started = new ManualResetEventSlim();
            using var connection = Db.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @Id", _movie, transaction);
            var insert = Task.Run(() =>
            {
                started.Set();
                try
                {
                    if (downloadHistory)
                    {
                        downloads.Insert(new DownloadHistory
                        {
                            MovieId = grab.MovieId, MovieEditionId = grab.MovieEditionId, EditionRuleRevision = grab.EditionRuleRevision,
                            DownloadClientId = 41, DownloadId = grab.DownloadId, SourceTitle = grab.SourceTitle,
                            Date = grab.Date, EventType = DownloadHistoryEventType.DownloadGrabbed
                        });
                    }
                    else
                    {
                        history.Insert(grab);
                    }

                    return (Exception)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            try
            {
                started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                (await Task.WhenAny(insert, Task.Delay(100))).Should().NotBe(insert);
                connection.Execute(@"DELETE FROM ""MovieEditions"" WHERE ""Id"" = @Id", _secondary, transaction);
                transaction.Commit();
            }
            finally
            {
                transaction.Dispose();
            }

            var completed = await Task.WhenAny(insert, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(insert);
            (await insert).Should().BeOfType<InvalidOperationException>();
            history.All().Should().BeEmpty();
            downloads.All().Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void history_only_grab_blocks_target_deletion_with_known_or_unknown_client(bool knownClient)
        {
            Mocker.Resolve<HistoryRepository>().Insert(HistoryGrab(knownClient));

            Action remove = () => _editions.Delete(_secondary.Id);

            remove.Should().Throw<InvalidOperationException>();
            _editions.Find(_secondary.Id).Should().NotBeNull();
        }

        [Test]
        public void history_only_grab_requires_a_matching_later_terminal_before_deletion(
            [Values(DownloadHistoryEventType.DownloadImported, DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType eventType,
            [Values("movie", "target", "unassigned", "revision", "unknown-revision", "client", "download", "older", "equal-time", "matching")] string evidence)
        {
            var grab = Mocker.Resolve<HistoryRepository>().Insert(HistoryGrab());
            var terminal = new DownloadHistory
            {
                MovieId = _movie.Id, MovieEditionId = _secondary.Id, EditionRuleRevision = 1,
                DownloadClientId = 41, DownloadId = grab.DownloadId, SourceTitle = grab.SourceTitle,
                Date = grab.Date.AddSeconds(1), EventType = eventType
            };
            if (evidence == "movie") terminal.MovieId += 999;
            if (evidence == "target") terminal.MovieEditionId = _primary.Id;
            if (evidence == "unassigned") terminal.MovieEditionId = null;
            if (evidence == "revision") terminal.EditionRuleRevision = 2;
            if (evidence == "unknown-revision") terminal.EditionRuleRevision = null;
            if (evidence == "client") terminal.DownloadClientId++;
            if (evidence == "download") terminal.DownloadId = "synthetic-other-history";
            if (evidence == "older") terminal.Date = grab.Date.AddSeconds(-1);
            if (evidence == "equal-time") terminal.Date = grab.Date;
            Mocker.Resolve<DownloadHistoryRepository>().Insert(terminal);
            _secondary.MatchRules.Add("ChangedSyntheticGroup$");
            _editions.Update(_secondary);

            Action remove = () => _editions.Delete(_secondary.Id);
            if (evidence == "matching")
            {
                remove.Should().NotThrow();
                _editions.Find(_secondary.Id).Should().BeNull();
                Mocker.Resolve<HistoryRepository>().Get(grab.Id).EditionRuleRevision.Should().Be(1);
            }
            else
            {
                remove.Should().Throw<InvalidOperationException>();
                _editions.Find(_secondary.Id).Should().NotBeNull();
            }
        }
    }
}
