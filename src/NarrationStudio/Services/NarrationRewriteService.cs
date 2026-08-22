using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio.Configuration;
using OllamaSharp;

namespace NarrationStudio.Services;

public sealed class OllamaChatClientFactory
{
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaChatClientFactory> _logger;
    private readonly Lock _gate = new();
    private IChatClient? _client;

    public OllamaChatClientFactory(IOptions<OllamaOptions> options, ILogger<OllamaChatClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IChatClient Create()
    {
        lock (_gate)
        {
            return _client ??= CreateCore();
        }
    }

    private IChatClient CreateCore()
    {
        var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint)
            ? "http://localhost:11434"
            : _options.Endpoint.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(_options.Model) ? "qwen3.8:latest" : _options.Model;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes));

        var http = new HttpClient
        {
            BaseAddress = new Uri(endpoint),
            Timeout = timeout
        };

        _logger.LogInformation("Using Ollama model {Model} at {Endpoint} (timeout {Timeout} min).", model, endpoint, timeout.TotalMinutes);
        return new OllamaApiClient(http, model);
    }
}

public sealed class NarrationRewriteService
{
    private readonly OllamaChatClientFactory _factory;
    private readonly PipelineStore _store;
    private readonly StudioOptions _studio;
    private readonly ILogger<NarrationRewriteService> _logger;

    public NarrationRewriteService(
        OllamaChatClientFactory factory,
        PipelineStore store,
        IOptions<StudioOptions> studio,
        ILogger<NarrationRewriteService> logger)
    {
        _factory = factory;
        _store = store;
        _studio = studio.Value;
        _logger = logger;
    }

    public async Task<string> RephraseAsync(string transcript, CancellationToken cancellationToken)
    {
        if (_studio.UseStubNarration)
        {
            if (!TryReadStubNarration(out var stubPath, out var stubText))
            {
                throw new FileNotFoundException(
                    "Ollama rewrite is stubbed, but voice_transcript_ollama.txt was not found. Place it next to the transcript or set Studio:StubNarrationPath.");
            }

            _logger.LogWarning("Ollama rewrite is stubbed. Using placeholder narration from {Path}.", stubPath);
            var stub = StripModelExtras(stubText);
            if (string.IsNullOrWhiteSpace(stub) || stub.Length < 400)
            {
                throw new InvalidOperationException($"Stub narration at {stubPath} is empty or too short.");
            }

            _logger.LogInformation("Loaded stub narration ({Words} words).", CountWords(stub));
            return stub;
        }

        var minutes = Math.Max(1, _studio.TargetTalkMinutes);
        var targetWords = minutes * 140;
        var chat = _factory.Create();

        var instructions =
            $"""
            You are a speechwriter for a live talk.
            Rewrite the source notes into a spoken narration of about {minutes} minutes
            (roughly {targetWords - 80} to {targetWords + 80} words).

            Audience: people from all walks of life, leaning toward executives
            who need a clear, practical view of information systems, topical vs
            systemic thinking, and why integrated operations matter.

            Voice:
            - A talk, not an essay. Use "you", short spoken sentences, and light signposting.
            - Warm and authoritative. No slang, no academic jargon without a plain explanation.
            - Keep the speaker's ideas, stories, and conclusions. Clean up false starts,
              repetition, and transcription artifacts.
            - Do not invent companies, dates, or savings figures that are not in the source.

            """;
        if (!string.IsNullOrWhiteSpace(_studio.RewriteExtraInstructions))
        {
            instructions +=
                $"""
                Additional operator instructions:
                {_studio.RewriteExtraInstructions.Trim()}

                """;
        }

        instructions +=
            """
            Output rules:
            - Return only the words to be spoken.
            - No title, no stage directions, no markdown, no bullet lists, no thinking.
            """;

        _logger.LogInformation("Submitting transcript ({Length} chars) to Ollama for a {Minutes}-minute talk.", transcript.Length, minutes);
        var elapsed = Stopwatch.StartNew();

        var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, instructions),
                new ChatMessage(ChatRole.User, "Rewrite this source transcript into the talk:\n\n" + transcript)
            ],
            new ChatOptions { Temperature = 0.35f },
            cancellationToken);

        elapsed.Stop();
        var narration = StripModelExtras(response.Text);
        if (string.IsNullOrWhiteSpace(narration) || narration.Length < 400)
        {
            throw new InvalidOperationException("Ollama returned an empty or too-short narration. Check that the model is pulled and the endpoint is reachable.");
        }

        _logger.LogInformation(
            "Received rewritten narration ({Words} words) in {Elapsed:0.0}s.",
            CountWords(narration),
            elapsed.Elapsed.TotalSeconds);
        return narration;
    }

    private bool TryReadStubNarration(out string path, out string text)
    {
        path = "";
        text = "";
        var transcriptDir = Path.GetDirectoryName(_store.Snapshot().TranscriptPath);
        var configured = _studio.StubNarrationPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "voice_transcript_ollama.txt";
        }

        var candidates = new List<string>();
        if (Path.IsPathRooted(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(transcriptDir))
            {
                candidates.Add(Path.Combine(transcriptDir, configured));
                candidates.Add(Path.Combine(transcriptDir, "voice_transcript_ollama.txt"));
            }

            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), configured));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, configured));
            candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", configured)));
        }

        var match = candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);

        if (match is null)
        {
            return false;
        }

        path = match;
        text = File.ReadAllText(match);
        return true;
    }

    internal static string StripModelExtras(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var cleaned = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"```(?:[\w-]+)?\s*", "");
        cleaned = cleaned.Replace("```", "", StringComparison.Ordinal);
        return cleaned.Trim();
    }

    private static int CountWords(string text) =>
        text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
}
