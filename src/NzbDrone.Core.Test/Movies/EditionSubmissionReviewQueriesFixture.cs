using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionSubmissionJournalFixture
    {
        private MovieEditionSubmission ReviewRow(MovieEditionSubmissionState state, DateTime createdAt, int? editionId = null)
        {
            var row = Intent();
            row.OperationId = Guid.NewGuid().ToString("D");
            row.State = state;
            row.CreatedAt = createdAt;
            row.UpdatedAt = createdAt;
            row.MovieEditionId = editionId ?? row.MovieEditionId;

            // Read-query fixtures represent historical rows, without invoking lifecycle transitions.
            return Db.Insert(row);
        }

        [TestCase(MovieEditionSubmissionState.Prepared)]
        [TestCase(MovieEditionSubmissionState.Accepted)]
        [TestCase(MovieEditionSubmissionState.Recorded)]
        public void review_keeps_older_unresolved_work_ahead_of_bounded_finished_history(MovieEditionSubmissionState state)
        {
            var intent = _journal.Prepare(Intent());
            if (state >= MovieEditionSubmissionState.Accepted) _journal.Accept(intent.OperationId, "synthetic-download");
            if (state == MovieEditionSubmissionState.Recorded) _journal.RecordGrabs(intent.OperationId);
            var finished = Enumerable.Range(1, 105).Select(i => ReviewRow(MovieEditionSubmissionState.Settled, intent.CreatedAt.AddMinutes(i))).ToList();
            var foreign = ReviewRow(MovieEditionSubmissionState.Settled, intent.CreatedAt.AddDays(1));
            using (var connection = Db.OpenConnection())
            {
                connection.Execute(@"UPDATE ""MovieEditionSubmissions"" SET ""MovieId"" = @MovieId WHERE ""Id"" = @Id",
                    new { foreign.Id, MovieId = _movie.Id + 999 });
            }

            var review = _journal.RecentForMovie(_movie.Id);

            review.Should().HaveCount(101);
            review[0].OperationId.Should().Be(intent.OperationId);
            review[0].State.Should().Be(state);
            review.Skip(1).Select(row => row.Id).Should().Equal(finished.OrderByDescending(row => row.CreatedAt).Take(100).Select(row => row.Id));
            review.Should().OnlyContain(row => row.MovieId == _movie.Id);
        }

        [Test]
        public void review_does_not_truncate_unresolved_work_at_the_finished_history_limit()
        {
            var oldest = _journal.Prepare(Intent());
            for (var i = 1; i <= 105; i++)
            {
                var edition = Mocker.Resolve<MovieEditionRepository>().Insert(new MovieEdition
                {
                    MovieId = _movie.Id, Name = "Synthetic Review Target " + i, EditionKey = "synthetic-review-" + i,
                    QualityProfileId = _movie.QualityProfileId, Monitored = true
                });
                ReviewRow(MovieEditionSubmissionState.Prepared, oldest.CreatedAt.AddMinutes(i), edition.Id);
            }

            var review = _journal.RecentForMovie(_movie.Id);

            review.Should().HaveCount(106);
            review.Should().OnlyContain(row => row.State == MovieEditionSubmissionState.Prepared);
            review[^1].OperationId.Should().Be(oldest.OperationId);
            review.Select(row => row.Id).Should().Equal(review.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).Select(row => row.Id));
        }
    }
}
