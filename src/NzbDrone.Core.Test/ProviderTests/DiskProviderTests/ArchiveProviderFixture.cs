using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.ProviderTests.DiskProviderTests
{
    [TestFixture]
    public class ArchiveProviderFixture : TestBase<ArchiveService>
    {
        [TestCase("zip")]
        [TestCase("tar.gz")]
        public void Should_extract_to_correct_folder(string format)
        {
            var source = Path.Combine(TempFolder, "SyntheticArchiveSource");
            var expected = new Dictionary<string, string>();
            foreach (var subfolder in new[] { "", "SyntheticAlpha", "SyntheticBeta" })
            {
                var relative = Path.Combine("SyntheticArchive", subfolder);
                Directory.CreateDirectory(Path.Combine(source, relative));
                for (var number = 1; number <= 2; number++)
                {
                    var file = Path.Combine(relative, $"SyntheticFile{number}.txt");
                    var content = $"Synthetic archive payload: {file}\n";
                    File.WriteAllText(Path.Combine(source, file), content);
                    expected.Add(file, content);
                }
            }

            var archive = GetTempFilePath() + "." + format;
            if (format == "zip")
            {
                ZipFile.CreateFromDirectory(source, archive);
            }
            else
            {
                using var output = File.Create(archive);
                using var gzip = new GZipStream(output, CompressionLevel.Fastest);
                using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
                foreach (var file in expected.Keys)
                {
                    tar.WriteEntry(Path.Combine(source, file), file.Replace(Path.DirectorySeparatorChar, '/'));
                }
            }

            var destinationFolder = new DirectoryInfo(GetTempFilePath());
            Subject.Extract(archive, destinationFolder.FullName);

            destinationFolder.Exists.Should().BeTrue();
            destinationFolder.GetDirectories().Should().HaveCount(1);
            destinationFolder.GetDirectories("*", SearchOption.AllDirectories).Should().HaveCount(3);
            destinationFolder.GetFiles("*.*", SearchOption.AllDirectories).Should().HaveCount(6);
            foreach (var file in expected)
            {
                File.ReadAllText(Path.Combine(destinationFolder.FullName, file.Key)).Should().Be(file.Value);
            }
        }
    }
}
