using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Converters;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.Datastore.Converters
{
    public class ReleaseInfoConverterFixture
    {
        [TestCase("\"torrent\"")]
        [TestCase("\"Torrent\"")]
        [TestCase("2")]
        public void restores_torrent_fields_from_existing_json_without_a_type_discriminator(string protocol)
        {
            var json = "{\"downloadProtocol\":" + protocol + ",\"title\":\"Synthetic.Voyage.2026-SyntheticGroup\",\"infoHash\":\"synthetic-hash\",\"seeders\":17}";
            var release = new ReleaseInfoConverter().Parse(json).Should().BeOfType<TorrentInfo>().Subject;
            release.InfoHash.Should().Be("synthetic-hash");
            release.Seeders.Should().Be(17);
        }

        [TestCase("{\"downloadProtocol\":\"usenet\",\"guid\":\"synthetic-guid\"}")]
        [TestCase("{\"guid\":\"synthetic-guid\"}")]
        public void retains_legacy_usenet_and_unspecified_protocols(string json)
        {
            new ReleaseInfoConverter().Parse(json).Should().BeOfType<ReleaseInfo>().Which.Guid.Should().Be("synthetic-guid");
        }

        [Test]
        public void handles_a_json_null_like_the_existing_converter()
        {
            new ReleaseInfoConverter().Parse("null").Should().BeNull();
        }
    }
}
