using System;
using System.Text.Json;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Datastore.Converters
{
    public class ReleaseInfoConverter : EmbeddedDocumentConverter<ReleaseInfo>
    {
        public override ReleaseInfo Parse(object value)
        {
            using var document = JsonDocument.Parse((string)value);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals("downloadProtocol", StringComparison.OrdinalIgnoreCase) &&
                        ((property.Value.ValueKind == JsonValueKind.String && property.Value.GetString().Equals("torrent", StringComparison.OrdinalIgnoreCase)) ||
                         (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var protocol) && protocol == (int)DownloadProtocol.Torrent)))
                    {
                        return JsonSerializer.Deserialize<TorrentInfo>((string)value, SerializerSettings);
                    }
                }
            }

            return base.Parse(value);
        }
    }
}
