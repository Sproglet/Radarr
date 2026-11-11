using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class MovieEditionClassifierFixture : CoreTest<MovieEditionClassifier>
    {
        private List<MovieEdition> _editions;

        [SetUp]
        public void Setup()
        {
            _editions = new List<MovieEdition>
            {
                Edition(1, "theatrical"), Edition(2, "directors-cut"),
                Edition(3, "theatrical", "stereoscopic", "3d"),
                Edition(4, "theatrical", "framing", "imax"),
                Edition(5, "directors-cut", "stereoscopic", "3d")
            };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(42)).Returns(_editions);
        }

        private static MovieEdition Edition(int id, string cut, string key = null, string value = null)
        {
            var e = new MovieEdition { Id = id, MovieId = 42, RuleRevision = 7, Attributes = new Dictionary<string, string> { { "cut", cut } } };
            if (key != null)
            {
                e.Attributes[key] = value;
            }

            return e;
        }

        [TestCase("Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticGroup", 1)]
        [TestCase("Synthetic.Lunar.Voyage.2026.3D.1080p.BluRay-SyntheticGroup", 3)]
        [TestCase("Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup", 4)]
        [TestCase("Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup", 5)]
        [TestCase("Synthetic.IMAX.Voyage.2026.1080p.BluRay-SyntheticGroup", 1)]
        [TestCase("Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticIMAXGroup", 1)]
        public void definition_preview_uses_parser_evidence_for_complete_combinations_and_excludes_title_and_group_words(string title, int expected)
        {
            var proposal = Edition(2, "directors-cut");
            proposal.Name = "Synthetic New Directors";
            proposal.Monitored = false;
            var result = Subject.ClassifyProposed(proposal, title);
            result.Status.Should().Be(EditionClassificationStatus.Matched);
            result.CandidateEditionIds.Should().Equal(expected);
            _editions[1].Name.Should().BeNull();
        }

        [Test]
        public void definition_preview_replaces_one_target_without_persisting_its_rules()
        {
            var proposal = Edition(2, "directors-cut");
            proposal.MatchRules.Add("SyntheticCollector");
            proposal.RuleRevision = 8;
            var result = Subject.ClassifyProposed(proposal, "Synthetic.Lunar.Voyage.2026.SyntheticCollector.1080p.BluRay-SyntheticGroup");
            result.Status.Should().Be(EditionClassificationStatus.Matched);
            result.CandidateEditionIds.Should().Equal(2);
            result.RuleRevision.Should().Be(8);
            _editions[1].RuleRevision.Should().Be(7);
            _editions[1].MatchRules.Should().BeEmpty();
        }

        [Test]
        public void definition_preview_reports_overlapping_rules_independently_of_monitoring()
        {
            _editions[0].MatchRules.Add("SyntheticCollector");
            _editions[0].Monitored = true;
            var proposal = Edition(2, "directors-cut");
            proposal.Monitored = false;
            proposal.MatchRules.Add("SyntheticCollector");
            var result = Subject.ClassifyProposed(proposal, "Synthetic.Lunar.Voyage.2026.SyntheticCollector.1080p.BluRay-SyntheticGroup");
            result.Status.Should().Be(EditionClassificationStatus.Ambiguous);
            result.CandidateEditionIds.Should().Equal(1, 2);
            result.RuleRevision.Should().BeNull();
        }

        [TestCase("Synthetic.Lunar.Voyage.2026.Criterion.1080p.BluRay-SyntheticGroup", EditionClassificationStatus.Unconfigured)]
        [TestCase("Synthetic.Lunar.Voyage.2026.3D.IMAX.1080p.BluRay-SyntheticGroup", EditionClassificationStatus.Unconfigured)]
        [TestCase("Synthetic.Lunar.Voyage.2026.Directors.Cut.Theatrical.1080p.BluRay-SyntheticGroup", EditionClassificationStatus.Ambiguous)]
        [TestCase("", EditionClassificationStatus.Invalid)]
        [TestCase("???", EditionClassificationStatus.Invalid)]
        public void definition_preview_keeps_unknown_conflicting_or_absent_evidence_diagnostic(string title, EditionClassificationStatus status)
        {
            Subject.ClassifyProposed(Edition(2, "directors-cut"), title).Status.Should().Be(status);
        }

        [Test]
        public void definition_preview_rejects_foreign_or_oversized_input()
        {
            Subject.ClassifyProposed(Edition(99, "directors-cut"), "Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticGroup").Status.Should().Be(EditionClassificationStatus.Invalid);
            Subject.ClassifyProposed(Edition(2, "directors-cut"), new string('a', 16385)).Status.Should().Be(EditionClassificationStatus.Invalid);
        }

        [TestCase(null, "", 1)]
        [TestCase("Director's Cut", "", 2)]
        [TestCase(null, "3D", 3)]
        [TestCase(null, "HSBS", 3)]
        [TestCase(null, "SBS", 3)]
        [TestCase(null, "HOU", 3)]
        [TestCase(null, "Half-SBS", 3)]
        [TestCase(null, "Half-OU", 3)]
        [TestCase("IMAX", "", 4)]
        [TestCase("Director’s Cut", "3D", 5)]
        public void should_resolve_combinations_without_monitoring_fallback(string label, string tags, int id)
        {
            var result = Subject.Classify(42, "Synthetic.Lunar.Voyage.2026-SyntheticGroup", label, tags);
            result.Status.Should().Be(EditionClassificationStatus.Matched);
            result.CandidateEditionIds.Should().Equal(id);
            result.RuleRevision.Should().Be(7);
        }

        [TestCase("SBSynthetic")]
        [TestCase("HOUston")]
        [TestCase("3Dimensions")]
        public void should_not_match_partial_presentation_words(string tags)
        {
            Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", null, tags).CandidateEditionIds.Should().Equal(1);
        }

        [TestCase("Criterion", "", EditionClassificationStatus.Unconfigured)]
        [TestCase("Extended", "", EditionClassificationStatus.Unconfigured)]
        [TestCase("Directors Cut Theatrical", "", EditionClassificationStatus.Ambiguous)]
        [TestCase(null, "2D 3D", EditionClassificationStatus.Ambiguous)]
        [TestCase("IMAX Open Matte", "", EditionClassificationStatus.Ambiguous)]
        [TestCase(null, "3D IMAX", EditionClassificationStatus.Unconfigured)]
        public void should_not_fallback_for_unknown_or_conflicting_evidence(string label, string tags, EditionClassificationStatus status)
        {
            Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", label, tags).Status.Should().Be(status);
        }

        [Test]
        public void should_preserve_all_three_dimensions_when_configured()
        {
            var target = Edition(6, "theatrical", "stereoscopic", "3d");
            target.Attributes["framing"] = "imax";
            _editions.Add(target);
            var result = Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", "IMAX", "3D");
            result.CandidateEditionIds.Should().Equal(6);
            result.Attributes.Should().BeEquivalentTo(target.Attributes);
        }

        [Test]
        public void should_not_route_unlabelled_release_to_only_monitored_cut()
        {
            _editions.RemoveAt(0);
            _editions[0].Monitored = true;
            Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", null, "").Status.Should().Be(EditionClassificationStatus.Unconfigured);
        }

        [Test]
        public void custom_rules_are_case_insensitive_or_alternatives_and_override_labels()
        {
            _editions[1].MatchRules = new List<string> { "NeverMatches", "syntheticgroup$" };
            var result = Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", "Criterion", "IMAX");
            result.CandidateEditionIds.Should().Equal(2);
            _editions[0].MatchRules.Add("Synthetic");
            Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", null, "").Status.Should().Be(EditionClassificationStatus.Ambiguous);
        }

        [Test]
        public void invalid_rules_and_timeouts_fail_closed()
        {
            _editions[0].MatchRules.Add("[");
            Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", null, "").Status.Should().Be(EditionClassificationStatus.Invalid);
            _editions[0].MatchRules[0] = "^(a+)+$";
            Subject.Classify(42, new string('a', 12000) + "!", null, "").Status.Should().Be(EditionClassificationStatus.Invalid);
        }

        [Test]
        public void explicit_assignment_requires_membership_and_preserves_custom_attributes()
        {
            _editions[0].Attributes["custom-brand"] = "Synthetic Collection";
            Subject.Classify(42, "Synthetic.Voyage", "Directors Cut", "3D", 1).Attributes.Should().ContainKey("custom-brand");
            Subject.Classify(42, "Synthetic.Voyage", null, "", 99).Status.Should().Be(EditionClassificationStatus.Invalid);
        }

        [Test]
        public void duplicate_attribute_targets_are_ambiguous()
        {
            _editions.Add(Edition(6, "theatrical"));
            Subject.Classify(42, "Synthetic.Voyage.2026-SyntheticGroup", null, "").Status.Should().Be(EditionClassificationStatus.Ambiguous);
        }

        [Test]
        public void rules_are_validated_before_saving()
        {
            Action validate = () => MovieEditionClassifier.ValidateRules(new[] { "[" });
            validate.Should().Throw<ArgumentException>();
        }
    }
}
