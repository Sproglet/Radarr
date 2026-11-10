using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Api.V3.Movies;
using Radarr.Http;
using Radarr.Http.REST;

namespace Radarr.Api.V3.ManualImport
{
    [V3ApiController]
    public class ManualImportController : Controller
    {
        private readonly IManualImportService _manualImportService;

        public ManualImportController(IManualImportService manualImportService)
        {
            _manualImportService = manualImportService;
        }

        [HttpGet]
        [Produces("application/json")]
        public Ok<List<ManualImportResource>> GetMediaFiles(string folder, string downloadId, int? movieId, bool filterExistingFiles = true, int? downloadClientId = null)
        {
            if (movieId.HasValue && downloadId.IsNullOrWhiteSpace() && !downloadClientId.HasValue)
            {
                return TypedResults.Ok(_manualImportService.GetMediaFiles(movieId.Value).ToResource().Select(AddQualityWeight).ToList());
            }

            try
            {
                var files = downloadClientId.HasValue
                    ? _manualImportService.GetMediaFiles(folder, downloadId, movieId, filterExistingFiles, downloadClientId)
                    : _manualImportService.GetMediaFiles(folder, downloadId, movieId, filterExistingFiles);
                return TypedResults.Ok(files.ToResource().Select(AddQualityWeight).ToList());
            }
            catch (InvalidOperationException ex)
            {
                throw new BadRequestException(ex.Message);
            }
        }

        [HttpPost]
        [Consumes("application/json")]
        public Results<Ok<List<ManualImportReprocessResource>>, BadRequest> ReprocessItems([FromBody] List<ManualImportReprocessResource> items)
        {
            if (items is { Count: 0 })
            {
                throw new BadRequestException("items must be provided");
            }

            foreach (var item in items)
            {
                ManualImportItem processedItem;
                try
                {
                    processedItem = item.DownloadClientId.HasValue
                        ? _manualImportService.ReprocessItem(item.Path, item.DownloadId, item.MovieId, item.ReleaseGroup, item.Quality, item.Languages, item.IndexerFlags, item.MovieEditionId, item.DownloadClientId)
                        : _manualImportService.ReprocessItem(item.Path, item.DownloadId, item.MovieId, item.ReleaseGroup, item.Quality, item.Languages, item.IndexerFlags, item.MovieEditionId);
                }
                catch (InvalidOperationException ex)
                {
                    throw new BadRequestException(ex.Message);
                }

                item.Movie = processedItem.Movie.ToResource(0);
                item.MovieEditionId = processedItem.MovieEditionId;
                item.MovieEditionName = processedItem.MovieEditionName;
                item.DownloadClientId = processedItem.DownloadClientId;
                item.IndexerFlags = processedItem.IndexerFlags;
                item.Rejections = processedItem.Rejections.Select(r => r.ToResource());
                item.CustomFormats = processedItem.CustomFormats.ToResource(false);
                item.CustomFormatScore = processedItem.CustomFormatScore;

                // Only set the language/quality if they're unknown and languages were returned.
                if (item.Languages?.Count <= 1 && (item.Languages?.SingleOrDefault() ?? Language.Unknown) == Language.Unknown && processedItem.Languages.Any())
                {
                    item.Languages = processedItem.Languages;
                }

                if (item.Quality?.Quality == Quality.Unknown)
                {
                    item.Quality = processedItem.Quality;
                }

                if (item.ReleaseGroup.IsNullOrWhiteSpace())
                {
                    item.ReleaseGroup = processedItem.ReleaseGroup;
                }
            }

            return TypedResults.Ok(items);
        }

        private ManualImportResource AddQualityWeight(ManualImportResource item)
        {
            if (item.Quality != null)
            {
                item.QualityWeight = Quality.DefaultQualityDefinitions.Single(q => q.Quality == item.Quality.Quality).Weight;
                item.QualityWeight += item.Quality.Revision.Real * 10;
                item.QualityWeight += item.Quality.Revision.Version;
            }

            return item;
        }
    }
}
