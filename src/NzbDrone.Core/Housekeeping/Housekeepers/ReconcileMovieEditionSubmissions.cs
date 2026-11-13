using System;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class ReconcileMovieEditionSubmissions : IHousekeepingTask, IHandleAsync<ApplicationStartedEvent>
    {
        private readonly IMovieEditionSubmissionRepository _journal;
        private readonly IMovieEditionSubmissionService _submissions;
        private readonly Logger _logger;
        private readonly object _sync = new();

        public ReconcileMovieEditionSubmissions(IMovieEditionSubmissionRepository journal, IMovieEditionSubmissionService submissions, Logger logger)
        {
            _journal = journal;
            _submissions = submissions;
            _logger = logger;
        }

        public void Clean()
        {
            lock (_sync)
            {
                var throughId = _journal.ReconciliationUpperBound();
                var afterId = 0;
                while (afterId < throughId)
                {
                    var candidates = _journal.ReconciliationCandidates(afterId, throughId, 100);
                    if (candidates.Count == 0)
                    {
                        return;
                    }

                    foreach (var candidate in candidates)
                    {
                        afterId = candidate.Id;
                        try
                        {
                            // Reconcile only durable acceptance; this operation has no client or filesystem IO.
                            var result = _submissions.Reconcile(candidate.OperationId);
                            if (candidate.HasTerminalEvidence && result.State != MovieEditionSubmissionState.Settled)
                            {
                                _logger.Warn("Edition submission row {0} retains terminal evidence requiring review", candidate.Id);
                            }
                        }
                        catch (Exception)
                        {
                            // Keep failures isolated, including malformed old payloads. Do not log private release/client payloads.
                            _logger.Warn("Could not reconcile edition submission row {0}; review it if it remains unresolved", candidate.Id);
                        }
                    }
                }
            }
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            Clean();
        }
    }
}
