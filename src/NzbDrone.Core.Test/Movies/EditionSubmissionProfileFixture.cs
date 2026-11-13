using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionSubmissionJournalFixture
    {
        private QualityProfile SubmissionProfile()
        {
            var repository = Mocker.Resolve<QualityProfileRepository>();
            var profile = repository.Get(_edition.QualityProfileId);
            profile.Items = new()
            {
                new() { Quality = Quality.SDTV, Allowed = true },
                new() { Quality = Quality.Bluray1080p, Allowed = true }
            };
            repository.Update(profile);
            return repository.Get(profile.Id);
        }

        [TestCase("cutoff")]
        [TestCase("allowed")]
        [TestCase("order")]
        [TestCase("minimum-score")]
        [TestCase("upgrade-score")]
        [TestCase("format-cutoff")]
        [TestCase("language")]
        [TestCase("upgrades")]
        public async Task stale_profile_settings_are_rejected_before_preparation(string change)
        {
            var profile = SubmissionProfile();
            var release = SubmissionRelease();
            switch (change)
            {
                case "cutoff": profile.Cutoff = Quality.Bluray1080p.Id; break;
                case "allowed": profile.Items[0].Allowed = false; break;
                case "order": profile.Items.Reverse(); break;
                case "minimum-score": profile.MinFormatScore++; break;
                case "upgrade-score": profile.MinUpgradeFormatScore++; break;
                case "format-cutoff": profile.CutoffFormatScore++; break;
                case "language": profile.Language = Language.French; break;
                case "upgrades": profile.UpgradeAllowed = !profile.UpgradeAllowed; break;
            }

            Mocker.Resolve<QualityProfileRepository>().Update(profile);
            var client = SubmissionClient();
            Func<Task> submit = () => Coordinator().Submit(release, client.Object, null);

            await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*profile*before client IO*");
            _journal.Count().Should().Be(0);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [TestCase("score")]
        [TestCase("regex")]
        [TestCase("removed")]
        public async Task fresh_format_definitions_are_checked_even_when_the_profile_cache_is_stale(string change)
        {
            var formats = Mocker.Resolve<CustomFormatRepository>();
            var format = formats.Insert(new CustomFormat("Synthetic Submission Format", new ReleaseGroupSpecification { Value = "SyntheticOldGroup" }));
            // Keep this cached definition unchanged while the underlying repository changes.
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat> { format });
            var profiles = Mocker.Resolve<QualityProfileRepository>();
            var profile = SubmissionProfile();
            profile.FormatItems = new() { new ProfileFormatItem { Format = format, Score = 77 } };
            profiles.Update(profile);
            var release = SubmissionRelease();
            if (change == "score")
            {
                profile.FormatItems[0].Score = 88;
                profiles.Update(profile);
            }
            else if (change == "regex")
            {
                var changed = formats.Get(format.Id);
                ((ReleaseGroupSpecification)changed.Specifications[0]).Value = "SyntheticNewGroup";
                formats.Update(changed);
            }
            else formats.Delete(format.Id);

            var client = SubmissionClient();
            Func<Task> submit = () => Coordinator().Submit(release, client.Object, null);

            await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*profile*before client IO*");
            _journal.Count().Should().Be(0);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [Test]
        public async Task profile_snapshot_is_frozen_before_waiting_for_the_acquisition_gate()
        {
            var profile = SubmissionProfile();
            var release = SubmissionRelease();
            var client = SubmissionClient();
            var service = Coordinator();
            Task send;
            using (MovieEditionAcquisitionGate.Acquire(_movie.Id))
            {
                send = service.Submit(release, client.Object, null);
                send.IsCompleted.Should().BeFalse();
                profile.MinFormatScore++;
                Mocker.Resolve<QualityProfileRepository>().Update(profile);
                release.EditionContext.QualityProfile.MinFormatScore = profile.MinFormatScore;
            }

            Func<Task> wait = () => send;
            await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage("*profile*before client IO*");
            _journal.Count().Should().Be(0);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [Test]
        public async Task a_profile_change_after_preparation_retains_intent_without_client_io()
        {
            var profile = SubmissionProfile();
            var release = SubmissionRelease();
            var journal = new Mock<IMovieEditionSubmissionRepository>();
            journal.Setup(j => j.Prepare(It.IsAny<MovieEditionSubmission>())).Returns<MovieEditionSubmission>(request =>
            {
                var prepared = _journal.Prepare(request);
                profile.MinFormatScore++;
                Mocker.Resolve<QualityProfileRepository>().Update(profile);
                return prepared;
            });
            journal.Setup(j => j.Get(It.IsAny<int>())).Returns<int>(id => _journal.Get(id));
            var client = SubmissionClient();
            Func<Task> submit = () => Coordinator(journal.Object).Submit(release, client.Object, null);

            await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*profile*before client IO*");
            _journal.All().Should().ContainSingle().Which.State.Should().Be(MovieEditionSubmissionState.Prepared);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task a_missing_or_foreign_evaluated_profile_is_rejected_before_intent(bool foreign)
        {
            var release = SubmissionRelease();
            release.EditionContext = new MovieEditionContext(_edition, foreign ? new QualityProfile { Id = _edition.QualityProfileId + 99 } : null, null, true);
            var client = SubmissionClient();
            Func<Task> submit = () => Coordinator().Submit(release, client.Object, null);

            await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*profile*before client IO*");
            _journal.Count().Should().Be(0);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [Test]
        public async Task unchanged_profile_sends_fresh_format_definitions_without_sharing_cached_objects()
        {
            var formats = Mocker.Resolve<CustomFormatRepository>();
            var format = formats.Insert(new CustomFormat("Synthetic Submission Format", new ReleaseGroupSpecification { Value = "SyntheticOldGroup" }));
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat> { format });
            var profile = SubmissionProfile();
            profile.FormatItems = new() { new ProfileFormatItem { Format = format, Score = 77 } };
            Mocker.Resolve<QualityProfileRepository>().Update(profile);
            var release = SubmissionRelease();
            var client = SubmissionClient();
            client.Setup(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()))
                .Returns<NzbDrone.Core.Parser.Model.RemoteMovie, IIndexer>((sent, _) =>
                {
                    var sentFormat = sent.EditionContext.QualityProfile.FormatItems[0].Format;
                    sentFormat.Should().NotBeSameAs(format);
                    ((ReleaseGroupSpecification)format.Specifications[0]).Value = "SyntheticMutatedCache";
                    ((ReleaseGroupSpecification)sentFormat.Specifications[0]).Value.Should().Be("SyntheticOldGroup");
                    return Task.FromResult("synthetic-download");
                });

            var result = await Coordinator().Submit(release, client.Object, null);

            result.State.Should().Be(MovieEditionSubmissionState.Recorded);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task a_new_profile_format_missing_from_the_cache_cannot_hide_a_definition_change()
        {
            var profile = SubmissionProfile();
            var release = SubmissionRelease();
            var format = Mocker.Resolve<CustomFormatRepository>().Insert(new CustomFormat("Synthetic Uncached Format", new ReleaseGroupSpecification { Value = "SyntheticNewGroup" }));
            // The service cache deliberately remains empty, while storage gains a scored format.
            profile.FormatItems = new() { new ProfileFormatItem { Format = format, Score = 88 } };
            Mocker.Resolve<QualityProfileRepository>().Update(profile);
            var client = SubmissionClient();
            Func<Task> submit = () => Coordinator().Submit(release, client.Object, null);

            await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*profile*before client IO*");
            _journal.Count().Should().Be(0);
            client.Verify(c => c.Download(It.IsAny<NzbDrone.Core.Parser.Model.RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }
    }
}
