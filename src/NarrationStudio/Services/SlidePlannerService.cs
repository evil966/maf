using System.Text.RegularExpressions;
using NarrationStudio.Configuration;
using NarrationStudio.Models;

namespace NarrationStudio.Services;

public sealed class SlidePlannerService
{
    public List<SlideSpec> Plan(string script, StudioOptions options)
    {
        var maxSlides = Math.Clamp(options.MaxSlidesPerSegment, 1, 5);
        var sentences = Regex.Split(script, @"(?<=[\.!\?])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count == 0)
        {
            sentences = [script.Trim()];
        }

        var phrases = sentences.Count <= maxSlides
            ? sentences
            : MergeInto(sentences, maxSlides);

        return phrases
            .Take(maxSlides)
            .Select((phrase, index) => new SlideSpec
            {
                Number = index + 1,
                Phrase = TrimPhrase(phrase)
            })
            .ToList();
    }

    private static List<string> MergeInto(IReadOnlyList<string> sentences, int buckets)
    {
        var size = (int)Math.Ceiling(sentences.Count / (double)buckets);
        var result = new List<string>();
        for (var i = 0; i < sentences.Count; i += size)
        {
            result.Add(string.Join(" ", sentences.Skip(i).Take(size)));
        }

        return result.Take(buckets).ToList();
    }

    private static string TrimPhrase(string phrase)
    {
        phrase = Regex.Replace(phrase, @"\s+", " ").Trim();
        return phrase.Length <= 220 ? phrase : phrase[..217].TrimEnd() + "...";
    }
}
