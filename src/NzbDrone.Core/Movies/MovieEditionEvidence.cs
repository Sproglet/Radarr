using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Movies
{
    public class MovieEditionEvidence
    {
        public string Label { get; private set; }
        public string Presentation { get; private set; }
        public bool Valid { get; private set; }

        public static MovieEditionEvidence FromParsed(ParsedMovieInfo parsed)
        {
            // The parser replaces the matched movie title with a neutral placeholder. Never
            // scan the full title: a movie or group called IMAX/Extended is not edition evidence.
            if (string.IsNullOrWhiteSpace(parsed?.SimpleReleaseTitle) || parsed.SimpleReleaseTitle.Length > 16384)
            {
                return new MovieEditionEvidence();
            }

            var tags = parsed.SimpleReleaseTitle;
            if (!string.IsNullOrWhiteSpace(parsed.ReleaseGroup))
            {
                tags = Regex.Replace(tags, @"(?:-|\[)" + Regex.Escape(parsed.ReleaseGroup) + @"\]?(?:\.[A-Za-z0-9]{2,5})?$", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }

            tags = tags.Replace('.', ' ');
            var labels = new List<string>();
            var remaining = tags;
            // Preserve separate/conflicting labels rather than taking only the parser's first cut.
            for (var i = 0; i < 32; i++)
            {
                var label = Parser.Parser.ParseEdition("A.Movie " + remaining);
                if (string.IsNullOrWhiteSpace(label))
                {
                    break;
                }

                labels.Add(label);
                remaining = Regex.Replace(remaining, Regex.Escape(label), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }

            foreach (Match match in Regex.Matches(remaining, @"\b(?:criterion|[\p{L}\p{N}]+[ ._-]+(?:edition|cut|version))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            {
                labels.Add(match.Value);
            }

            return new MovieEditionEvidence { Valid = true, Label = string.Join(" ", labels), Presentation = tags };
        }
    }
}
