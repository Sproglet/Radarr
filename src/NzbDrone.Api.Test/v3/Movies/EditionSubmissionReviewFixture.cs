using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using Radarr.Api.V3.MovieEditions;
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Movies
{
    public class EditionSubmissionReviewFixture
    {
        private const string Operation = "00000000-0000-4000-8000-000000000041";
        private Mock<IMovieRepository> _movies;
        private Mock<IMovieEditionSubmissionRepository> _journal;
        private Mock<IMovieEditionSubmissionService> _submissions;
        private MovieEditionSubmission _intent;
        private MovieEditionSubmissionController _controller;

        [SetUp]
        public void Setup()
        {
            _movies = new();
            _journal = new();
            _submissions = new();
            _intent = new MovieEditionSubmission
            {
                OperationId = Operation, MovieId = 1, MovieEditionId = 2, EditionRuleRevision = 3, EditionName = "Synthetic Directors 3D",
                Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026.Directors.Cut.3D-SyntheticGroup", DownloadUrl = "http://127.0.0.1/synthetic?apikey=synthetic-secret" },
                DownloadId = "synthetic-download", DownloadClientId = 41, DownloadClientName = "Synthetic Client", MoviePath = "/synthetic/private/path",
                State = MovieEditionSubmissionState.Accepted, HistoryData = new(), DownloadHistoryData = new(), Languages = new()
            };
            _movies.Setup(m => m.Find(1)).Returns(new Movie { Id = 1 });
            _journal.Setup(j => j.FindByOperationId(Operation)).Returns(() => _intent);
            _journal.Setup(j => j.RecentForMovie(1)).Returns(() => new List<MovieEditionSubmission> { _intent });
            _controller = new(_movies.Object, _journal.Object, _submissions.Object);
        }

        private static MovieEditionSubmissionReconcileResource Request() => new()
        {
            MovieId = 1, MovieEditionId = 2, ExpectedState = MovieEditionSubmissionState.Accepted, ExpectedDownloadId = "synthetic-download"
        };

        [Test]
        public void list_exposes_only_review_metadata_and_the_saved_edition_name()
        {
            var resource = _controller.GetForMovie(1).Value.Should().ContainSingle().Which;
            resource.EditionName.Should().Be("Synthetic Directors 3D");
            resource.OperationId.Should().Be(Operation);
            resource.CanReconcile.Should().BeTrue();
            var json = JsonSerializer.Serialize(resource);
            json.Should().NotContain("synthetic-secret").And.NotContain("DownloadUrl").And.NotContain("HistoryData").And.NotContain("/synthetic/private/path");
            _submissions.VerifyNoOtherCalls();
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void list_requires_a_positive_movie(int movieId)
        {
            Action get = () => _controller.GetForMovie(movieId);
            get.Should().Throw<BadRequestException>();
        }

        [Test]
        public void unknown_movie_returns_not_found()
        {
            Action get = () => _controller.GetForMovie(999);
            get.Should().Throw<NotFoundException>();
        }

        [TestCase(MovieEditionSubmissionState.Prepared)]
        [TestCase(MovieEditionSubmissionState.Settled)]
        public void unresolved_outcome_or_settled_work_cannot_be_reconciled(MovieEditionSubmissionState state)
        {
            _intent.State = state;
            _controller.GetForMovie(1).Value[0].CanReconcile.Should().BeFalse();
            _controller.Reconcile(Operation, Request()).Result.Should().BeOfType<Conflict<object>>();
            _submissions.VerifyNoOtherCalls();
        }

        [TestCase("movie")]
        [TestCase("edition")]
        [TestCase("state")]
        [TestCase("download")]
        public void incomplete_expected_state_is_rejected(string missing)
        {
            var request = Request();
            if (missing == "movie") request.MovieId = null;
            if (missing == "edition") request.MovieEditionId = null;
            if (missing == "state") request.ExpectedState = null;
            if (missing == "download") request.ExpectedDownloadId = null;
            Action act = () => _controller.Reconcile(Operation, request);
            act.Should().Throw<BadRequestException>();
            _submissions.VerifyNoOtherCalls();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void foreign_movie_or_edition_is_rejected(bool movie)
        {
            var request = Request();
            if (movie) request.MovieId++;
            else request.MovieEditionId++;
            Action act = () => _controller.Reconcile(Operation, request);
            act.Should().Throw<BadRequestException>();
            _submissions.VerifyNoOtherCalls();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void stale_state_or_download_identity_requires_reload(bool state)
        {
            if (state) _intent.State = MovieEditionSubmissionState.Recorded;
            else _intent.DownloadId = "synthetic-changed";
            _controller.Reconcile(Operation, Request()).Result.Should().BeOfType<Conflict<object>>();
            _submissions.VerifyNoOtherCalls();
        }

        [Test]
        public void missing_snapshots_require_manual_review()
        {
            _intent.HistoryData = null;
            _controller.GetForMovie(1).Value[0].CanReconcile.Should().BeFalse();
            _controller.Reconcile(Operation, Request()).Result.Should().BeOfType<Conflict<object>>();
            _submissions.VerifyNoOtherCalls();
        }

        [Test]
        public void matching_expected_state_delegates_only_to_reconciliation()
        {
            _submissions.Setup(s => s.Reconcile(Operation)).Returns(_intent);
            _controller.Reconcile(Operation, Request()).Result.Should().BeOfType<Ok<MovieEditionSubmissionResource>>();
            _submissions.Verify(s => s.Reconcile(Operation), Times.Once());
            _submissions.VerifyNoOtherCalls();
        }

        [Test]
        public void conflicting_persisted_evidence_returns_conflict()
        {
            _submissions.Setup(s => s.Reconcile(Operation)).Throws(new InvalidOperationException("Synthetic conflicting evidence"));
            _controller.Reconcile(Operation, Request()).Result.Should().BeOfType<Conflict<object>>();
        }
    }
}
