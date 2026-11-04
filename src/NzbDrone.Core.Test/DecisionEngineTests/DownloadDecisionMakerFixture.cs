using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class DownloadDecisionMakerFixture : CoreTest<DownloadDecisionMaker>
    {
        private List<ReleaseInfo> _reports;
        private RemoteMovie _remoteMovie;

        private Mock<IDownloadDecisionEngineSpecification> _pass1;
        private Mock<IDownloadDecisionEngineSpecification> _pass2;
        private Mock<IDownloadDecisionEngineSpecification> _pass3;

        private Mock<IDownloadDecisionEngineSpecification> _fail1;
        private Mock<IDownloadDecisionEngineSpecification> _fail2;
        private Mock<IDownloadDecisionEngineSpecification> _fail3;

        [SetUp]
        public void Setup()
        {
            _pass1 = new Mock<IDownloadDecisionEngineSpecification>();
            _pass2 = new Mock<IDownloadDecisionEngineSpecification>();
            _pass3 = new Mock<IDownloadDecisionEngineSpecification>();

            _fail1 = new Mock<IDownloadDecisionEngineSpecification>();
            _fail2 = new Mock<IDownloadDecisionEngineSpecification>();
            _fail3 = new Mock<IDownloadDecisionEngineSpecification>();

            _pass1.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Accept);
            _pass2.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Accept);
            _pass3.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Accept);

            _fail1.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.Unknown, "fail1"));
            _fail2.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.Unknown, "fail2"));
            _fail3.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>())).Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.Unknown, "fail3"));

            _reports = new List<ReleaseInfo> { new ReleaseInfo { Title = "Trolls.2016.720p.WEB-DL.DD5.1.H264-FGT" } };
            _remoteMovie = new RemoteMovie
            {
                Movie = new Movie(),
                ParsedMovieInfo = new ParsedMovieInfo()
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>())).Returns(_remoteMovie);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void multi_edition_reports_show_evaluation_but_never_authorize_a_download(bool search)
        {
            _reports[0].Title = "Synthetic.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup";
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(_remoteMovie.Movie.Id))
                .Returns(new List<MovieEdition> { new MovieEdition { Id = 1 }, new MovieEdition { Id = 2 } });
            var evaluation = new MovieEditionEvaluation
            {
                Classification = new EditionClassification { Status = EditionClassificationStatus.Matched, CandidateEditionIds = new List<int> { 2 } },
                Context = new MovieEditionContext(new MovieEdition { Id = 2 }, null, null, true), CustomFormatScore = 123
            };
            Mocker.GetMock<IMovieEditionEvaluationService>().Setup(e => e.PreviewParsed(_remoteMovie)).Returns(evaluation);
            GivenSpecifications(_pass1);

            var results = search ? Subject.GetSearchDecision(_reports, new MovieSearchCriteria()) : Subject.GetRssDecision(_reports);

            results.Should().ContainSingle();
            results[0].RemoteMovie.EditionContext.Edition.Id.Should().Be(2);
            results[0].RemoteMovie.CustomFormatScore.Should().Be(123);
            results[0].RemoteMovie.DownloadAllowed.Should().BeFalse();
            results[0].Rejections.Should().ContainSingle(r => r.Reason == DownloadRejectionReason.MultiEditionAcquisitionDisabled);
            _pass1.Verify(p => p.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
        }

        [Test]
        public void edition_preview_reports_selected_profile_rejections_and_keeps_gate_closed()
        {
            _reports[0].Title = "Synthetic.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup";
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(_remoteMovie.Movie.Id))
                .Returns(new List<MovieEdition> { new MovieEdition { Id = 1 }, new MovieEdition { Id = 2 } });
            Mocker.GetMock<NzbDrone.Core.CustomFormats.ICustomFormatCalculationService>()
                .Setup(f => f.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>()))
                .Returns(new List<NzbDrone.Core.CustomFormats.CustomFormat>());
            var evaluation = new MovieEditionEvaluation
            {
                Classification = new EditionClassification { Status = EditionClassificationStatus.Matched },
                Context = new MovieEditionContext(new MovieEdition { Id = 2 }, new NzbDrone.Core.Profiles.Qualities.QualityProfile { MinFormatScore = 200 }, null, true),
                CustomFormatScore = 123
            };
            Mocker.GetMock<IMovieEditionEvaluationService>().Setup(e => e.PreviewParsed(_remoteMovie)).Returns(evaluation);
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(new IDownloadDecisionEngineSpecification[]
            {
                new CustomFormatAllowedbyProfileSpecification(TestLogger), _pass1.Object
            });

            var decision = Subject.GetRssDecision(_reports).Single();

            decision.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.MultiEditionAcquisitionDisabled);
            decision.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.CustomFormatMinimumScore);
            decision.RemoteMovie.DownloadAllowed.Should().BeFalse();
            _pass1.Verify(p => p.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
        }

        private void GivenSpecifications(params Mock<IDownloadDecisionEngineSpecification>[] mocks)
        {
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(mocks.Select(c => c.Object));
        }

        [Test]
        public void should_call_all_specifications()
        {
            GivenSpecifications(_pass1, _pass2, _pass3, _fail1, _fail2, _fail3);

            Subject.GetRssDecision(_reports).ToList();

            _fail1.Verify(c => c.IsSatisfiedBy(_remoteMovie, It.IsAny<ReleaseDecisionInformation>()), Times.Once());
            _fail2.Verify(c => c.IsSatisfiedBy(_remoteMovie, It.IsAny<ReleaseDecisionInformation>()), Times.Once());
            _fail3.Verify(c => c.IsSatisfiedBy(_remoteMovie, It.IsAny<ReleaseDecisionInformation>()), Times.Once());
            _pass1.Verify(c => c.IsSatisfiedBy(_remoteMovie, It.IsAny<ReleaseDecisionInformation>()), Times.Once());
            _pass2.Verify(c => c.IsSatisfiedBy(_remoteMovie, It.IsAny<ReleaseDecisionInformation>()), Times.Once());
            _pass3.Verify(c => c.IsSatisfiedBy(_remoteMovie, It.IsAny<ReleaseDecisionInformation>()), Times.Once());
        }

        [Test]
        public void should_return_rejected_if_single_specs_fail()
        {
            GivenSpecifications(_fail1);

            var result = Subject.GetRssDecision(_reports);

            result.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_return_rejected_if_one_of_specs_fail()
        {
            GivenSpecifications(_pass1, _fail1, _pass2, _pass3);

            var result = Subject.GetRssDecision(_reports);

            result.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_return_pass_if_all_specs_pass()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            var result = Subject.GetRssDecision(_reports);

            result.Single().Approved.Should().BeTrue();
        }

        [Test]
        public void should_have_same_number_of_rejections_as_specs_that_failed()
        {
            GivenSpecifications(_pass1, _pass2, _pass3, _fail1, _fail2, _fail3);

            var result = Subject.GetRssDecision(_reports);
            result.Single().Rejections.Should().HaveCount(3);
        }

        [Test]
        public void should_not_attempt_to_map_episode_if_not_parsable()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);
            _reports[0].Title = "Not parsable";

            Subject.GetRssDecision(_reports).ToList();

            Mocker.GetMock<IParsingService>().Verify(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()), Times.Never());

            _pass1.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
            _pass2.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
            _pass3.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
        }

        [Test]
        public void should_return_rejected_result_for_unparsable_search()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);
            _reports[0].Title = "1937 - Snow White and the Seven Dwarves";

            Subject.GetSearchDecision(_reports, new MovieSearchCriteria()).ToList();

            Mocker.GetMock<IParsingService>().Verify(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()), Times.Never());

            _pass1.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
            _pass2.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
            _pass3.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
        }

        [Test]
        public void should_not_attempt_to_make_decision_if_series_is_unknown()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteMovie.Movie = null;

            Subject.GetRssDecision(_reports);

            _pass1.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
            _pass2.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
            _pass3.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<ReleaseDecisionInformation>()), Times.Never());
        }

        [Test]
        public void broken_report_shouldnt_blowup_the_process()
        {
            GivenSpecifications(_pass1);

            Mocker.GetMock<IParsingService>().Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()))
                     .Throws<TestException>();

            _reports = new List<ReleaseInfo>
                {
                    new ReleaseInfo { Title = "Synthetic.Voyage.2026.720p.WEB-DL-SyntheticGroup" },
                    new ReleaseInfo { Title = "Synthetic.Voyage.2026.720p.WEB-DL-SyntheticGroup" },
                    new ReleaseInfo { Title = "Synthetic.Voyage.2026.720p.WEB-DL-SyntheticGroup" }
                };

            var decisions = Subject.GetRssDecision(_reports);

            Mocker.GetMock<IParsingService>().Verify(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()), Times.Exactly(_reports.Count));

            decisions.Should().HaveCount(3);
            decisions.Should().OnlyContain(d => d.Rejections.Any(r => r.Reason == DownloadRejectionReason.Error));
            ExceptionVerification.IgnoreErrors();
        }

        [Test]
        public void should_return_unknown_series_rejection_if_series_is_unknown()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteMovie.Movie = null;

            var result = Subject.GetRssDecision(_reports);

            result.Should().HaveCount(1);
        }

        [Test]
        public void should_not_allow_download_if_series_is_unknown()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteMovie.Movie = null;

            var result = Subject.GetRssDecision(_reports);

            result.Should().HaveCount(1);

            // result.First().RemoteMovie.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        [Ignore("Series")]
        public void should_not_allow_download_if_no_episodes_found()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteMovie.Movie = null;

            var result = Subject.GetRssDecision(_reports);

            result.Should().HaveCount(1);

            // result.First().RemoteMovie.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        public void should_return_a_decision_when_exception_is_caught()
        {
            GivenSpecifications(_pass1);

            Mocker.GetMock<IParsingService>().Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()))
                     .Throws<TestException>();

            _reports = new List<ReleaseInfo>
                {
                    new ReleaseInfo { Title = "Trolls.2016.720p.WEB-DL.DD5.1.H264-FGT" },
                };

            Subject.GetRssDecision(_reports).Should().HaveCount(1);

            ExceptionVerification.ExpectedErrors(1);
        }
    }
}
