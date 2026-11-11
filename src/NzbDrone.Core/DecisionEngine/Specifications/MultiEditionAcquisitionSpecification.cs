using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class MultiEditionAcquisitionSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly IMovieEditionRepository _editions;

        public MultiEditionAcquisitionSpecification(IMovieEditionRepository editions)
        {
            _editions = editions;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public DownloadSpecDecision IsSatisfiedBy(RemoteMovie subject, ReleaseDecisionInformation information)
        {
            return MovieEditionAcquisitionGuard.RequiresEditionContext(subject, subject.Movie == null ? null : _editions.GetByMovie(subject.Movie.Id))
                ? DownloadSpecDecision.Reject(DownloadRejectionReason.MultiEditionAcquisitionDisabled, "Multi-edition acquisition is disabled until classification and download recovery gates pass. Use explicit manual file imports.")
                : DownloadSpecDecision.Accept();
        }
    }
}
