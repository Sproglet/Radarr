using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    // An operation's edition snapshot. Never substitutes fields on the shared Movie object.
    public class MovieEditionContext
    {
        public MovieEdition Edition { get; }
        public QualityProfile QualityProfile { get; }
        public MovieFile CurrentFile { get; }
        public bool HasMultipleEditions { get; }

        public MovieEditionContext(MovieEdition edition, QualityProfile qualityProfile, MovieFile currentFile, bool hasMultipleEditions = false)
        {
            Edition = edition;
            QualityProfile = qualityProfile;
            CurrentFile = currentFile;
            HasMultipleEditions = hasMultipleEditions;
        }
    }
}
