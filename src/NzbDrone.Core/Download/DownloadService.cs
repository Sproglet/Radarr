using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface IDownloadService
    {
        Task DownloadReport(RemoteMovie remoteMovie, int? downloadClientId);
    }

    public class DownloadService : IDownloadService
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly IRateLimitService _rateLimitService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ISeedConfigProvider _seedConfigProvider;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieRepository _movies;
        private readonly Logger _logger;

        public DownloadService(IProvideDownloadClient downloadClientProvider,
                               IDownloadClientStatusService downloadClientStatusService,
                               IIndexerFactory indexerFactory,
                               IIndexerStatusService indexerStatusService,
                               IRateLimitService rateLimitService,
                               IEventAggregator eventAggregator,
                               ISeedConfigProvider seedConfigProvider,
                               IMovieEditionRepository editions,
                               IMovieRepository movies,
                               Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _downloadClientStatusService = downloadClientStatusService;
            _indexerFactory = indexerFactory;
            _indexerStatusService = indexerStatusService;
            _rateLimitService = rateLimitService;
            _eventAggregator = eventAggregator;
            _seedConfigProvider = seedConfigProvider;
            _editions = editions;
            _movies = movies;
            _logger = logger;
        }

        public async Task DownloadReport(RemoteMovie remoteMovie, int? downloadClientId)
        {
            Ensure.That(remoteMovie.Movie, () => remoteMovie.Movie).IsNotNull();
            var snapshot = new AcquisitionSnapshot(remoteMovie.Movie);
            using var acquisition = await MovieEditionAcquisitionGate.AcquireAsync(snapshot.MovieId);
            ValidateAcquisition(remoteMovie, snapshot);

            var filterBlockedClients = remoteMovie.Release.PendingReleaseReason == PendingReleaseReason.DownloadClientUnavailable;

            var tags = remoteMovie.Movie.Tags;

            var downloadClient = downloadClientId.HasValue
                ? _downloadClientProvider.Get(downloadClientId.Value)
                : _downloadClientProvider.GetDownloadClient(remoteMovie.Release.DownloadProtocol, remoteMovie.Release.IndexerId, filterBlockedClients, tags);

            var grabbed = await DownloadReport(remoteMovie, downloadClient, snapshot);
            // Client submission is complete. Release before notifications/scripts, which may call the API.
            acquisition.Dispose();
            _eventAggregator.PublishEvent(grabbed);
        }

        private void ValidateAcquisition(RemoteMovie remoteMovie, AcquisitionSnapshot snapshot)
        {
            if (!snapshot.Matches(_movies.Find(snapshot.MovieId)) || !snapshot.Matches(remoteMovie.Movie))
            {
                throw new InvalidOperationException("The movie changed after release evaluation. Refresh the release before submitting it.");
            }

            if (MovieEditionAcquisitionGuard.RequiresEditionContext(remoteMovie, _editions.GetByMovie(snapshot.MovieId)))
            {
                throw new InvalidOperationException("Multi-edition acquisition is disabled. Import files with explicit edition assignments instead.");
            }
        }

        private async Task<MovieGrabbedEvent> DownloadReport(RemoteMovie remoteMovie, IDownloadClient downloadClient, AcquisitionSnapshot snapshot)
        {
            Ensure.That(remoteMovie.Movie, () => remoteMovie.Movie).IsNotNull();

            var downloadTitle = remoteMovie.Release.Title;

            if (downloadClient == null)
            {
                throw new DownloadClientUnavailableException($"{remoteMovie.Release.DownloadProtocol} Download client isn't configured yet");
            }

            // Get the seed configuration for this release.
            remoteMovie.SeedConfiguration = _seedConfigProvider.GetSeedConfiguration(remoteMovie);

            // Limit grabs to 2 per second.
            if (remoteMovie.Release.DownloadUrl.IsNotNullOrWhiteSpace() && !remoteMovie.Release.DownloadUrl.StartsWith("magnet:"))
            {
                var url = new HttpUri(remoteMovie.Release.DownloadUrl);
                await _rateLimitService.WaitAndPulseAsync(url.Host, TimeSpan.FromSeconds(2));
            }

            IIndexer indexer = null;

            if (remoteMovie.Release.IndexerId > 0)
            {
                indexer = _indexerFactory.GetInstance(_indexerFactory.Get(remoteMovie.Release.IndexerId));
            }

            string downloadClientId;
            try
            {
                // A rate-limit wait can outlive changes made by legacy writers or
                // another process. Re-read ownership/preferences before client IO.
                ValidateAcquisition(remoteMovie, snapshot);
                downloadClientId = await downloadClient.Download(remoteMovie, indexer);
                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
                _indexerStatusService.RecordSuccess(remoteMovie.Release.IndexerId);
            }
            catch (ReleaseUnavailableException)
            {
                _logger.Trace("Release {0} no longer available on indexer.", remoteMovie);
                throw;
            }
            catch (ReleaseBlockedException)
            {
                _logger.Trace("Release {0} previously added to blocklist, not sending to download client again.", remoteMovie);
                throw;
            }
            catch (DownloadClientRejectedReleaseException)
            {
                _logger.Trace("Release {0} rejected by download client, possible duplicate.", remoteMovie);
                throw;
            }
            catch (ReleaseDownloadException ex)
            {
                if (ex.InnerException is TooManyRequestsException http429)
                {
                    _indexerStatusService.RecordFailure(remoteMovie.Release.IndexerId, http429.RetryAfter);
                }
                else
                {
                    _indexerStatusService.RecordFailure(remoteMovie.Release.IndexerId);
                }

                throw;
            }

            var movieGrabbedEvent = new MovieGrabbedEvent(remoteMovie);
            movieGrabbedEvent.DownloadClient = downloadClient.Name;
            movieGrabbedEvent.DownloadClientId = downloadClient.Definition.Id;
            movieGrabbedEvent.DownloadClientName = downloadClient.Definition.Name;

            if (downloadClientId.IsNotNullOrWhiteSpace())
            {
                movieGrabbedEvent.DownloadId = downloadClientId;
            }

            _logger.ProgressInfo("Report for {0} ({1}) sent to {2} from indexer {3}. {4}", remoteMovie.Movie.Title, remoteMovie.Movie.Year, downloadClient.Definition.Name, remoteMovie.Release.Indexer, downloadTitle);
            return movieGrabbedEvent;
        }

        private sealed class AcquisitionSnapshot
        {
            public int MovieId { get; }
            private readonly int _metadataId;
            private readonly int _profileId;
            private readonly int? _primaryId;
            private readonly int _fileId;
            private readonly bool _monitored;
            private readonly string _path;
            private readonly HashSet<int> _tags;

            public AcquisitionSnapshot(Movie movie)
            {
                MovieId = movie.Id;
                _metadataId = movie.MovieMetadataId;
                _profileId = movie.QualityProfileId;
                _primaryId = movie.PrimaryEditionId;
                _fileId = movie.MovieFileId;
                _monitored = movie.Monitored;
                _path = movie.Path;
                _tags = movie.Tags == null ? new HashSet<int>() : new HashSet<int>(movie.Tags);
            }

            public bool Matches(Movie movie) => movie != null && movie.Id == MovieId && movie.MovieMetadataId == _metadataId &&
                movie.QualityProfileId == _profileId && movie.PrimaryEditionId == _primaryId && movie.MovieFileId == _fileId &&
                movie.Monitored == _monitored && movie.Path == _path && _tags.SetEquals(movie.Tags ?? new HashSet<int>());
        }
    }
}
