using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    public class EditionRetryFixture : CoreTest<RedownloadFailedDownloadService>
    {
        private DownloadFailedEvent _failure;

        [SetUp]
        public void Setup()
        {
            _failure = new DownloadFailedEvent { MovieId = 1, SourceTitle = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup" };
            Mocker.GetMock<IConfigService>().SetupGet(s => s.AutoRedownloadFailed).Returns(true);
            Mocker.GetMock<IMovieEditionRepository>().Setup(s => s.GetByMovie(1)).Returns(new List<MovieEdition> { new MovieEdition { Id = 1, MovieId = 1 } });
        }

        [Test]
        public void legacy_single_edition_failure_keeps_automatic_retry_behavior()
        {
            Subject.Handle(_failure);
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.Is<MoviesSearchCommand>(c => c.MovieIds.Count == 1 && c.MovieIds[0] == 1), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Once());
        }

        [TestCase("assigned")]
        [TestCase("unassigned")]
        [TestCase("context")]
        [TestCase("recovery-error")]
        [TestCase("remote-error")]
        public void edition_failure_never_queues_a_movie_wide_retry_while_acquisition_is_disabled(string identity)
        {
            if (identity == "assigned")
            {
                _failure.MovieEditionId = 2;
            }
            else if (identity == "unassigned")
            {
                Mocker.GetMock<IMovieEditionRepository>().Setup(s => s.GetByMovie(1)).Returns(new List<MovieEdition> { new MovieEdition(), new MovieEdition() });
            }
            else
            {
                _failure.TrackedDownload = new TrackedDownload { RemoteMovie = new RemoteMovie() };
                if (identity == "context")
                {
                    _failure.TrackedDownload.RemoteMovie.EditionContext = new MovieEditionContext(new MovieEdition { Id = 2 }, null, null);
                }
                else if (identity == "recovery-error")
                {
                    _failure.TrackedDownload.EditionRecoveryError = "Synthetic review required";
                }
                else
                {
                    _failure.TrackedDownload.RemoteMovie.EditionRecoveryError = "Synthetic review required";
                }
            }

            Subject.Handle(_failure);
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoviesSearchCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
        }

        [TestCase("skip")]
        [TestCase("disabled")]
        [TestCase("interactive")]
        public void legacy_retry_still_respects_existing_preferences(string preference)
        {
            _failure.SkipRedownload = preference == "skip";
            if (preference == "disabled")
            {
                Mocker.GetMock<IConfigService>().SetupGet(s => s.AutoRedownloadFailed).Returns(false);
            }

            if (preference == "interactive")
            {
                _failure.ReleaseSource = ReleaseSourceType.InteractiveSearch;
            }

            Subject.Handle(_failure);
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoviesSearchCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
        }
    }
}
