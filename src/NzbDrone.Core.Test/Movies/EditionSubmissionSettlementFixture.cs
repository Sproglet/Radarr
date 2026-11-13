using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionSubmissionJournalFixture
    {
        private static DownloadHistory Terminal(MovieEditionSubmission intent, DownloadHistoryEventType eventType) => new()
        {
            MovieId = intent.MovieId, MovieEditionId = intent.MovieEditionId, EditionRuleRevision = intent.EditionRuleRevision,
            EditionOperationId = intent.OperationId, DownloadClientId = intent.DownloadClientId, DownloadId = intent.DownloadId,
            SourceTitle = "Synthetic.Client.Renamed.Voyage.2026-SyntheticGroup", Date = DateTime.UtcNow.AddSeconds(1), EventType = eventType,
            Data = new() { { "movieEditionName", intent.EditionName } }
        };

        [Test]
        public void terminal_and_grab_repair_settle_in_either_order(
            [Values(false, true)] bool terminalFirst,
            [Values(DownloadHistoryEventType.DownloadImported, DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType eventType)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            var terminal = Terminal(intent, eventType);
            if (terminalFirst)
            {
                downloads.Insert(terminal);
                _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Accepted);
                _journal.RecordGrabs(intent.OperationId).State.Should().Be(MovieEditionSubmissionState.Settled);
            }
            else
            {
                _journal.RecordGrabs(intent.OperationId);
                downloads.Insert(terminal);
            }

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
            Coordinator().Reconcile(intent.OperationId).State.Should().Be(MovieEditionSubmissionState.Settled);
            _journal.HasUnsettled(intent.MovieId, intent.MovieEditionId).Should().BeFalse();
            downloads.All().Single(h => h.EventType == eventType).Id.Should().Be(terminal.Id);
            Mocker.Resolve<MovieEditionRepository>().Delete(_edition.Id);
            _journal.Get(intent.Id).EditionName.Should().Be(intent.EditionName);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void settlement_failure_rolls_back_the_new_event_or_both_new_grabs(bool terminalFirst)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            if (terminalFirst) downloads.Insert(Terminal(intent, DownloadHistoryEventType.DownloadImported));
            else _journal.RecordGrabs(intent.OperationId);
            using (var connection = Db.OpenConnection())
            {
                connection.Execute(Db.DatabaseType == DatabaseType.PostgreSQL ?
                    @"ALTER TABLE ""MovieEditionSubmissions"" ADD CONSTRAINT synthetic_reject_settlement CHECK (""State"" <> 3)" :
                    @"CREATE TRIGGER synthetic_reject_settlement BEFORE UPDATE ON ""MovieEditionSubmissions"" WHEN NEW.""State"" = 3 BEGIN SELECT RAISE(ABORT, 'synthetic settlement failure'); END");
            }

            Action persist = () =>
            {
                if (terminalFirst) _journal.RecordGrabs(intent.OperationId);
                else downloads.Insert(Terminal(intent, DownloadHistoryEventType.DownloadImported));
            };
            persist.Should().Throw<DbException>();

            _journal.Get(intent.Id).State.Should().Be(terminalFirst ? MovieEditionSubmissionState.Accepted : MovieEditionSubmissionState.Recorded);
            downloads.All().Count(h => h.EventType == DownloadHistoryEventType.DownloadImported).Should().Be(terminalFirst ? 1 : 0);
            downloads.All().Count(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed).Should().Be(terminalFirst ? 0 : 1);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(terminalFirst ? 0 : 1);
            using (var connection = Db.OpenConnection())
            {
                connection.Execute(Db.DatabaseType == DatabaseType.PostgreSQL ?
                    @"ALTER TABLE ""MovieEditionSubmissions"" DROP CONSTRAINT synthetic_reject_settlement" : "DROP TRIGGER synthetic_reject_settlement");
            }

            persist.Should().NotThrow();
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
        }

        [Test]
        public void conflicting_terminal_evidence_is_retained_and_blocks_repair_settlement()
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            var wrong = Terminal(intent, DownloadHistoryEventType.DownloadIgnored);
            wrong.Data.Clear();
            downloads.Insert(wrong);
            downloads.Insert(Terminal(intent, DownloadHistoryEventType.DownloadImported));

            _journal.RecordGrabs(intent.OperationId).State.Should().Be(MovieEditionSubmissionState.Recorded);
            Action settle = () => _journal.Settle(intent.OperationId);
            settle.Should().Throw<InvalidOperationException>();
            downloads.Count().Should().Be(3);
            Action remove = () => Mocker.Resolve<MovieEditionRepository>().Delete(_edition.Id);
            remove.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void terminal_evidence_cannot_infer_acceptance_for_a_prepared_unknown_outcome()
        {
            var intent = _journal.Prepare(Intent());
            var terminal = Terminal(intent, DownloadHistoryEventType.DownloadImported);
            terminal.DownloadId = "synthetic-download";
            Mocker.Resolve<DownloadHistoryRepository>().Insert(terminal);

            var saved = _journal.Get(intent.Id);
            saved.State.Should().Be(MovieEditionSubmissionState.Prepared);
            saved.DownloadId.Should().BeNull();
            Action repair = () => _journal.RecordGrabs(intent.OperationId);
            repair.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void missing_or_conflicting_grab_evidence_keeps_terminal_unsettled(
            [Values("History", "DownloadHistory")] string table,
            [Values(false, true)] bool missing)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            _journal.RecordGrabs(intent.OperationId);
            using (var connection = Db.OpenConnection())
            {
                connection.Execute(missing ? $@"DELETE FROM ""{table}"" WHERE ""EditionOperationId"" = @OperationId" :
                    $@"UPDATE ""{table}"" SET ""SourceTitle"" = 'Synthetic.Conflicting.Voyage.2026-SyntheticGroup' WHERE ""EditionOperationId"" = @OperationId", intent);
            }

            Mocker.Resolve<DownloadHistoryRepository>().Insert(Terminal(intent, DownloadHistoryEventType.DownloadImported));

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            _journal.HasUnsettled(intent.MovieId, intent.MovieEditionId).Should().BeTrue();
        }

        [Test]
        public void terminal_settlement_does_not_release_another_editions_intent()
        {
            var first = _journal.Prepare(Intent());
            first = _journal.Accept(first.OperationId, "synthetic-download");
            _journal.RecordGrabs(first.OperationId);
            var primary = Mocker.Resolve<MovieEditionRepository>().Get(_movie.PrimaryEditionId.Value);
            var request = Intent();
            request.MovieEditionId = primary.Id;
            request.EditionName = primary.Name;
            request.EditionAttributes = new(primary.Attributes);
            request.EditionRuleRevision = primary.RuleRevision;
            var second = _journal.Prepare(request);

            Mocker.Resolve<DownloadHistoryRepository>().Insert(Terminal(first, DownloadHistoryEventType.DownloadImported));

            _journal.Get(first.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
            _journal.Get(second.Id).State.Should().Be(MovieEditionSubmissionState.Prepared);
            _journal.HasUnsettled(_movie.Id).Should().BeTrue();
            Mocker.Resolve<MovieRepository>().Get(_movie.Id).PrimaryEditionId.Should().Be(primary.Id);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void terminal_and_grab_repair_serialize_without_losing_settlement(int attempt)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download-" + attempt);
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            using var start = new ManualResetEventSlim();
            var repair = Task.Run(() => { start.Wait(); _journal.RecordGrabs(intent.OperationId); });
            var terminal = Task.Run(() => { start.Wait(); downloads.Insert(Terminal(intent, DownloadHistoryEventType.DownloadImported)); });
            start.Set();
            Task.WaitAll(repair, terminal);

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
            downloads.Count().Should().Be(2);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(1);
        }

        [Test]
        public void terminal_transaction_locks_accepted_intent_until_its_evidence_commits()
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            using var started = new ManualResetEventSlim();
            using var connection = Db.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            downloads.Insert(connection, transaction, Terminal(intent, DownloadHistoryEventType.DownloadImported));
            var repair = Task.Run(() => { started.Set(); return _journal.RecordGrabs(intent.OperationId); });
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var completedBeforeCommit = repair.Wait(TimeSpan.FromMilliseconds(200));
            transaction.Commit();

            repair.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            completedBeforeCommit.Should().BeFalse();
            repair.Result.State.Should().Be(MovieEditionSubmissionState.Settled);
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
        }

        [TestCase(DownloadHistoryEventType.DownloadImported)]
        [TestCase(DownloadHistoryEventType.DownloadFailed)]
        [TestCase(DownloadHistoryEventType.DownloadIgnored)]
        public void real_terminal_handlers_settle_the_saved_operation(DownloadHistoryEventType eventType)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            _journal.RecordGrabs(intent.OperationId);
            var client = new DownloadClientItemClientInfo { Id = intent.DownloadClientId, Name = "Synthetic Client", Type = "Synthetic Client Implementation" };
            var tracked = new TrackedDownload
            {
                DownloadClient = client.Id, RemoteMovie = SubmissionRelease(),
                DownloadItem = new DownloadClientItem { DownloadId = intent.DownloadId, DownloadClientInfo = client, Title = "Synthetic.Renamed.Voyage.2026-SyntheticGroup" }
            };
            var service = new DownloadHistoryService(Mocker.Resolve<DownloadHistoryRepository>(), Mocker.GetMock<IHistoryService>().Object);
            if (eventType == DownloadHistoryEventType.DownloadImported) service.Handle(new DownloadCompletedEvent(tracked, intent.MovieId));
            else if (eventType == DownloadHistoryEventType.DownloadFailed) service.Handle(new DownloadFailedEvent
            {
                MovieId = intent.MovieId, DownloadId = intent.DownloadId, DownloadClientId = client.Id, TrackedDownload = tracked,
                SourceTitle = tracked.DownloadItem.Title
            });
            else service.Handle(new DownloadIgnoredEvent
            {
                MovieId = intent.MovieId, DownloadId = intent.DownloadId, DownloadClientInfo = client, TrackedDownload = tracked,
                SourceTitle = tracked.DownloadItem.Title
            });

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
            Mocker.Resolve<DownloadHistoryRepository>().All().Single(h => h.EventType == eventType).EditionOperationId.Should().Be(intent.OperationId);
        }
    }
}
