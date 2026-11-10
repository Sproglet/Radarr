using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.Wanted;

namespace NzbDrone.Api.Test.v3.Wanted
{
    [TestFixture]
    public class WantedEditionResourceFixture
    {
        [TestCase("present")]
        [TestCase("missing")]
        [TestCase("invalid-profile")]
        [TestCase("invalid-file")]
        public void should_expose_target_parent_monitoring_and_link_diagnostics(string state)
        {
            var row = new MovieEditionWanted
            {
                Id = 2, MovieId = 1, PrimaryEditionId = 1, MovieTitle = "Synthetic Lunar Voyage", Year = 2026, TmdbId = -1000001,
                Name = "Synthetic Directors Cut 3D", QualityProfileId = 7, QualityProfileName = "Synthetic Secondary",
                MovieMonitored = false, Monitored = true, ProfileExists = state != "invalid-profile",
                MovieFileId = state == "present" || state == "invalid-file" ? 5 : null,
                ResolvedMovieFileId = state == "present" ? 5 : null,
                RelativePath = state == "present" ? "Synthetic-Directors-3D.mkv" : null,
                Quality = state == "present" ? new QualityModel(Quality.DVD) : null,
                Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" }, { "branding", "Synthetic Collector" } }
            };
            var resource = row.ToResource();
            resource.Id.Should().Be(2);
            resource.MovieId.Should().Be(1);
            resource.Title.Should().Be("Synthetic Lunar Voyage");
            resource.Year.Should().Be(2026);
            resource.TmdbId.Should().Be(-1000001);
            resource.QualityProfileId.Should().Be(7);
            resource.Monitored.Should().BeTrue();
            resource.MovieMonitored.Should().BeFalse();
            resource.EffectiveMonitored.Should().BeFalse();
            resource.IsPrimary.Should().BeFalse();
            resource.MovieFileId.Should().Be(row.MovieFileId);
            resource.Attributes.Should().BeEquivalentTo(row.Attributes);
            resource.State.Should().Be(state.StartsWith("invalid") ? "invalid" : state);
            if (state.StartsWith("invalid"))
            {
                resource.RejectionReason.Should().NotBeNullOrEmpty();
                resource.RelativePath.Should().BeNull();
            }
        }
    }
}
