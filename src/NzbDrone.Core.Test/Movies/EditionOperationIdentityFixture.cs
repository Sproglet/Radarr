using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class EditionOperationIdentityFixture : DbTest
    {
        private const string OperationA = "00000000-0000-4000-8000-000000000041";
        private const string OperationB = "00000000-0000-4000-8000-000000000042";
        private Movie _movie;
        private MovieEdition _edition;
        private HistoryRepository _history;
        private DownloadHistoryRepository _downloads;
        private MovieEditionRepository _editions;

        [SetUp]
        public void Setup()
        {
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Profile", Items = new(), Language = NzbDrone.Core.Languages.Language.English });
            _movie = Mocker.Resolve<MovieRepository>().Insert(new Movie { Title = "Synthetic Voyage", Path = "/synthetic/voyage", QualityProfileId = profile.Id });
            _editions = Mocker.Resolve<MovieEditionRepository>();
            _edition = _editions.Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Directors 3D", EditionKey = "synthetic-directors-3d", QualityProfileId = profile.Id,
                Attributes = new() { { "cut", "directors" }, { "presentation", "3d" } }
            });
            _history = Mocker.Resolve<HistoryRepository>();
            _downloads = Mocker.Resolve<DownloadHistoryRepository>();
            Mocker.SetConstant<IHistoryRepository>(_history);
            Mocker.SetConstant<IDownloadHistoryRepository>(_downloads);
            Mocker.SetConstant<IHistoryService>(Mocker.Resolve<HistoryService>());
        }

        private MovieHistory MovieGrab(string operation = OperationA) => new()
        {
            MovieId = _movie.Id, MovieEditionId = _edition.Id, EditionRuleRevision = _edition.RuleRevision, EditionOperationId = operation,
            DownloadId = "synthetic-download", SourceTitle = "Synthetic.Voyage.2026.Directors.Cut.3D-SyntheticGroup",
            Date = DateTime.UtcNow, EventType = MovieHistoryEventType.Grabbed, Quality = new(), Languages = new(),
            Data = new() { { MovieHistory.DOWNLOAD_CLIENT_ID, "41" }, { "movieEditionName", _edition.Name } }
        };

        private DownloadHistory DownloadGrab(string operation = OperationA) => new()
        {
            MovieId = _movie.Id, MovieEditionId = _edition.Id, EditionRuleRevision = _edition.RuleRevision, EditionOperationId = operation,
            DownloadId = "synthetic-download", SourceTitle = "Synthetic.Voyage.2026.Directors.Cut.3D-SyntheticGroup",
            Date = DateTime.UtcNow, EventType = DownloadHistoryEventType.DownloadGrabbed, DownloadClientId = 41,
            Data = new() { { "movieEditionName", _edition.Name } }
        };

        [Test]
        public void both_handlers_persist_one_shared_operation_and_separate_grabs_have_distinct_operations()
        {
            var remote = new RemoteMovie
            {
                Movie = _movie, EditionContext = new MovieEditionContext(_edition, null, null, true),
                ParsedMovieInfo = Parser.Parser.ParseMovieTitle("Synthetic.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup"),
                Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026-SyntheticGroup", PublishDate = DateTime.UtcNow, Indexer = "Synthetic Indexer" }
            };
            var first = new MovieGrabbedEvent(remote) { DownloadId = "synthetic-first", DownloadClientId = 41 };
            var second = new MovieGrabbedEvent(remote) { DownloadId = "synthetic-second", DownloadClientId = 41 };
            foreach (var message in new[] { first, second })
            {
                Mocker.Resolve<HistoryService>().Handle(message);
                Mocker.Resolve<DownloadHistoryService>().Handle(message);
                Guid.ParseExact(message.EditionOperationId, "D").Should().NotBe(Guid.Empty);
                _history.FindByDownloadId(message.DownloadId).Single().EditionOperationId.Should().Be(message.EditionOperationId);
                _downloads.FindByDownloadId(41, message.DownloadId).Single().EditionOperationId.Should().Be(message.EditionOperationId);
            }

            first.EditionOperationId.Should().NotBe(second.EditionOperationId);
            remote.EditionContext = null;
            new MovieGrabbedEvent(remote).EditionOperationId.Should().BeNull();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void duplicate_grab_operation_is_rejected_without_creating_a_second_row(bool downloadHistory)
        {
            Action duplicate;
            if (downloadHistory)
            {
                _downloads.Insert(DownloadGrab());
                duplicate = () => _downloads.Insert(DownloadGrab());
            }
            else
            {
                _history.Insert(MovieGrab());
                duplicate = () => _history.Insert(MovieGrab());
            }

            duplicate.Should().Throw<System.Data.Common.DbException>();
            (downloadHistory ? _downloads.Count() : _history.Count()).Should().Be(1);
        }

        [Test]
        public void invalid_grab_operation_rolls_back_its_insert(
            [Values(false, true)] bool downloadHistory,
            [Values("", "not-a-guid", "00000000-0000-0000-0000-000000000000", "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", "00000000000040008000000000000041")] string operation)
        {
            Action insert = downloadHistory ? () => _downloads.Insert(DownloadGrab(operation)) : () => _history.Insert(MovieGrab(operation));

            insert.Should().Throw<InvalidOperationException>().WithMessage("*operation identity*");
            _downloads.Count().Should().Be(0);
            _history.Count().Should().Be(0);
        }

        [Test]
        public void recovery_requires_matching_valid_operation_identity(
            [Values("legacy", "matching", "different", "left-missing", "right-missing", "invalid")] string evidence)
        {
            var movieGrab = MovieGrab(evidence is "legacy" or "left-missing" ? null : OperationA);
            var downloadGrab = DownloadGrab(evidence switch { "legacy" or "right-missing" => null, "different" => OperationB, "invalid" => "not-a-guid", _ => OperationA });
            var recovery = new MovieEditionRecoveryService(Mocker.GetMock<IMovieEditionContextService>().Object, _editions);
            Action recover = () => recovery.Recover(_movie, movieGrab, downloadGrab);

            if (evidence is "legacy" or "matching")
            {
                recover.Should().NotThrow();
            }
            else
            {
                recover.Should().Throw<InvalidOperationException>().WithMessage("*operation identity*");
                Mocker.GetMock<IMovieEditionContextService>().Verify(s => s.Resolve(_movie, _edition.Id, null), Moq.Times.Never());
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void operation_identity_cannot_be_saved_without_an_edition_assignment(bool downloadHistory)
        {
            var movieGrab = MovieGrab();
            var downloadGrab = DownloadGrab();
            movieGrab.MovieEditionId = null;
            downloadGrab.MovieEditionId = null;
            Action insert = downloadHistory ? () => _downloads.Insert(downloadGrab) : () => _history.Insert(movieGrab);

            insert.Should().Throw<InvalidOperationException>().WithMessage("*operation identity*");
            _history.Count().Should().Be(0);
            _downloads.Count().Should().Be(0);
        }

        [Test]
        public void only_the_same_operation_can_settle_a_grab_for_target_deletion(
            [Values(false, true)] bool historyOnly,
            [Values(DownloadHistoryEventType.DownloadImported, DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType eventType,
            [Values("matching", "different", "missing")] string evidence)
        {
            var terminal = DownloadGrab();
            if (historyOnly)
            {
                var grab = _history.Insert(MovieGrab());
                terminal.Date = grab.Date;
            }
            else
            {
                _downloads.Insert(terminal);
                terminal.Id = 0;
            }

            terminal.Date = terminal.Date.AddSeconds(1);
            terminal.EventType = eventType;
            terminal.EditionOperationId = evidence == "matching" ? OperationA : evidence == "different" ? OperationB : null;
            _downloads.Insert(terminal);
            Action remove = () => _editions.Delete(_edition.Id);

            if (evidence == "matching")
            {
                remove.Should().NotThrow();
                _editions.Find(_edition.Id).Should().BeNull();
            }
            else
            {
                remove.Should().Throw<InvalidOperationException>();
                _editions.Find(_edition.Id).Should().NotBeNull();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void completion_keeps_the_saved_operation_and_revision_after_rule_edits(bool historyOnly)
        {
            var grab = _history.Insert(MovieGrab());
            if (!historyOnly)
            {
                _downloads.Insert(DownloadGrab());
            }

            var savedName = _edition.Name;
            _edition.MatchRules.Add("ChangedSyntheticGroup$");
            _edition.Name = "Changed synthetic edition";
            _editions.Update(_edition);
            var tracked = new TrackedDownload
            {
                DownloadClient = 41,
                DownloadItem = new DownloadClientItem { DownloadId = grab.DownloadId, Title = grab.SourceTitle, DownloadClientInfo = new DownloadClientItemClientInfo { Id = 41, Name = "Synthetic Client", Type = "Synthetic Client" } },
                RemoteMovie = new RemoteMovie { Movie = _movie, EditionContext = new MovieEditionContext(_edition, null, null, true) }
            };

            Mocker.Resolve<DownloadHistoryService>().Handle(new DownloadCompletedEvent(tracked, _movie.Id));

            var terminal = _downloads.All().Single(h => h.EventType == DownloadHistoryEventType.DownloadImported);
            terminal.EditionOperationId.Should().Be(OperationA);
            terminal.EditionRuleRevision.Should().Be(grab.EditionRuleRevision);
            terminal.Data["movieEditionName"].Should().Be(savedName);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_malformed_restored_operation_cannot_release_target_deletion(bool historyOnly)
        {
            var terminal = DownloadGrab();
            if (historyOnly)
            {
                _history.Insert(MovieGrab());
            }
            else
            {
                _downloads.Insert(terminal);
                terminal.Id = 0;
            }

            using (var connection = Db.OpenConnection())
            {
                var table = historyOnly ? "History" : "DownloadHistory";
                connection.Execute($@"UPDATE ""{table}"" SET ""EditionOperationId"" = 'malformed-operation'");
            }

            terminal.EditionOperationId = "malformed-operation";
            terminal.EventType = DownloadHistoryEventType.DownloadImported;
            terminal.Date = DateTime.UtcNow.AddSeconds(1);
            _downloads.Insert(terminal);
            Action remove = () => _editions.Delete(_edition.Id);

            remove.Should().Throw<InvalidOperationException>();
            _editions.Find(_edition.Id).Should().NotBeNull();
        }
    }
}
