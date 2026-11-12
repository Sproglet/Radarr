using System;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public class MovieGrabbedEvent : IEvent
    {
        public RemoteMovie Movie { get; private set; }
        public int DownloadClientId { get; set; }
        public string DownloadClient { get; set; }
        public string DownloadClientName { get; set; }
        public string DownloadId { get; set; }
        public string EditionOperationId { get; }
        internal DateTime? EditionGrabbedAt { get; }

        public MovieGrabbedEvent(RemoteMovie movie)
        {
            Movie = movie;
            EditionOperationId = movie.EditionContext == null ? null : Guid.NewGuid().ToString("D");
        }

        internal MovieGrabbedEvent(RemoteMovie movie, string operationId, DateTime createdAt)
            : this(movie)
        {
            if (movie.EditionContext == null || operationId == null || !MovieEditionOperationIdentity.IsValid(operationId) || createdAt.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Durable edition grabs require assigned canonical operation identity and a UTC timestamp.");
            }

            EditionOperationId = operationId;
            EditionGrabbedAt = createdAt;
        }
    }
}
