using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NarrationStudio.Models;

namespace NarrationStudio.Services;

public sealed class SlidePhraseRewriteService
{
    private readonly OllamaChatClientFactory _factory;
    private readonly ILogger<SlidePhraseRewriteService> _logger;

    public SlidePhraseRewriteService(
        OllamaChatClientFactory factory,
        ILogger<SlidePhraseRewriteService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task RewriteAllAsync(IEnumerable<NarrationSegment> segments, CancellationToken cancellationToken)
    {
        var chat = _factory.Create();
        var total = Stopwatch.StartNew();
        var rewritten = 0;
        foreach (var segment in segments)
        {
            foreach (var slide in segment.Slides)
            {
                if (slide.PhraseRewritten && !string.IsNullOrWhiteSpace(slide.Phrase))
                {
                    continue;
                }

                var source = string.IsNullOrWhiteSpace(slide.SourcePhrase) ? slide.Phrase : slide.SourcePhrase;
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                slide.SourcePhrase = source.Trim();
                slide.Phrase = await RephraseToOneSentenceAsync(chat, segment.Number, slide.Number, slide.SourcePhrase, cancellationToken);
                slide.PhraseRewritten = true;
                rewritten++;
            }
        }

        if (rewritten > 0)
        {
            _logger.LogInformation("Rephrased {Count} slide sentence(s) in {Elapsed:0.0}s.", rewritten, total.Elapsed.TotalSeconds);
        }
    }

    private async Task<string> RephraseToOneSentenceAsync(
        IChatClient chat,
        int segmentNumber,
        int slideNumber,
        string source,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Rephrasing slide {Segment:00}-{Slide:00} into one sentence ({Chars} chars).", segmentNumber, slideNumber, source.Length);
        var elapsed = Stopwatch.StartNew();

        var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System,
                    """
                    You write on-screen slide titles for a live talk.
                    Rewrite the excerpt as exactly one short sentence.
                    Rules:
                    - One sentence only. No second sentence.
                    - Keep the original meaning. Do not add facts.
                    - Plain spoken language. No quotes, title, markdown, bullets, or thinking.
                    - About 8 to 22 words. End with a period.
                    """),
                new ChatMessage(ChatRole.User, "Excerpt to put on the slide:\n\n" + source)
            ],
            new ChatOptions { Temperature = 0.2f },
            cancellationToken);

        elapsed.Stop();
        var seconds = elapsed.Elapsed.TotalSeconds;
        var sentence = FirstSentence(NarrationRewriteService.StripModelExtras(response.Text));
        if (string.IsNullOrWhiteSpace(sentence))
        {
            _logger.LogWarning(
                "Ollama returned an empty slide sentence for {Segment:00}-{Slide:00} after {Elapsed:0.0}s; using the source excerpt.",
                segmentNumber,
                slideNumber,
                seconds);
            return TrimForSlide(source);
        }

        sentence = TrimForSlide(sentence);
        _logger.LogInformation(
            "Rephrased slide {Segment:00}-{Slide:00} in {Elapsed:0.0}s: {Sentence}",
            segmentNumber,
            slideNumber,
            seconds,
            sentence);
        return sentence;
    }

    private static string FirstSentence(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim().Trim('"', '\'', '“', '”');
        var match = Regex.Match(flat, @"^[^.!?]+[.!?]");
        return match.Success ? match.Value.Trim() : flat;
    }

    private static string TrimForSlide(string phrase)
    {
        phrase = Regex.Replace(phrase, @"\s+", " ").Trim();
        return phrase.Length <= 220 ? phrase : phrase[..217].TrimEnd() + "...";
    }
}
