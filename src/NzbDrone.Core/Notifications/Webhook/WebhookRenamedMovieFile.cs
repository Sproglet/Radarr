using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.Webhook
{
    public class WebhookRenamedMovieFile : WebhookMovieFile
    {
        public WebhookRenamedMovieFile(RenamedMovieFile renamedMovie, NotificationMovieEdition edition = null)
            : base(renamedMovie.MovieFile, edition)
        {
            PreviousRelativePath = renamedMovie.PreviousRelativePath;
            PreviousPath = renamedMovie.PreviousPath;
        }

        public string PreviousRelativePath { get; set; }
        public string PreviousPath { get; set; }
    }
}
