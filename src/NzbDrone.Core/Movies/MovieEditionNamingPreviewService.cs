using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public class MovieEditionNamingSample
    {
        public int? MovieEditionId { get; set; }
        public string Name { get; set; }
        public string RelativePath { get; set; }
    }

    public class MovieEditionNamingPreview
    {
        public MovieEdition ProposedEdition { get; set; }
        public string NamingFormat { get; set; }
        public int? ReferenceMovieFileId { get; set; }
        public List<MovieEditionNamingSample> Samples { get; set; } = new();
        public List<string> Rejections { get; set; } = new();
        public bool NamingValid => Rejections.Count == 0 && Samples.Count > 0;
        public bool CreationEnabled => NamingValid && ProposedEdition?.Id == 0;
        public bool EditingEnabled => NamingValid && ProposedEdition?.Id > 0;
    }

    public interface IMovieEditionNamingPreviewService
    {
        MovieEditionNamingPreview Preview(MovieEdition proposal);
        MovieEditionNamingPreview PreviewEdit(MovieEdition proposal);
    }

    // A naming prerequisite only: never creates an edition, refreshes media info or reserves a destination.
    public class MovieEditionNamingPreviewService : IMovieEditionNamingPreviewService
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieEditionContextService _contexts;
        private readonly IMediaFileRepository _files;
        private readonly IQualityProfileRepository _profiles;
        private readonly INamingConfigService _naming;
        private readonly IBuildFileNames _names;
        private readonly ICustomFormatCalculationService _formats;

        public MovieEditionNamingPreviewService(IMovieRepository movies, IMovieEditionRepository editions,
            IMovieEditionContextService contexts, IMediaFileRepository files, IQualityProfileRepository profiles,
            INamingConfigService naming, IBuildFileNames names, ICustomFormatCalculationService formats)
        {
            _movies = movies;
            _editions = editions;
            _contexts = contexts;
            _files = files;
            _profiles = profiles;
            _naming = naming;
            _names = names;
            _formats = formats;
        }

        public MovieEditionNamingPreview Preview(MovieEdition proposal)
        {
            return Preview(proposal, false);
        }

        public MovieEditionNamingPreview PreviewEdit(MovieEdition proposal)
        {
            if (proposal?.Id <= 0 || proposal == null)
            {
                throw new ArgumentException("Select an existing edition for the definition preview.");
            }

            return Preview(proposal, true);
        }

        private MovieEditionNamingPreview Preview(MovieEdition proposal, bool editing)
        {
            if (proposal == null || proposal.MovieId <= 0 || string.IsNullOrWhiteSpace(proposal.Name) || proposal.Name.Any(char.IsControl) || proposal.Name.Trim().Length > 120 || proposal.QualityProfileId < 0)
            {
                throw new ArgumentException("Provide a positive movie ID, a name of up to 120 characters and a valid profile ID.");
            }

            var attributes = new Dictionary<string, string>(proposal.Attributes ?? new(), StringComparer.OrdinalIgnoreCase);
            if (!attributes.ContainsKey("cut"))
            {
                attributes.Add("cut", "theatrical");
            }

            if (attributes.Count > 32 || attributes.Any(a => string.IsNullOrWhiteSpace(a.Key) || a.Key.Length > 64 || string.IsNullOrWhiteSpace(a.Value) || a.Value.Length > 128))
            {
                throw new ArgumentException("Edition attributes require at most 32 nonempty keys/values, with keys up to 64 and values up to 128 characters.");
            }

            var rules = new List<string>(proposal.MatchRules ?? new());
            MovieEditionClassifier.ValidateRules(rules);
            using var lease = MovieFileOperationLock.Acquire(proposal.MovieId);
            var movie = _movies.Find(proposal.MovieId) ?? throw new ModelNotFoundException(typeof(Movie), proposal.MovieId);
            var profile = _profiles.Get(proposal.QualityProfileId == 0 ? movie.QualityProfileId : proposal.QualityProfileId);
            var editions = _editions.GetByMovie(movie.Id).OrderByDescending(e => e.Id == movie.PrimaryEditionId).ThenBy(e => e.Id).ToList();
            var original = editing ? editions.SingleOrDefault(e => e.Id == proposal.Id) : null;
            if (editing && original == null)
            {
                throw new ModelNotFoundException(typeof(MovieEdition), proposal.Id);
            }

            if (editing && profile.Id != original.QualityProfileId)
            {
                throw new ArgumentException("Change the edition profile separately before editing its definition.");
            }
            var proposed = new MovieEdition
            {
                Id = original?.Id ?? 0, MovieId = movie.Id, Name = proposal.Name.Trim(), QualityProfileId = profile.Id,
                Attributes = attributes, MatchRules = rules, Monitored = original?.Monitored ?? false,
                EditionKey = original?.EditionKey, MovieFileId = original?.MovieFileId,
                LastSearchTime = original?.LastSearchTime, RuleRevision = original?.RuleRevision ?? 1
            };
            var config = _naming.GetConfig();
            var result = new MovieEditionNamingPreview { ProposedEdition = proposed, NamingFormat = config.StandardMovieFormat };
            if (editions.Any(e => e.Id != proposed.Id && string.Equals(e.Name, proposed.Name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Rejections.Add("An edition with this name already exists for the movie.");
            }
            if (!config.RenameMovies || !(config.StandardMovieFormat?.Contains("{Managed Edition}", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                result.Rejections.Add("Enable movie renaming and use the full {Managed Edition} token without truncation before adding another edition.");
                return result;
            }

            var contexts = new List<MovieEditionContext>();
            foreach (var edition in editions)
            {
                try
                {
                    contexts.Add(_contexts.Resolve(movie, edition.Id));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ModelNotFoundException)
                {
                    result.Rejections.Add($"Repair edition '{edition.Name}' before previewing another target: {ex.Message}");
                }
            }

            var source = contexts.FirstOrDefault(c => c.Edition.Id == proposed.Id)?.CurrentFile ?? contexts.Select(c => c.CurrentFile).FirstOrDefault(f => f != null);
            if (source == null)
            {
                result.Rejections.Add("Import a first edition file before previewing additional edition filenames.");
                return result;
            }

            result.ReferenceMovieFileId = source.Id;
            var extension = Path.GetExtension(source.RelativePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                result.Rejections.Add("Repair the reference file's extension before previewing another target.");
                return result;
            }
            // The detached movie has no path, so FileNameBuilder cannot trigger media-info refreshes.
            var previewMovie = new Movie { Id = movie.Id, MovieMetadataId = movie.MovieMetadataId, MovieMetadata = movie.MovieMetadata };
            var knownFiles = _files.GetFilesByMovie(movie.Id);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edition in editions.Where(e => e.Id != proposed.Id).Concat(new[] { proposed }))
            {
                var editionProfile = edition == proposed ? profile : contexts.FirstOrDefault(c => c.Edition.Id == edition.Id)?.QualityProfile;
                if (editionProfile == null)
                {
                    continue;
                }

                var reference = contexts.FirstOrDefault(c => c.Edition.Id == edition.Id)?.CurrentFile ?? source;
                var formats = _formats.ParseCustomFormat(reference, movie);
                var fileExtension = Path.GetExtension(reference.RelativePath);
                if (string.IsNullOrWhiteSpace(fileExtension))
                {
                    result.Rejections.Add($"Repair edition '{edition.Name}' file extension before previewing filenames.");
                    continue;
                }

                var sample = new MovieFile
                {
                    MovieId = movie.Id, RelativePath = reference.RelativePath, SceneName = reference.SceneName,
                    OriginalFilePath = reference.OriginalFilePath, Quality = reference.Quality, MediaInfo = reference.MediaInfo,
                    Languages = reference.Languages, ReleaseGroup = reference.ReleaseGroup, Edition = reference.Edition,
                    EditionContext = new MovieEditionContext(edition, editionProfile, null)
                };
                var labelConfig = new NamingConfig
                {
                    RenameMovies = true, ReplaceIllegalCharacters = config.ReplaceIllegalCharacters,
                    ColonReplacementFormat = config.ColonReplacementFormat, StandardMovieFormat = "{Managed Edition}"
                };
                if (string.IsNullOrWhiteSpace(_names.BuildFileName(previewMovie, sample, labelConfig, formats)))
                {
                    result.Rejections.Add($"Edition '{edition.Name}' has no usable filename label after sanitization.");
                }

                var fileName = _names.BuildFileName(previewMovie, sample, config, formats);
                var relativePath = fileName + fileExtension;
                var key = Path.ChangeExtension(relativePath.Replace('\\', '/'), null);
                if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(relativePath) || relativePath.Replace('\\', '/').Split('/').Any(p => p == ".."))
                {
                    result.Rejections.Add($"Edition '{edition.Name}' has an invalid preview destination.");
                }
                else if (!destinations.Add(key))
                {
                    result.Rejections.Add("Edition names produce the same destination after formatting/sanitization. Choose distinct names.");
                }

                if (edition == proposed && knownFiles.Any(f => f.Id != (editing ? original.MovieFileId : null) &&
                    string.Equals(Path.ChangeExtension(f.RelativePath.Replace('\\', '/'), null), key, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Rejections.Add("The proposed destination conflicts with an existing file record, including its sidecar basename.");
                }

                result.Samples.Add(new MovieEditionNamingSample { MovieEditionId = edition.Id == 0 ? null : edition.Id, Name = edition.Name, RelativePath = relativePath });
            }

            return result;
        }
    }
}
