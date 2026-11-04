using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Movies
{
    public enum EditionClassificationStatus
    {
        Matched,
        Unconfigured,
        Ambiguous,
        Invalid
    }

    public class EditionClassification
    {
        public EditionClassificationStatus Status { get; set; }
        public List<int> CandidateEditionIds { get; set; } = new List<int>();
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public string ParsedLabel { get; set; }
        public int? RuleRevision { get; set; }
        public string Reason { get; set; }
    }

    public interface IMovieEditionClassifier
    {
        EditionClassification Classify(int movieId, string releaseTitle, string parsedLabel, string presentationEvidence, int? explicitEditionId = null);
    }

    // Read-only classification. Does not assign files, change monitoring or authorize a grab.
    // Presentation evidence must exclude the movie title and release group (e.g. parsed release tags).
    public class MovieEditionClassifier : IMovieEditionClassifier
    {
        private readonly IMovieEditionRepository _editions;
        private static readonly TimeSpan RuleTimeout = TimeSpan.FromMilliseconds(50);
        private static readonly (string Key, string Value, string Pattern)[] Presets =
        {
            ("cut", "theatrical", @"theatrical(?:[ ._-]+(?:cut|version))?"),
            ("cut", "directors-cut", @"director['’]?s(?:[ ._-]+cut)?"),
            ("cut", "extended", @"extended(?:[ ._-]+(?:cut|edition|version))?"),
            ("cut", "unrated", @"unrated"),
            ("cut", "final-cut", @"final[ ._-]+cut"),
            ("cut", "assembly-cut", @"assembly[ ._-]+cut"),
            ("stereoscopic", "3d", @"3d|h?sbs|h?ou|half[ ._-]+(?:sbs|ou)"),
            ("stereoscopic", "2d", @"2d"),
            ("framing", "imax", @"imax"),
            ("framing", "open-matte", @"open[ ._-]*matte")
        };

        public MovieEditionClassifier(IMovieEditionRepository editions)
        {
            _editions = editions;
        }

        public static void ValidateRules(IEnumerable<string> rules)
        {
            var list = rules?.ToList() ?? new List<string>();
            if (list.Count > 32 || list.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 4096))
            {
                throw new ArgumentException("Edition rules require at most 32 nonempty expressions of up to 4096 characters.");
            }

            foreach (var rule in list)
            {
                _ = new Regex(rule, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleTimeout);
            }
        }

        public EditionClassification Classify(int movieId, string releaseTitle, string parsedLabel, string presentationEvidence, int? explicitEditionId = null)
        {
            var result = new EditionClassification { ParsedLabel = parsedLabel };
            var editions = _editions.GetByMovie(movieId);
            if (explicitEditionId.HasValue)
            {
                var target = editions.SingleOrDefault(e => e.Id == explicitEditionId && e.MovieId == movieId);
                if (target == null)
                {
                    return Finish(result, EditionClassificationStatus.Invalid, "The explicit edition does not belong to this movie.");
                }

                result.Attributes = new Dictionary<string, string>(target.Attributes);
                return Match(result, new List<MovieEdition> { target }, "Explicit manual assignment.");
            }

            if (string.IsNullOrWhiteSpace(releaseTitle) || releaseTitle.Length > 16384)
            {
                return Finish(result, EditionClassificationStatus.Invalid, "Missing or oversized release title.");
            }

            var customMatches = new List<MovieEdition>();
            var budget = Stopwatch.StartNew();
            try
            {
                foreach (var edition in editions)
                {
                    ValidateRules(edition.MatchRules);
                    var matched = false;
                    foreach (var rule in edition.MatchRules ?? new List<string>())
                    {
                        if (budget.ElapsedMilliseconds > 250)
                        {
                            return Finish(result, EditionClassificationStatus.Invalid, "Edition matching exceeded its time budget.");
                        }

                        matched |= Regex.IsMatch(releaseTitle, rule, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleTimeout);
                    }

                    if (matched)
                    {
                        customMatches.Add(edition);
                    }
                }
            }
            catch (ArgumentException)
            {
                return Finish(result, EditionClassificationStatus.Invalid, "An edition rule is invalid.");
            }
            catch (RegexMatchTimeoutException)
            {
                return Finish(result, EditionClassificationStatus.Invalid, "An edition rule timed out.");
            }

            if (customMatches.Count != 0)
            {
                return Match(result, customMatches, "User-defined edition rules.");
            }

            var label = parsedLabel ?? string.Empty;
            var presentation = presentationEvidence ?? string.Empty;
            if (label.Length + presentation.Length > 16384)
            {
                return Finish(result, EditionClassificationStatus.Invalid, "Oversized edition evidence.");
            }

            foreach (var preset in Presets)
            {
                // Cuts are taken from parsed label evidence; title words cannot become cut tags.
                var evidence = preset.Key == "cut" ? label : label + " " + presentation;
                if (!Regex.IsMatch(evidence, @"(?<![\p{L}\p{N}])(?:" + preset.Pattern + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleTimeout))
                {
                    continue;
                }

                if (result.Attributes.TryGetValue(preset.Key, out var previous) && previous != preset.Value)
                {
                    return Finish(result, EditionClassificationStatus.Ambiguous, "Conflicting labels in the " + preset.Key + " dimension.");
                }

                result.Attributes[preset.Key] = preset.Value;
                label = Regex.Replace(label, @"(?<![\p{L}\p{N}])(?:" + preset.Pattern + @")(?![\p{L}\p{N}])", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleTimeout);
            }

            // Unrecognized descriptive labels require an explicit/custom mapping, never a default.
            if (Regex.IsMatch(label, @"[\p{L}\p{N}]", RegexOptions.CultureInvariant, RuleTimeout))
            {
                return Finish(result, EditionClassificationStatus.Unconfigured, "Unmapped edition label.");
            }

            result.Attributes.TryAdd("cut", "theatrical");
            var candidates = editions.Where(e => (e.MatchRules == null || e.MatchRules.Count == 0) &&
                e.Attributes.Count == result.Attributes.Count && result.Attributes.All(a => e.Attributes.TryGetValue(a.Key, out var value) && value == a.Value)).ToList();
            return Match(result, candidates, "Built-in attribute combination.");
        }

        private static EditionClassification Match(EditionClassification result, List<MovieEdition> candidates, string reason)
        {
            result.CandidateEditionIds = candidates.Select(e => e.Id).OrderBy(id => id).ToList();
            result.RuleRevision = candidates.Count == 1 ? candidates[0].RuleRevision : null;
            return Finish(result, candidates.Count == 1 ? EditionClassificationStatus.Matched :
                candidates.Count == 0 ? EditionClassificationStatus.Unconfigured : EditionClassificationStatus.Ambiguous, reason);
        }

        private static EditionClassification Finish(EditionClassification result, EditionClassificationStatus status, string reason)
        {
            result.Status = status;
            result.Reason = reason;
            return result;
        }
    }
}
