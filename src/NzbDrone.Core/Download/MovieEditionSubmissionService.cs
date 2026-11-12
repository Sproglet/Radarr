using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Download
{
    // Submission primitive only. The public download entry point remains acquisition-disabled for editions.
    public interface IMovieEditionSubmissionService
    {
        Task<MovieEditionSubmission> Submit(RemoteMovie remote, IDownloadClient client, IIndexer indexer);
        MovieEditionSubmission Reconcile(string operationId);
    }

    public class MovieEditionSubmissionService : IMovieEditionSubmissionService
    {
        private readonly IMovieEditionSubmissionRepository _journal;
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionRepository _editions;
        private readonly IQualityProfileRepository _profiles;
        private readonly IMediaFileRepository _files;

        public MovieEditionSubmissionService(
            IMovieEditionSubmissionRepository journal,
            IMovieRepository movies,
            IMovieEditionRepository editions,
            IQualityProfileRepository profiles,
            IMediaFileRepository files)
        {
            _journal = journal;
            _movies = movies;
            _editions = editions;
            _profiles = profiles;
            _files = files;
        }

        public async Task<MovieEditionSubmission> Submit(RemoteMovie remote, IDownloadClient client, IIndexer indexer)
        {
            if (remote?.Movie == null || remote.EditionContext == null || remote.EditionRecoveryError != null || remote.Release == null || remote.ParsedMovieInfo == null ||
                client?.Definition == null || client.Definition.Id <= 0)
            {
                throw new InvalidOperationException("Submission requires an explicitly assigned release and configured client.");
            }

            var movieId = remote.Movie.Id;
            var target = remote.EditionContext.Edition;
            using var acquisition = await MovieEditionAcquisitionGate.AcquireAsync(movieId);
            if (_journal.HasUnsettled(movieId, target.Id))
            {
                throw new ModelConflictException(typeof(MovieEdition), target.Id, "has unresolved submission intent; reconcile it before submitting again.");
            }

            var parentProfile = remote.Movie.QualityProfileId;
            var parentPrimary = remote.Movie.PrimaryEditionId;
            var parentFile = remote.Movie.MovieFileId;
            var metadataId = remote.Movie.MovieMetadataId;
            var tags = new HashSet<int>(remote.Movie.Tags ?? new());
            var seed = remote.SeedConfiguration == null ? null : new TorrentSeedConfiguration
            {
                Ratio = remote.SeedConfiguration.Ratio, SeedTime = remote.SeedConfiguration.SeedTime
            };
            var prepared = _journal.Prepare(new MovieEditionSubmission
            {
                MovieId = movieId, MovieEditionId = target.Id, EditionRuleRevision = target.RuleRevision,
                EditionName = target.Name, EditionAttributes = new(target.Attributes), QualityProfileId = target.QualityProfileId,
                MovieFileId = target.MovieFileId, MoviePath = remote.Movie.Path, DownloadClientId = client.Definition.Id,
                DownloadClientName = client.Definition.Name, DownloadClient = client.Name, Release = remote.Release,
                ParsedMovieInfo = remote.ParsedMovieInfo, Languages = new(remote.Languages), CustomFormatScore = remote.CustomFormatScore,
                MovieMatchType = remote.MovieMatchType, ReleaseSource = remote.ReleaseSource
            });

            // Reload the committed payload; client IO must not depend on mutable caller objects.
            prepared = _journal.Get(prepared.Id);
            var movie = _movies.Get(movieId);
            var edition = _editions.Get(prepared.MovieEditionId);
            if (!movie.Monitored || !edition.Monitored || movie.Path != prepared.MoviePath ||
                movie.MovieMetadataId != metadataId || movie.QualityProfileId != parentProfile || movie.PrimaryEditionId != parentPrimary ||
                movie.MovieFileId != parentFile || !tags.SetEquals(movie.Tags ?? new()) ||
                edition.MovieId != movieId || edition.MovieFileId != prepared.MovieFileId || edition.QualityProfileId != prepared.QualityProfileId ||
                edition.RuleRevision != prepared.EditionRuleRevision || edition.Name != prepared.EditionName ||
                edition.Attributes.Count != prepared.EditionAttributes.Count || edition.Attributes.Any(a => !prepared.EditionAttributes.TryGetValue(a.Key, out var value) || value != a.Value) ||
                client.Definition.Id != prepared.DownloadClientId || client.Definition.Name != prepared.DownloadClientName || client.Name != prepared.DownloadClient)
            {
                throw new InvalidOperationException("Submission context changed before client IO; retain prepared intent for review.");
            }

            var profile = _profiles.Get(prepared.QualityProfileId);
            var file = prepared.MovieFileId.HasValue ? _files.Get(prepared.MovieFileId.Value) : null;
            if (file != null && (file.MovieId != movieId || file.MovieEditionId != edition.Id))
            {
                throw new InvalidOperationException("Current-file ownership changed before client IO.");
            }

            var frozen = new RemoteMovie
            {
                Movie = movie, Release = prepared.Release, ParsedMovieInfo = prepared.ParsedMovieInfo,
                Languages = prepared.Languages, CustomFormatScore = prepared.CustomFormatScore, MovieMatchType = prepared.MovieMatchType,
                ReleaseSource = prepared.ReleaseSource, SeedConfiguration = seed,
                EditionContext = new MovieEditionContext(edition, profile, file, true)
            };

            // Exceptions/empty IDs can follow an accepted request. Never clear or retry prepared intent on uncertainty.
            var downloadId = await client.Download(frozen, indexer);
            _journal.Accept(prepared.OperationId, downloadId);
            return _journal.RecordGrabs(prepared.OperationId);
        }

        public MovieEditionSubmission Reconcile(string operationId)
        {
            var intent = _journal.FindByOperationId(operationId) ?? throw new InvalidOperationException("Unknown submission operation.");
            if (intent.State == MovieEditionSubmissionState.Settled)
            {
                return intent;
            }

            // Reconciliation has no client submission dependency and never repeats network IO.
            return _journal.RecordGrabs(operationId);
        }
    }
}
