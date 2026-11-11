using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    public class SingleEditionAcquisitionGuardFixture : CoreTest<MultiEditionAcquisitionSpecification>
    {
        [TestCase("profile")]
        [TestCase("monitoring")]
        [TestCase("attributes")]
        [TestCase("rules")]
        [TestCase("foreign")]
        [TestCase("recovery")]
        public void managed_or_damaged_single_target_is_rejected_without_mutating_preferences(string state)
        {
            var movie = new Movie { Id = 1, Monitored = true, QualityProfileId = 1 };
            var target = new MovieEdition { Id = 2, MovieId = 1, Monitored = true, QualityProfileId = 1 };
            var release = new RemoteMovie { Movie = movie, Release = new ReleaseInfo { Title = "Synthetic.Lunar.Voyage.2026-SyntheticGroup" } };
            if (state == "profile") target.QualityProfileId = 7;
            if (state == "monitoring") target.Monitored = false;
            if (state == "attributes") target.Attributes["stereoscopic"] = "3d";
            if (state == "rules") target.MatchRules.Add("SyntheticGroup$");
            if (state == "foreign") target.MovieId = 99;
            if (state == "recovery") release.EditionRecoveryError = "Synthetic invalid assignment";
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(1)).Returns(new List<MovieEdition> { target });
            var decision = Subject.IsSatisfiedBy(release, new ReleaseDecisionInformation());
            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(DownloadRejectionReason.MultiEditionAcquisitionDisabled);
            movie.QualityProfileId.Should().Be(1);
            release.EditionContext.Should().BeNull();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_equivalent_single_target_keeps_existing_behavior(bool monitored)
        {
            var movie = new Movie { Id = 1, Monitored = monitored, QualityProfileId = 1 };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(1)).Returns(new List<MovieEdition>
                { new() { Id = 2, MovieId = 1, Monitored = monitored, QualityProfileId = 1 } });
            Subject.IsSatisfiedBy(new RemoteMovie { Movie = movie }, new ReleaseDecisionInformation()).Accepted.Should().BeTrue();
        }
    }
}
