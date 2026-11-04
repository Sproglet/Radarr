using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class MovieEditionEvidenceFixture : CoreTest<MovieEditionClassifier>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(42)).Returns(new List<MovieEdition>
            {
                new MovieEdition { Id = 1, Attributes = new Dictionary<string, string> { { "cut", "theatrical" } } },
                new MovieEdition { Id = 2, Attributes = new Dictionary<string, string> { { "cut", "theatrical" }, { "stereoscopic", "3d" } } },
                new MovieEdition { Id = 3, Attributes = new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } } },
                new MovieEdition { Id = 4, Attributes = new Dictionary<string, string> { { "cut", "theatrical" }, { "framing", "imax" } } }
            });
        }

        [TestCase("Synthetic.Voyage.2026.3D.1080p.BluRay-SyntheticGroup", 2)]
        [TestCase("Synthetic.Voyage.2026.HSBS.1080p.BluRay-SyntheticGroup", 2)]
        [TestCase("Synthetic.Voyage.2026.Half-SBS.1080p.BluRay-SyntheticGroup", 2)]
        [TestCase("Synthetic.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup", 3)]
        [TestCase("Synthetic.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup", 4)]
        [TestCase("Synthetic.IMAX.Voyage.2026.1080p.BluRay-SyntheticGroup", 1)]
        [TestCase("Synthetic.Extended.Voyage.2026.1080p.BluRay-SyntheticGroup", 1)]
        [TestCase("Synthetic.Voyage.2026.1080p.BluRay-IMAX", 1)]
        [TestCase("Synthetic.Voyage.2026.IMAX.1080p.BluRay-IMAX", 4)]
        public void should_classify_parser_evidence_without_title_or_group_false_positives(string title, int target)
        {
            var parsed = Parser.Parser.ParseMovieTitle(title);
            parsed.Should().NotBeNull();
            var evidence = MovieEditionEvidence.FromParsed(parsed);
            evidence.Valid.Should().BeTrue();
            var result = Subject.Classify(42, title, evidence.Label, evidence.Presentation);
            result.Status.Should().Be(EditionClassificationStatus.Matched);
            result.CandidateEditionIds.Should().Equal(new[] { target }, "evidence: {0}; tags: {1}; simple: {2}", evidence.Label, evidence.Presentation, parsed.SimpleReleaseTitle);
        }

        [TestCase("Synthetic.Voyage.2026.Directors.Cut.Theatrical.1080p.BluRay-SyntheticGroup", EditionClassificationStatus.Ambiguous)]
        [TestCase("Synthetic.Voyage.2026.Criterion.1080p.BluRay-SyntheticGroup", EditionClassificationStatus.Unconfigured)]
        [TestCase("Synthetic.Voyage.2026.Synthetic.Edition.1080p.BluRay-SyntheticGroup", EditionClassificationStatus.Unconfigured)]
        public void should_preserve_conflicting_and_unknown_labels(string title, EditionClassificationStatus status)
        {
            var parsed = Parser.Parser.ParseMovieTitle(title);
            var evidence = MovieEditionEvidence.FromParsed(parsed);
            Subject.Classify(42, title, evidence.Label, evidence.Presentation).Status.Should().Be(status);
        }
    }
}
