using System;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    public class ReleaseIdentityFixture
    {
        private ReleaseInfo Release() => new ReleaseInfo
        {
            Title = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup", IndexerId = 1, Indexer = "Synthetic Indexer",
            Guid = "synthetic-guid", PublishDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), DownloadProtocol = DownloadProtocol.Usenet
        };

        [Test]
        public void stable_guid_survives_title_and_timestamp_changes()
        {
            var revised = Release();
            revised.Title += ".REPACK";
            revised.PublishDate = revised.PublishDate.AddHours(1);
            ReleaseIdentity.Matches(Release(), revised).Should().BeTrue();
        }

        [Test]
        public void differing_known_guids_are_distinct_even_with_identical_titles()
        {
            var other = Release();
            other.Guid += "-other";
            ReleaseIdentity.Matches(Release(), other).Should().BeFalse();
        }

        [Test]
        public void same_guid_is_scoped_to_indexer_and_protocol()
        {
            var other = Release();
            other.IndexerId++;
            ReleaseIdentity.Matches(Release(), other).Should().BeFalse();
            other = Release();
            other.DownloadProtocol = DownloadProtocol.Torrent;
            ReleaseIdentity.Matches(Release(), other).Should().BeFalse();
        }

        [Test]
        public void matching_torrent_hashes_cross_indexers_case_insensitively()
        {
            var first = new TorrentInfo { IndexerId = 1, InfoHash = "abcdef0123456789abcdef0123456789abcdef01", DownloadProtocol = DownloadProtocol.Torrent };
            var second = new TorrentInfo { IndexerId = 2, InfoHash = first.InfoHash.ToUpperInvariant(), DownloadProtocol = DownloadProtocol.Torrent };
            ReleaseIdentity.Matches(first, second).Should().BeTrue();
        }

        [Test]
        public void download_url_is_a_fallback_when_guid_is_absent()
        {
            var first = Release();
            var second = Release();
            first.Guid = second.Guid = null;
            first.DownloadUrl = second.DownloadUrl = "https://synthetic.invalid/download/1";
            second.Title += ".REPACK";
            ReleaseIdentity.Matches(first, second).Should().BeTrue();
        }

        [Test]
        public void legacy_fallback_requires_title_time_and_indexer()
        {
            var first = Release();
            var second = Release();
            first.Guid = second.Guid = null;
            first.IndexerId = second.IndexerId = 0;
            ReleaseIdentity.Matches(first, second).Should().BeTrue();
            second.PublishDate = second.PublishDate.AddSeconds(1);
            ReleaseIdentity.Matches(first, second).Should().BeFalse();
            ReleaseIdentity.Matches(new ReleaseInfo(), new ReleaseInfo()).Should().BeFalse();
        }

        [Test]
        public void pending_reference_is_neither_serialized_nor_accepted_from_json()
        {
            var release = Release();
            release.PendingReleaseId = 42;
            JsonSerializer.Serialize(release).Should().NotContain("PendingReleaseId");
            JsonSerializer.Deserialize<ReleaseInfo>("{\"PendingReleaseId\":42}").PendingReleaseId.Should().BeNull();
        }

        [Test]
        public void legacy_identity_survives_persisted_timestamp_precision()
        {
            var release = Release();
            release.Guid = null;
            release.PublishDate = release.PublishDate.AddMilliseconds(123);
            ReleaseIdentity.Matches(release, release.JsonClone()).Should().BeTrue();
        }
    }
}
