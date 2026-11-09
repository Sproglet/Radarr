using System;

namespace NzbDrone.Core.Parser.Model
{
    // Release identity is independent of the movie/edition competing for it.
    public static class ReleaseIdentity
    {
        public static bool Matches(ReleaseInfo left, ReleaseInfo right)
        {
            if (left == null || right == null || left.DownloadProtocol != right.DownloadProtocol)
            {
                return false;
            }

            if (left is TorrentInfo a && right is TorrentInfo b && Present(a.InfoHash) && Present(b.InfoHash))
            {
                return string.Equals(a.InfoHash, b.InfoHash, StringComparison.OrdinalIgnoreCase);
            }

            var sameIndexer = left.IndexerId > 0 && right.IndexerId > 0
                ? left.IndexerId == right.IndexerId
                : Present(left.Indexer) && left.Indexer == right.Indexer;
            if (!sameIndexer)
            {
                return false;
            }

            if (Present(left.Guid) && Present(right.Guid))
            {
                return left.Guid == right.Guid;
            }

            if (Present(left.DownloadUrl) && Present(right.DownloadUrl))
            {
                return left.DownloadUrl == right.DownloadUrl;
            }

            // Persisted release timestamps use whole seconds in STJUtcConverter.
            return Present(left.Title) && left.Title == right.Title &&
                left.PublishDate.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond == right.PublishDate.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond;
        }

        private static bool Present(string value) => !string.IsNullOrWhiteSpace(value);
    }
}
