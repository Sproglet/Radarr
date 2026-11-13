using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    public class ReconcileMovieEditionSubmissionsFixture : CoreTest<ReconcileMovieEditionSubmissions>
    {
        [Test]
        public void a_full_failed_page_does_not_starve_later_rows_or_expand_the_saved_upper_bound()
        {
            var failed = Enumerable.Range(1, 100).Select(id => new MovieEditionSubmissionCandidate { Id = id, OperationId = Guid.NewGuid().ToString("D") }).ToList();
            var good = new MovieEditionSubmissionCandidate { Id = 130, OperationId = Guid.NewGuid().ToString("D") };
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Setup(j => j.ReconciliationUpperBound()).Returns(130);
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Setup(j => j.ReconciliationCandidates(0, 130, 100)).Returns(failed);
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Setup(j => j.ReconciliationCandidates(100, 130, 100)).Returns(new List<MovieEditionSubmissionCandidate> { good });
            Mocker.GetMock<IMovieEditionSubmissionService>().Setup(s => s.Reconcile(It.IsAny<string>())).Throws(new InvalidOperationException("Synthetic private release payload must not enter worker logs"));
            Mocker.GetMock<IMovieEditionSubmissionService>().Setup(s => s.Reconcile(good.OperationId)).Returns(new MovieEditionSubmission { State = MovieEditionSubmissionState.Recorded });

            Subject.HandleAsync(new ApplicationStartedEvent());

            Mocker.GetMock<IMovieEditionSubmissionRepository>().Verify(j => j.ReconciliationUpperBound(), Times.Once());
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Verify(j => j.ReconciliationCandidates(0, 130, 100), Times.Once());
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Verify(j => j.ReconciliationCandidates(100, 130, 100), Times.Once());
            Mocker.GetMock<IMovieEditionSubmissionRepository>().VerifyNoOtherCalls();
            foreach (var candidate in failed.Append(good))
            {
                Mocker.GetMock<IMovieEditionSubmissionService>().Verify(s => s.Reconcile(candidate.OperationId), Times.Once());
            }

            Mocker.GetMock<IMovieEditionSubmissionService>().VerifyNoOtherCalls();
            ExceptionVerification.ExpectedWarns(100);
        }

        [Test]
        public void an_empty_journal_does_not_load_payloads_or_attempt_reconciliation()
        {
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Setup(j => j.ReconciliationUpperBound()).Returns(0);

            Subject.Clean();

            Mocker.GetMock<IMovieEditionSubmissionRepository>().Verify(j => j.ReconciliationUpperBound(), Times.Once());
            Mocker.GetMock<IMovieEditionSubmissionRepository>().VerifyNoOtherCalls();
            Mocker.GetMock<IMovieEditionSubmissionService>().VerifyNoOtherCalls();
        }
    }
}
