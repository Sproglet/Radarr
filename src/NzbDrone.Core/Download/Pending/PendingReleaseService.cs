using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.Download.Pending
{
    public interface IPendingReleaseService
    {
        void Add(DownloadDecision decision, PendingReleaseReason reason);
        void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions);
        List<ReleaseInfo> GetPending();
        RemoteMovie GetPendingRemoteMovie(ReleaseInfo release);
        List<RemoteMovie> GetPendingRemoteMovies(int movieId);
        List<Queue.Queue> GetPendingQueue();
        Queue.Queue FindPendingQueueItem(int queueId);
        void RemovePendingQueueItems(int queueId);
        RemoteMovie OldestPendingRelease(int movieId);
        RemoteMovie OldestPendingReleaseForEdition(int movieId, int editionId);
    }

    public class PendingReleaseService : IPendingReleaseService,
                                         IHandle<MovieGrabbedEvent>,
                                         IHandle<MoviesDeletedEvent>,
                                         IHandle<RssSyncCompleteEvent>
    {
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly IPendingReleaseRepository _repository;
        private readonly IMovieService _movieService;
        private readonly IParsingService _parsingService;
        private readonly IDelayProfileService _delayProfileService;
        private readonly ITaskManager _taskManager;
        private readonly IConfigService _configService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IMovieEditionRecoveryService _editionRecovery;

        public PendingReleaseService(IIndexerStatusService indexerStatusService,
                                     IPendingReleaseRepository repository,
                                     IMovieService movieService,
                                     IParsingService parsingService,
                                     IDelayProfileService delayProfileService,
                                     ITaskManager taskManager,
                                     IConfigService configService,
                                     ICustomFormatCalculationService formatCalculator,
                                     IRemoteMovieAggregationService aggregationService,
                                     IDownloadClientFactory downloadClientFactory,
                                     IIndexerFactory indexerFactory,
                                     IEventAggregator eventAggregator,
                                     IMovieEditionRecoveryService editionRecovery,
                                     Logger logger)
        {
            _indexerStatusService = indexerStatusService;
            _repository = repository;
            _movieService = movieService;
            _parsingService = parsingService;
            _delayProfileService = delayProfileService;
            _taskManager = taskManager;
            _configService = configService;
            _formatCalculator = formatCalculator;
            _aggregationService = aggregationService;
            _downloadClientFactory = downloadClientFactory;
            _indexerFactory = indexerFactory;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _editionRecovery = editionRecovery;
        }

        public void Add(DownloadDecision decision, PendingReleaseReason reason)
        {
            AddMany(new List<Tuple<DownloadDecision, PendingReleaseReason>> { Tuple.Create(decision, reason) });
        }

        public void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions)
        {
            if (decisions.Any(a => decisions.Any(b => ReleaseIdentity.Matches(a.Item1.RemoteMovie.Release, b.Item1.RemoteMovie.Release) &&
                (a.Item1.RemoteMovie.Movie.Id != b.Item1.RemoteMovie.Movie.Id || a.Item1.RemoteMovie.EditionContext?.Edition.Id != b.Item1.RemoteMovie.EditionContext?.Edition.Id))))
            {
                throw new InvalidOperationException("The pending batch assigns the same release to multiple targets; manual review required.");
            }

            foreach (var movieDecisions in decisions.GroupBy(v => v.Item1.RemoteMovie.Movie.Id))
            {
                var movie = movieDecisions.First().Item1.RemoteMovie.Movie;
                var alreadyPending = _repository.AllByMovieId(movie.Id) ?? new List<PendingRelease>();
                if (movieDecisions.Any(d => alreadyPending.Any(p => ReleaseIdentity.Matches(p.Release, d.Item1.RemoteMovie.Release) &&
                    p.MovieEditionId != d.Item1.RemoteMovie.EditionContext?.Edition.Id)))
                {
                    throw new InvalidOperationException("The release is already pending for a different or unassigned edition. Review the saved assignment.");
                }

                foreach (var pair in movieDecisions)
                {
                    var decision = pair.Item1;
                    var reason = pair.Item2;

                    var existingReports = alreadyPending ?? Enumerable.Empty<PendingRelease>();

                    var matchingReports = existingReports.Where(MatchingReleasePredicate(decision.RemoteMovie.Release)).ToList();

                    if (matchingReports.Any())
                    {
                        var matchingReport = matchingReports.First();

                        if (matchingReport.Reason != reason)
                        {
                            if (matchingReport.Reason == PendingReleaseReason.DownloadClientUnavailable)
                            {
                                _logger.Debug("The release {0} is already pending with reason {1}, not changing reason", decision.RemoteMovie, matchingReport.Reason);
                            }
                            else
                            {
                                _logger.Debug("The release {0} is already pending with reason {1}, changing to {2}", decision.RemoteMovie, matchingReport.Reason, reason);
                                matchingReport.Reason = reason;
                                _repository.Update(matchingReport);
                            }
                        }
                        else
                        {
                            _logger.Debug("The release {0} is already pending with reason {1}, not adding again", decision.RemoteMovie, reason);
                        }

                        if (matchingReports.Count > 1)
                        {
                            _logger.Debug("The release {0} had {1} duplicate pending, removing duplicates.", decision.RemoteMovie, matchingReports.Count - 1);

                            foreach (var duplicate in matchingReports.Skip(1))
                            {
                                _repository.Delete(duplicate.Id);
                                alreadyPending.Remove(duplicate);
                            }
                        }

                        continue;
                    }

                    _logger.Debug("Adding release {0} to pending releases with reason {1}", decision.RemoteMovie, reason);
                    alreadyPending.Add(Insert(decision, reason));
                }
            }
        }

        public List<ReleaseInfo> GetPending()
        {
            var releases = _repository.All().Select(p =>
            {
                var release = p.Release;

                release.PendingReleaseReason = p.Reason;
                release.PendingReleaseId = p.Id;

                return release;
            }).ToList();

            if (releases.Any())
            {
                releases = FilterBlockedIndexers(releases);
            }

            return releases;
        }

        public RemoteMovie GetPendingRemoteMovie(ReleaseInfo release)
        {
            var stored = release.PendingReleaseId.HasValue ? _repository.Find(release.PendingReleaseId.Value) : null;
            if (stored == null || !ReleaseIdentity.Matches(stored.Release, release))
            {
                return new RemoteMovie { Release = release, EditionRecoveryError = "Pending release changed or was removed; retry RSS evaluation." };
            }

            var remote = IncludeRemoteMovies(new List<PendingRelease> { stored }).SingleOrDefault()?.RemoteMovie;
            if (remote == null)
            {
                return new RemoteMovie { Release = release, EditionRecoveryError = "Pending movie no longer exists; manual review required." };
            }

            if (_repository.AllByMovieId(stored.MovieId).Any(p => p.MovieEditionId != stored.MovieEditionId && ReleaseIdentity.Matches(p.Release, stored.Release)))
            {
                remote.EditionRecoveryError = "The same release has conflicting pending edition assignments; manual review required.";
            }

            remote.Release = release;
            remote.EditionEvaluation = new MovieEditionEvaluation { Classification = new EditionClassification { RuleRevision = stored.EditionRuleRevision } };
            return remote;
        }

        private List<ReleaseInfo> FilterBlockedIndexers(List<ReleaseInfo> releases)
        {
            var blockedIndexers = new HashSet<int>(_indexerStatusService.GetBlockedProviders().Select(v => v.ProviderId));

            return releases.Where(release => !blockedIndexers.Contains(release.IndexerId)).ToList();
        }

        public List<RemoteMovie> GetPendingRemoteMovies(int movieId)
        {
            return IncludeRemoteMovies(_repository.AllByMovieId(movieId)).Select(v => v.RemoteMovie)
                .Where(r => r.EditionRecoveryError == null).ToList();
        }

        public List<Queue.Queue> GetPendingQueue()
        {
            var queued = new List<Queue.Queue>();

            var nextRssSync = new Lazy<DateTime>(() => _taskManager.GetNextExecution(typeof(RssSyncCommand)));

            var pendingReleases = IncludeRemoteMovies(_repository.WithoutFallback());

            foreach (var pendingRelease in pendingReleases)
            {
                if (pendingRelease.RemoteMovie.Movie == null)
                {
                    var noMovieItem = GetQueueItem(pendingRelease, nextRssSync, null);

                    noMovieItem.ErrorMessage = "Unable to find matching movie(s)";

                    queued.Add(noMovieItem);

                    continue;
                }

                queued.Add(GetQueueItem(pendingRelease, nextRssSync, pendingRelease.RemoteMovie.Movie));
            }

            // Return the best quality release for each edition
            var deduped = queued.Where(q => q.Movie != null && q.RemoteMovie.EditionRecoveryError == null)
                .GroupBy(q => (q.Movie.Id, q.RemoteMovie.EditionContext?.Edition.Id)).Select(g =>
            {
                var profile = g.First().RemoteMovie.EffectiveQualityProfile;

                return g.OrderByDescending(e => e.Quality, new QualityModelComparer(profile))
                        .ThenBy(q => PrioritizeDownloadProtocol(q.Movie, q.Protocol))
                        .First();
            });

            return deduped.Concat(queued.Where(q => q.Movie == null || q.RemoteMovie.EditionRecoveryError != null)).ToList();
        }

        public Queue.Queue FindPendingQueueItem(int queueId)
        {
            return GetPendingQueue().SingleOrDefault(p => p.Id == queueId);
        }

        public void RemovePendingQueueItems(int queueId)
        {
            var targetItem = FindPendingRelease(queueId);
            var movieReleases = _repository.AllByMovieId(targetItem.MovieId);

            var releasesToRemove = movieReleases.Where(c => c.MovieEditionId == targetItem.MovieEditionId &&
                c.ParsedMovieInfo.PrimaryMovieTitle == targetItem.ParsedMovieInfo.PrimaryMovieTitle);

            _repository.DeleteMany(releasesToRemove.Select(c => c.Id));
        }

        public RemoteMovie OldestPendingRelease(int movieId)
        {
            var movieReleases = GetPendingReleases(movieId);

            return movieReleases.Select(r => r.RemoteMovie).Where(r => r.EditionRecoveryError == null)
                                 .MaxBy(p => p.Release.AgeHours);
        }

        public RemoteMovie OldestPendingReleaseForEdition(int movieId, int editionId)
        {
            return IncludeRemoteMovies(_repository.AllByEdition(movieId, editionId))
                .Where(p => p.RemoteMovie.EditionRecoveryError == null)
                .Select(p => p.RemoteMovie).MaxBy(p => p.Release.AgeHours);
        }

        private List<PendingRelease> GetPendingReleases()
        {
            return IncludeRemoteMovies(_repository.All().ToList());
        }

        private List<PendingRelease> GetPendingReleases(int movieId)
        {
            return IncludeRemoteMovies(_repository.AllByMovieId(movieId).ToList());
        }

        private List<PendingRelease> IncludeRemoteMovies(List<PendingRelease> releases, Dictionary<string, RemoteMovie> knownRemoteMovies = null)
        {
            var result = new List<PendingRelease>();

            var movieMap = new Dictionary<int, Movie>();

            if (knownRemoteMovies != null)
            {
                foreach (var movie in knownRemoteMovies.Values.Select(v => v.Movie))
                {
                    movieMap.TryAdd(movie.Id, movie);
                }
            }

            foreach (var movie in _movieService.GetMovies(releases.Select(v => v.MovieId).Distinct().Where(v => !movieMap.ContainsKey(v))))
            {
                movieMap[movie.Id] = movie;
            }

            foreach (var release in releases)
            {
                var movie = movieMap.GetValueOrDefault(release.MovieId);

                // Just in case the movie was removed, but wasn't cleaned up yet (housekeeper will clean it up)
                if (movie == null)
                {
                    continue;
                }

                // Languages will be empty if added before upgrading to v4, reparsing the languages if they're empty will set it to Unknown or better.
                if (release.ParsedMovieInfo.Languages.Empty())
                {
                    release.ParsedMovieInfo.Languages = LanguageParser.ParseLanguages(release.Title);
                }

                release.RemoteMovie = new RemoteMovie
                {
                    Movie = movie,
                    MovieMatchType = release.AdditionalInfo?.MovieMatchType ?? MovieMatchType.Unknown,
                    ReleaseSource = release.AdditionalInfo?.ReleaseSource ?? ReleaseSourceType.Unknown,
                    ParsedMovieInfo = release.ParsedMovieInfo,
                    Release = release.Release
                };

                try
                {
                    release.RemoteMovie.EditionContext = _editionRecovery.RecoverPending(movie, release);
                    _editionRecovery.ValidateEvidence(release.RemoteMovie.EditionContext, release.ParsedMovieInfo);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to recover pending edition for {0}", release.Id);
                    release.RemoteMovie.EditionRecoveryError = ex.Message;
                    result.Add(release);
                    continue;
                }

                _aggregationService.Augment(release.RemoteMovie);
                release.RemoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(release.RemoteMovie, release.Release.Size);
                release.RemoteMovie.CustomFormatScore = release.RemoteMovie.EffectiveQualityProfile?.CalculateCustomFormatScore(release.RemoteMovie.CustomFormats) ?? 0;

                result.Add(release);
            }

            return result;
        }

        private Queue.Queue GetQueueItem(PendingRelease pendingRelease, Lazy<DateTime> nextRssSync, Movie movie)
        {
            var ect = pendingRelease.Release.PublishDate.AddMinutes(GetDelay(pendingRelease.RemoteMovie));

            if (ect < nextRssSync.Value)
            {
                ect = nextRssSync.Value;
            }
            else
            {
                ect = ect.AddMinutes(_configService.RssSyncInterval);
            }

            var timeLeft = ect.Subtract(DateTime.UtcNow);

            if (timeLeft.TotalSeconds < 0)
            {
                timeLeft = TimeSpan.Zero;
            }

            string downloadClientName = null;
            var indexer = _indexerFactory.Find(pendingRelease.Release.IndexerId);

            if (indexer is { DownloadClientId: > 0 })
            {
                var downloadClient = _downloadClientFactory.Find(indexer.DownloadClientId);

                downloadClientName = downloadClient?.Name;
            }

            var queue = new Queue.Queue
            {
                Id = GetQueueId(pendingRelease, movie),
                ErrorMessage = pendingRelease.RemoteMovie.EditionRecoveryError,
                Movie = movie,
                Quality = pendingRelease.RemoteMovie.ParsedMovieInfo?.Quality ?? new QualityModel(),
                Languages = pendingRelease.RemoteMovie.Languages,
                Title = pendingRelease.Title,
                Size = pendingRelease.RemoteMovie.Release.Size,
                SizeLeft = pendingRelease.RemoteMovie.Release.Size,
                RemoteMovie = pendingRelease.RemoteMovie,
                TimeLeft = timeLeft,
                EstimatedCompletionTime = ect,
                Added = pendingRelease.Added,
                Status = Enum.TryParse(pendingRelease.Reason.ToString(), out QueueStatus outValue) ? outValue : QueueStatus.Unknown,
                Protocol = pendingRelease.RemoteMovie.Release.DownloadProtocol,
                Indexer = pendingRelease.RemoteMovie.Release.Indexer,
                DownloadClient = downloadClientName
            };

            return queue;
        }

        private PendingRelease Insert(DownloadDecision decision, PendingReleaseReason reason)
        {
            var release = new PendingRelease
            {
                MovieId = decision.RemoteMovie.Movie.Id,
                MovieEditionId = decision.RemoteMovie.EditionContext?.Edition.Id,
                EditionRuleRevision = decision.RemoteMovie.EditionContext?.Edition.RuleRevision,
                ParsedMovieInfo = decision.RemoteMovie.ParsedMovieInfo,
                Release = decision.RemoteMovie.Release,
                Title = decision.RemoteMovie.Release.Title,
                Added = DateTime.UtcNow,
                Reason = reason,
                AdditionalInfo = new PendingReleaseAdditionalInfo
                {
                    EditionName = decision.RemoteMovie.EditionContext?.Edition.Name,
                    MovieMatchType = decision.RemoteMovie.MovieMatchType,
                    ReleaseSource = decision.RemoteMovie.ReleaseSource
                }
            };

            if (release.ParsedMovieInfo == null)
            {
                _logger.Warn("Pending release {0} does not have ParsedMovieInfo, will cause issues.", release.Title);
            }

            _repository.Insert(release);

            _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
            return release;
        }

        private void Delete(PendingRelease pendingRelease)
        {
            _repository.Delete(pendingRelease);
            _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
        }

        private static Func<PendingRelease, bool> MatchingReleasePredicate(ReleaseInfo release)
        {
            return p => ReleaseIdentity.Matches(p.Release, release);
        }

        private int GetDelay(RemoteMovie remoteMovie)
        {
            var delayProfile = _delayProfileService.AllForTags(remoteMovie.Movie.Tags).OrderBy(d => d.Order).First();
            var delay = delayProfile.GetProtocolDelay(remoteMovie.Release.DownloadProtocol);
            var minimumAge = _configService.MinimumAge;

            return new[] { delay, minimumAge }.Max();
        }

        private void RemoveGrabbed(RemoteMovie remoteMovie)
        {
            var pendingReleases = GetPendingReleases(remoteMovie.Movie.Id);

            var existingReports = pendingReleases.Where(r => r.RemoteMovie.Movie.Id == remoteMovie.Movie.Id &&
                r.MovieEditionId == remoteMovie.EditionContext?.Edition.Id && r.RemoteMovie.EditionRecoveryError == null)
                                                             .ToList();

            if (existingReports.Empty())
            {
                return;
            }

            var profile = remoteMovie.EffectiveQualityProfile;

            foreach (var existingReport in existingReports)
            {
                var compare = new QualityModelComparer(profile).Compare(remoteMovie.ParsedMovieInfo.Quality,
                                                                        existingReport.RemoteMovie.ParsedMovieInfo.Quality);

                // Only remove lower/equal quality pending releases
                // It is safer to retry these releases on the next round than remove it and try to re-add it (if its still in the feed)
                if (compare >= 0)
                {
                    _logger.Debug("Removing previously pending release, as it was grabbed.");
                    Delete(existingReport);
                }
            }
        }

        private void RemoveRejected(List<DownloadDecision> rejected)
        {
            _logger.Debug("Removing failed releases from pending");
            var pending = GetPendingReleases();

            foreach (var rejectedRelease in rejected)
            {
                if (rejectedRelease.Rejections.Any(r => r.Reason == DownloadRejectionReason.MultiEditionAcquisitionDisabled))
                {
                    continue; // Preserve stored work while acquisition is gated.
                }

                var matching = pending.Where(p => p.MovieId == rejectedRelease.RemoteMovie.Movie?.Id &&
                    p.MovieEditionId == rejectedRelease.RemoteMovie.EditionContext?.Edition.Id)
                    .Where(MatchingReleasePredicate(rejectedRelease.RemoteMovie.Release));

                foreach (var pendingRelease in matching)
                {
                    _logger.Debug("Removing previously pending release, as it has now been rejected.");
                    Delete(pendingRelease);
                }
            }
        }

        private PendingRelease FindPendingRelease(int queueId)
        {
            return GetPendingReleases().First(p => queueId == GetQueueId(p, p.RemoteMovie.Movie));
        }

        private int GetQueueId(PendingRelease pendingRelease, Movie movie)
        {
            return HashConverter.GetHashInt31(string.Format("pending-{0}-movie{1}", pendingRelease.Id, movie?.Id ?? 0));
        }

        private int PrioritizeDownloadProtocol(Movie movie, DownloadProtocol downloadProtocol)
        {
            var delayProfile = _delayProfileService.BestForTags(movie.Tags);

            if (downloadProtocol == delayProfile.PreferredProtocol)
            {
                return 0;
            }

            return 1;
        }

        public void Handle(MoviesDeletedEvent message)
        {
            _repository.DeleteByMovieIds(message.Movies.Select(m => m.Id).ToList());
        }

        public void Handle(MovieGrabbedEvent message)
        {
            RemoveGrabbed(message.Movie);
        }

        public void Handle(RssSyncCompleteEvent message)
        {
            RemoveRejected(message.ProcessedDecisions.Rejected);
        }
    }
}
