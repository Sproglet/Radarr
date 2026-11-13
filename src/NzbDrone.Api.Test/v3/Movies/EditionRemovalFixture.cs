using System;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using Radarr.Api.V3.MovieEditions;
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Movies
{
    public class EditionRemovalFixture
    {
        private Mock<IMovieEditionManagementService> _management;
        private MovieEditionController _controller;

        [SetUp]
        public void Setup()
        {
            _management = new();
            _controller = new(null, null, null, null, _management.Object);
        }

        private static MovieEditionRemovalResource Request() => new()
        {
            MovieId = 1, ExpectedPrimaryEditionId = 2, ExpectedMovieMonitored = true, ExpectedEditionMonitored = false,
            ExpectedMovieFileId = null, ExpectedQualityProfileId = 3, ExpectedRuleRevision = 4, ExpectedName = "Synthetic Unused Collector"
        };

        [Test]
        public void complete_request_delegates_only_the_exact_removal_snapshot()
        {
            _controller.RemoveUnused(5, Request()).Result.Should().BeOfType<Ok<object>>();

            _management.Verify(m => m.RemoveUnused(It.Is<MovieEditionRemovalChange>(c => c.MovieId == 1 && c.EditionId == 5 &&
                c.ExpectedPrimaryEditionId == 2 && c.ExpectedMovieMonitored && !c.ExpectedEditionMonitored && c.ExpectedMovieFileId == null &&
                c.ExpectedQualityProfileId == 3 && c.ExpectedRuleRevision == 4 && c.ExpectedName == "Synthetic Unused Collector")), Times.Once());
            _management.VerifyNoOtherCalls();
        }

        [Test]
        public void invalid_request_returns_bad_request()
        {
            _management.Setup(m => m.RemoveUnused(It.IsAny<MovieEditionRemovalChange>())).Throws(new ArgumentException("Synthetic missing snapshot"));
            Action remove = () => _controller.RemoveUnused(5, null);
            remove.Should().Throw<BadRequestException>();
        }

        [Test]
        public void missing_target_returns_not_found()
        {
            _management.Setup(m => m.RemoveUnused(It.IsAny<MovieEditionRemovalChange>())).Throws(new ModelNotFoundException(typeof(MovieEdition), 5));
            Action remove = () => _controller.RemoveUnused(5, Request());
            remove.Should().Throw<NotFoundException>();
        }

        [Test]
        public void stale_or_referenced_target_returns_conflict()
        {
            _management.Setup(m => m.RemoveUnused(It.IsAny<MovieEditionRemovalChange>())).Throws(new InvalidOperationException("Synthetic saved work"));
            _controller.RemoveUnused(5, Request()).Result.Should().BeOfType<Conflict<object>>();
        }

        [TestCase("ExpectedPrimaryEditionId")]
        [TestCase("ExpectedMovieMonitored")]
        [TestCase("ExpectedEditionMonitored")]
        [TestCase("ExpectedMovieFileId")]
        [TestCase("ExpectedQualityProfileId")]
        [TestCase("ExpectedRuleRevision")]
        [TestCase("ExpectedName")]
        public void missing_expected_json_field_is_rejected_including_an_omitted_empty_file(string field)
        {
            var values = JsonSerializer.SerializeToNode(Request()).AsObject();
            values.Remove(field);
            Action parse = () => JsonSerializer.Deserialize<MovieEditionRemovalResource>(values.ToJsonString());
            parse.Should().Throw<JsonException>();
        }
    }
}
