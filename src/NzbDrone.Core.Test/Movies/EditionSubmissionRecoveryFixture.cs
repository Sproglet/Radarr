using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Movies;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionSubmissionJournalFixture
    {
        private ReconcileMovieEditionSubmissions Recovery() => new(_journal, Coordinator(), TestLogger);

        private MovieEditionSubmission RecoveryIntent(string suffix)
        {
            var edition = Mocker.Resolve<MovieEditionRepository>().Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Recovery " + suffix, EditionKey = "synthetic-recovery-" + suffix,
                QualityProfileId = _movie.QualityProfileId, Monitored = true,
                Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" }, { "custom-label", suffix } }
            });
            var request = Intent();
            request.MovieEditionId = edition.Id;
            request.EditionName = edition.Name;
            request.EditionAttributes = new(edition.Attributes);
            return _journal.Prepare(request);
        }

        [Test]
        public void startup_recovers_only_known_acceptance_and_healthy_replays_preserve_progress_timestamps()
        {
            var prepared = _journal.Prepare(Intent());
            var known = RecoveryIntent("known");
            _journal.Accept(known.OperationId, "synthetic-known");
            prepared = _journal.Get(prepared.Id);
            var recovery = Recovery();

            recovery.HandleAsync(new ApplicationStartedEvent());

            _journal.Get(prepared.Id).Should().BeEquivalentTo(prepared, o => o
                .Excluding(row => row.Release.Age).Excluding(row => row.Release.AgeHours).Excluding(row => row.Release.AgeMinutes));
            var recorded = _journal.Get(known.Id);
            recorded.State.Should().Be(MovieEditionSubmissionState.Recorded);
            _journal.ReconciliationCandidates(0, _journal.ReconciliationUpperBound(), 100).Should().BeEmpty();
            var history = Mocker.Resolve<HistoryRepository>().All().Single();
            var download = Mocker.Resolve<DownloadHistoryRepository>().All().Single();
            recovery.Clean();
            Coordinator().Reconcile(known.OperationId);

            _journal.Get(known.Id).Should().BeEquivalentTo(recorded, o => o
                .Excluding(row => row.Release.Age).Excluding(row => row.Release.AgeHours).Excluding(row => row.Release.AgeMinutes));
            Mocker.Resolve<HistoryRepository>().All().Single().Should().BeEquivalentTo(history);
            Mocker.Resolve<DownloadHistoryRepository>().All().Single().Should().BeEquivalentTo(download, o => o
                .Excluding(row => row.Release.Age).Excluding(row => row.Release.AgeHours).Excluding(row => row.Release.AgeMinutes));
        }

        [TestCase("History")]
        [TestCase("DownloadHistory")]
        public void recovery_repairs_a_missing_grab_stream_without_replacing_the_other(string missing)
        {
            var intent = _journal.Prepare(Intent());
            _journal.Accept(intent.OperationId, "synthetic-known");
            _journal.RecordGrabs(intent.OperationId);
            var movieGrab = Mocker.Resolve<HistoryRepository>().All().Single();
            var downloadGrab = Mocker.Resolve<DownloadHistoryRepository>().All().Single();
            using (var connection = Db.OpenConnection())
            {
                connection.Execute($@"DELETE FROM ""{missing}"" WHERE ""EditionOperationId"" = @OperationId", intent);
            }

            Recovery().Clean();

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            var movie = Mocker.Resolve<HistoryRepository>().All().Single();
            var download = Mocker.Resolve<DownloadHistoryRepository>().All().Single();
            movie.Should().BeEquivalentTo(movieGrab, o => o.Excluding(row => row.Id));
            download.Should().BeEquivalentTo(downloadGrab, o => o.Excluding(row => row.Id)
                .Excluding(row => row.Release.Age).Excluding(row => row.Release.AgeHours).Excluding(row => row.Release.AgeMinutes));
            if (missing == "History") download.Id.Should().Be(downloadGrab.Id);
            else movie.Id.Should().Be(movieGrab.Id);
        }

        [TestCase(DownloadHistoryEventType.DownloadImported, true)]
        [TestCase(DownloadHistoryEventType.DownloadFailed, true)]
        [TestCase(DownloadHistoryEventType.DownloadIgnored, true)]
        [TestCase(DownloadHistoryEventType.FileImported, false)]
        public void recovery_selects_only_actual_terminal_events_for_recorded_work(DownloadHistoryEventType eventType, bool terminal)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-known");
            _journal.RecordGrabs(intent.OperationId);
            Db.Insert(Terminal(intent, eventType));
            var candidates = _journal.ReconciliationCandidates(0, intent.Id, 100);
            candidates.Should().HaveCount(terminal ? 1 : 0);
            if (terminal) candidates.Single().HasTerminalEvidence.Should().BeTrue();

            Recovery().Clean();

            _journal.Get(intent.Id).State.Should().Be(terminal ? MovieEditionSubmissionState.Settled : MovieEditionSubmissionState.Recorded);
        }

        [TestCase("missing-payload")]
        [TestCase("invalid-json")]
        [TestCase("conflicting-grab")]
        public void one_bad_operation_remains_for_review_and_does_not_block_later_acceptance(string damage)
        {
            var bad = _journal.Prepare(Intent());
            _journal.Accept(bad.OperationId, "synthetic-bad");
            if (damage == "conflicting-grab")
            {
                _journal.RecordGrabs(bad.OperationId);
                var accepted = _journal.Get(bad.Id);
                Db.Insert(Terminal(accepted, DownloadHistoryEventType.DownloadImported));
                using var connection = Db.OpenConnection();
                connection.Execute(@"UPDATE ""DownloadHistory"" SET ""SourceTitle"" = 'Synthetic.Conflicting.Voyage.2026-SyntheticGroup' WHERE ""EditionOperationId"" = @OperationId", bad);
            }
            else
            {
                using var connection = Db.OpenConnection();
                connection.Execute(damage == "invalid-json" ?
                    @"UPDATE ""MovieEditionSubmissions"" SET ""Release"" = 'invalid-synthetic-json' WHERE ""Id"" = @Id" :
                    @"UPDATE ""MovieEditionSubmissions"" SET ""HistoryData"" = NULL WHERE ""Id"" = @Id", bad);
            }

            var good = RecoveryIntent("later");
            _journal.Accept(good.OperationId, "synthetic-good");

            Recovery().Clean();

            using (var connection = Db.OpenConnection())
            {
                connection.ExecuteScalar<int>(@"SELECT ""State"" FROM ""MovieEditionSubmissions"" WHERE ""Id"" = @Id", bad)
                    .Should().Be(damage == "conflicting-grab" ? 2 : 1);
            }

            _journal.Get(good.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void contradictory_terminal_evidence_is_not_marked_settled()
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-known");
            _journal.RecordGrabs(intent.OperationId);
            var terminal = Terminal(intent, DownloadHistoryEventType.DownloadImported);
            terminal.Data.Clear();
            Db.Insert(terminal);

            Recovery().Clean();

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void recovery_write_failure_keeps_acceptance_and_can_repair_on_the_next_pass()
        {
            var intent = _journal.Prepare(Intent());
            _journal.Accept(intent.OperationId, "synthetic-known");
            RejectDownloadGrab(true);
            var recovery = Recovery();
            recovery.Clean();
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Accepted);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(0);
            Mocker.Resolve<DownloadHistoryRepository>().Count().Should().Be(0);
            RejectDownloadGrab(false);

            recovery.Clean();

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void concurrent_recovery_instances_keep_one_grab_pair_and_the_saved_operation()
        {
            var intent = _journal.Prepare(Intent());
            _journal.Accept(intent.OperationId, "synthetic-known");
            var first = Recovery();
            var second = Recovery();
            using var start = new ManualResetEventSlim();
            var tasks = new[] { first, second }.Select(recovery => Task.Run(() => { start.Wait(); recovery.Clean(); })).ToArray();
            start.Set();
            Task.WaitAll(tasks);

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(1);
            Mocker.Resolve<DownloadHistoryRepository>().Count().Should().Be(1);
        }

        [Test]
        public void reconciliation_pages_have_a_finite_snapshot_and_do_not_deserialize_bad_payloads()
        {
            _journal.ReconciliationUpperBound().Should().Be(0);
            var prepared = _journal.Prepare(Intent());
            var rows = Enumerable.Range(1, 105).Select(i =>
            {
                var edition = Db.Insert(new MovieEdition
                {
                    MovieId = _movie.Id, EditionKey = "synthetic-page-" + i, Name = "Synthetic Page " + i,
                    QualityProfileId = _movie.QualityProfileId
                });
                return ReviewRow(MovieEditionSubmissionState.Accepted, prepared.CreatedAt, edition.Id);
            }).ToList();
            var throughId = _journal.ReconciliationUpperBound();
            var late = RecoveryIntent("late");
            _journal.Accept(late.OperationId, "synthetic-late");
            using (var connection = Db.OpenConnection())
            {
                connection.Execute(@"UPDATE ""MovieEditionSubmissions"" SET ""Release"" = 'invalid-synthetic-json' WHERE ""Id"" = @Id", rows[0]);
            }

            var first = _journal.ReconciliationCandidates(0, throughId, 100);
            var last = _journal.ReconciliationCandidates(first[^1].Id, throughId, 100);

            first.Should().HaveCount(100);
            first.Concat(last).Select(row => row.Id).Should().Equal(rows.Select(row => row.Id));
            _journal.ReconciliationCandidates(throughId, _journal.ReconciliationUpperBound(), 100).Should().ContainSingle().Which.Id.Should().Be(late.Id);
            _journal.Get(prepared.Id).State.Should().Be(MovieEditionSubmissionState.Prepared);
        }

        [TestCase(-1, 1, 1)]
        [TestCase(2, 1, 1)]
        [TestCase(0, 1, 0)]
        [TestCase(0, 1, 101)]
        public void reconciliation_query_rejects_unbounded_or_invalid_ranges(int after, int through, int size)
        {
            Action query = () => _journal.ReconciliationCandidates(after, through, size);
            query.Should().Throw<ArgumentException>();
        }
    }
}
