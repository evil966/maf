using System.Text;
using System.Text.RegularExpressions;
using NarrationStudio.Configuration;
using NarrationStudio.Models;

namespace NarrationStudio.Services;

public sealed class TranscriptSegmenter
{
    public List<NarrationSegment> Segment(string transcript, StudioOptions options)
    {
        var cleaned = Regex.Replace(transcript, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new InvalidOperationException("The transcript is empty.");
        }

        var sentences = SplitSentences(cleaned);
        if (sentences.Count == 0)
        {
            sentences = [cleaned];
        }

        var targetCount = Math.Max(1, options.SegmentCount);
        var groups = Distribute(sentences, targetCount);
        var segments = new List<NarrationSegment>(targetCount);

        for (var i = 0; i < groups.Count; i++)
        {
            var script = string.Join(" ", groups[i]).Trim();
            segments.Add(new NarrationSegment
            {
                Number = i + 1,
                Title = MakeTitle(script, i + 1),
                Script = script
            });
        }

        return segments;
    }

    public void WriteScriptFiles(IReadOnlyList<NarrationSegment> segments, PipelineStore store)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            var path = Path.Combine(store.ScriptsDirectory, $"narration-{segment.Number:00}.txt");
            var body =
                $"""
                SEGMENT {segment.Number:00}: {segment.Title}

                {segment.Script}
                """;
            File.WriteAllText(path, body);
            builder.AppendLine($"=== {Path.GetFileName(path)} ===");
            builder.AppendLine(body);
            builder.AppendLine();
        }

        File.WriteAllText(Path.Combine(store.ScriptsDirectory, "narration-all.txt"), builder.ToString());
    }

    private static List<string> SplitSentences(string text)
    {
        var matches = Regex.Split(text, @"(?<=[\.!\?])\s+");
        return matches.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static List<List<string>> Distribute(IReadOnlyList<string> sentences, int buckets)
    {
        var totalChars = sentences.Sum(s => s.Length);
        var target = Math.Max(1, totalChars / buckets);
        var groups = new List<List<string>>();
        var current = new List<string>();
        var currentChars = 0;

        foreach (var sentence in sentences)
        {
            if (groups.Count < buckets - 1 && current.Count > 0 && currentChars + sentence.Length > target)
            {
                groups.Add(current);
                current = [];
                currentChars = 0;
            }

            current.Add(sentence);
            currentChars += sentence.Length + 1;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        while (groups.Count < buckets)
        {
            var richest = groups.OrderByDescending(g => g.Count).First();
            if (richest.Count < 2)
            {
                groups.Add([richest[0]]);
                break;
            }

            var moved = richest[^1];
            richest.RemoveAt(richest.Count - 1);
            groups.Add([moved]);
        }

        return groups.Take(buckets).ToList();
    }

    private static string MakeTitle(string script, int number)
    {
        var first = script.Split(['.', '!', '?'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? script;
        if (first.Length > 72)
        {
            first = first[..72].Trim() + "...";
        }

        return $"Segment {number:00} — {first}";
    }
}
