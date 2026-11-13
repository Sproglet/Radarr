using System;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Movies
{
    public partial class EditionRemovalFixture
    {
        [Test]
        public void complete_request_delegates_only_the_exact_retirement_snapshot()
        {
            _controller.RetireEmpty(5, Request()).Result.Should().BeOfType<Ok<object>>();

            _management.Verify(m => m.RetireEmpty(It.Is<MovieEditionRemovalChange>(c => c.MovieId == 1 && c.EditionId == 5 &&
                c.ExpectedPrimaryEditionId == 2 && c.ExpectedMovieMonitored && !c.ExpectedEditionMonitored && c.ExpectedMovieFileId == null &&
                c.ExpectedQualityProfileId == 3 && c.ExpectedRuleRevision == 4 && c.ExpectedName == "Synthetic Unused Collector")), Times.Once());
            _management.VerifyNoOtherCalls();
        }

        [Test]
        public void retirement_invalid_request_returns_bad_request()
        {
            _management.Setup(m => m.RetireEmpty(It.IsAny<MovieEditionRemovalChange>())).Throws(new ArgumentException("Synthetic missing snapshot"));
            Action remove = () => _controller.RetireEmpty(5, null);
            remove.Should().Throw<BadRequestException>();
        }

        [Test]
        public void retirement_missing_target_returns_not_found()
        {
            _management.Setup(m => m.RetireEmpty(It.IsAny<MovieEditionRemovalChange>())).Throws(new ModelNotFoundException(typeof(MovieEdition), 5));
            Action remove = () => _controller.RetireEmpty(5, Request());
            remove.Should().Throw<NotFoundException>();
        }

        [Test]
        public void retirement_stale_or_referenced_target_returns_conflict()
        {
            _management.Setup(m => m.RetireEmpty(It.IsAny<MovieEditionRemovalChange>())).Throws(new InvalidOperationException("Synthetic saved work"));
            _controller.RetireEmpty(5, Request()).Result.Should().BeOfType<Conflict<object>>();
        }
    }
}
