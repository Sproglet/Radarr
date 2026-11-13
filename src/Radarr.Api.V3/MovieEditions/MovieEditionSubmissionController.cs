using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Download;
using NzbDrone.Core.Movies;
using Radarr.Http;
using Radarr.Http.REST;

namespace Radarr.Api.V3.MovieEditions
{
    [V3ApiController("movieedition/submission")]
    public class MovieEditionSubmissionController : Controller
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionSubmissionRepository _journal;
        private readonly IMovieEditionSubmissionService _submissions;

        public MovieEditionSubmissionController(IMovieRepository movies, IMovieEditionSubmissionRepository journal, IMovieEditionSubmissionService submissions)
        {
            _movies = movies;
            _journal = journal;
            _submissions = submissions;
        }

        [HttpGet]
        [Produces("application/json")]
        public Ok<List<MovieEditionSubmissionResource>> GetForMovie(int movieId)
        {
            if (movieId <= 0)
            {
                throw new BadRequestException("Provide a positive movie ID.");
            }

            _ = _movies.Find(movieId) ?? throw new NotFoundException("Movie not found.");
            return TypedResults.Ok(_journal.RecentForMovie(movieId).Select(MovieEditionSubmissionResource.FromModel).ToList());
        }

        [HttpPost("{operationId}/reconcile")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public Results<Ok<MovieEditionSubmissionResource>, Conflict<object>> Reconcile(string operationId, [FromBody] MovieEditionSubmissionReconcileResource request)
        {
            if (!Guid.TryParseExact(operationId, "D", out var parsed) || parsed == Guid.Empty || operationId != parsed.ToString("D") ||
                request?.MovieId is not > 0 || request.MovieEditionId is not > 0 || !request.ExpectedState.HasValue ||
                request.ExpectedState is not (MovieEditionSubmissionState.Accepted or MovieEditionSubmissionState.Recorded) ||
                string.IsNullOrWhiteSpace(request.ExpectedDownloadId))
            {
                throw new BadRequestException("Provide canonical operation identity and complete expected movie, edition, state and client download identity.");
            }

            var current = _journal.FindByOperationId(operationId) ?? throw new NotFoundException("Submission not found.");
            if (current.MovieId != request.MovieId || current.MovieEditionId != request.MovieEditionId)
            {
                throw new BadRequestException("Submission belongs to a different movie or edition.");
            }

            _ = _movies.Find(current.MovieId) ?? throw new NotFoundException("Movie not found.");
            if (current.State != request.ExpectedState || current.DownloadId != request.ExpectedDownloadId ||
                !MovieEditionSubmissionResource.FromModel(current).CanReconcile)
            {
                return TypedResults.Conflict<object>(new { message = "Submission state/evidence changed or needs review. Reload before reconciling." });
            }

            try
            {
                return TypedResults.Ok(MovieEditionSubmissionResource.FromModel(_submissions.Reconcile(operationId)));
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict<object>(new { message = exception.Message });
            }
        }
    }
}
