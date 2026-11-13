using System;
using System.Collections.Generic;
using System.Data;
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
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionSubmissionJournalFixture
    {
        [Test]
        public void grab_repair_waits_for_the_parent_without_holding_the_journal_row()
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            using var connection = Db.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @Id", _movie, transaction);
            using var started = new ManualResetEventSlim();
            var repair = Task.Run(() => { started.Set(); return _journal.RecordGrabs(intent.OperationId); });
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            DbException journalLockFailure = null;
            bool parentWaitObserved;
            if (Db.DatabaseType == DatabaseType.PostgreSQL)
            {
                using var probe = Db.OpenConnection();
                parentWaitObserved = SpinWait.SpinUntil(() =>
                {
                    var waiting = probe.ExecuteScalar<int>(@"SELECT COUNT(*) FROM pg_stat_activity
                        WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE @Query",
                        new { Query = "%UPDATE \"Movies\"%" }) > 0;
                    if (!waiting) Thread.Sleep(20);
                    return waiting;
                }, TimeSpan.FromSeconds(5));
                if (parentWaitObserved)
                {
                    try
                    {
                        using var probeTransaction = probe.BeginTransaction(IsolationLevel.ReadCommitted);
                        probe.ExecuteScalar<int>(@"SELECT ""Id"" FROM ""MovieEditionSubmissions"" WHERE ""OperationId"" = @OperationId FOR UPDATE NOWAIT", intent, probeTransaction);
                        probeTransaction.Rollback();
                    }
                    catch (DbException exception)
                    {
                        journalLockFailure = exception;
                    }
                }
            }
            else
            {
                // SQLite serializes the write transactions before either row-level lock.
                parentWaitObserved = !repair.Wait(TimeSpan.FromMilliseconds(200));
            }

            transaction.Commit();
            repair.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            parentWaitObserved.Should().BeTrue();
            journalLockFailure.Should().BeNull("a repair waiting for the parent must leave the journal available to the parent owner");
            repair.Result.State.Should().Be(MovieEditionSubmissionState.Recorded);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void mixed_history_batch_and_grab_repair_finish_without_a_lock_cycle(int attempt)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            var primary = Mocker.Resolve<MovieEditionRepository>().Get(_movie.PrimaryEditionId.Value);
            var batch = new List<DownloadHistory>
            {
                new()
                {
                    MovieId = _movie.Id, MovieEditionId = primary.Id, EditionRuleRevision = primary.RuleRevision,
                    EditionOperationId = Guid.NewGuid().ToString("D"), DownloadClientId = 41, DownloadId = "synthetic-other-" + attempt,
                    SourceTitle = "Synthetic.Voyage.2026.Theatrical.1080p.BluRay-SyntheticGroup", Date = DateTime.UtcNow,
                    EventType = DownloadHistoryEventType.DownloadGrabbed, Data = new() { { "movieEditionName", primary.Name } }
                },
                Terminal(intent, DownloadHistoryEventType.DownloadImported)
            };
            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            using var start = new ManualResetEventSlim();
            var repair = Task.Run(() => { start.Wait(); _journal.RecordGrabs(intent.OperationId); });
            var insert = Task.Run(() => { start.Wait(); downloads.InsertMany(batch); });
            start.Set();
            Task.WaitAll(repair, insert);

            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Settled);
            downloads.Count().Should().Be(3);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(1);
            downloads.All().Single(row => row.MovieEditionId == primary.Id).EditionOperationId.Should().Be(batch[0].EditionOperationId);
            Mocker.Resolve<MovieRepository>().Get(_movie.Id).PrimaryEditionId.Should().Be(primary.Id);
        }

        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public void batches_with_opposite_movie_order_keep_their_input_order_without_a_lock_cycle(bool movieHistory, int attempt)
        {
            var metadata = Db.Insert(new MovieMetadata { Title = "Synthetic Solar Voyage", TmdbId = 80808081, Year = 2026 });
            var other = Mocker.Resolve<MovieRepository>().Insert(new Movie
            {
                MovieMetadataId = metadata.Id, QualityProfileId = _movie.QualityProfileId, Path = "/synthetic/solar-voyage", Monitored = true
            });
            var primary = Mocker.Resolve<MovieEditionRepository>().Get(_movie.PrimaryEditionId.Value);
            var otherPrimary = Mocker.Resolve<MovieEditionRepository>().Get(other.PrimaryEditionId.Value);
            DownloadHistory Grab(Movie movie, MovieEdition edition) => new()
            {
                MovieId = movie.Id, MovieEditionId = edition.Id, EditionRuleRevision = edition.RuleRevision,
                EditionOperationId = Guid.NewGuid().ToString("D"), DownloadClientId = 41, DownloadId = "synthetic-batch-" + attempt,
                SourceTitle = "Synthetic.Solar.Voyage.2026.Theatrical.1080p.BluRay-SyntheticGroup", Date = DateTime.UtcNow,
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                Data = new() { { "movieEditionName", edition.Name }, { MovieHistory.DOWNLOAD_CLIENT_ID, "41" } }
            };

            var downloads = Mocker.Resolve<DownloadHistoryRepository>();
            var history = Mocker.Resolve<HistoryRepository>();
            List<int> Insert(List<DownloadHistory> rows)
            {
                if (!movieHistory)
                {
                    downloads.InsertMany(rows);
                    return rows.Select(row => row.Id).ToList();
                }

                var records = rows.Select(row => new MovieHistory
                {
                    MovieId = row.MovieId, MovieEditionId = row.MovieEditionId, EditionRuleRevision = row.EditionRuleRevision,
                    EditionOperationId = row.EditionOperationId, DownloadId = row.DownloadId, SourceTitle = row.SourceTitle,
                    Date = row.Date, EventType = MovieHistoryEventType.Grabbed, Data = row.Data, Quality = new(), Languages = new()
                }).ToList();
                history.InsertMany(records);
                return records.Select(row => row.Id).ToList();
            }

            using var start = new ManualResetEventSlim();
            var first = Task.Run(() => { start.Wait(); return Insert(new() { Grab(other, otherPrimary), Grab(_movie, primary) }); });
            var second = Task.Run(() => { start.Wait(); return Insert(new() { Grab(_movie, primary), Grab(other, otherPrimary) }); });
            start.Set();
            Task.WaitAll(first, second);

            first.Result.Should().HaveCount(2).And.BeInAscendingOrder();
            second.Result.Should().HaveCount(2).And.BeInAscendingOrder();
            (movieHistory ? history.Count() : downloads.Count()).Should().Be(4);
        }
    }
}
