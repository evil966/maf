using System.Diagnostics;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NarrationStudio.Configuration;

namespace NarrationStudio.Services;

public sealed class TtsService
{
    private readonly StudioOptions _options;
    private readonly ILogger<TtsService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public TtsService(IOptions<StudioOptions> options, ILogger<TtsService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string Path, double DurationSeconds)> SynthesizeAsync(
        string script,
        string mp3Path,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(mp3Path)!);
        var apiKey = ResolveApiKey();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await SynthesizeElevenLabsAsync(script, mp3Path, apiKey, cancellationToken);
        }
        else
        {
            var wavPath = Path.ChangeExtension(mp3Path, ".wav");
            try
            {
                SynthesizeWindowsSpeech(script, wavPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows SAPI failed; generating a timed narration bed instead.");
                GenerateToneBed(script, wavPath);
            }

            FfmpegProcess.Run(["-y", "-i", wavPath, "-codec:a", "libmp3lame", "-qscale:a", "4", mp3Path], cancellationToken);
            TryDelete(wavPath);
        }

        var duration = ProbeDurationSeconds(mp3Path);
        return (mp3Path, duration);
    }

    private async Task SynthesizeElevenLabsAsync(string script, string mp3Path, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{_options.ElevenLabsVoiceId}");
        request.Headers.Add("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                text = script,
                model_id = _options.ElevenLabsModelId
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ElevenLabs TTS failed ({(int)response.StatusCode}) for voice {_options.ElevenLabsVoiceId}: {body}");
        }

        await using var output = File.Create(mp3Path);
        await response.Content.CopyToAsync(output, cancellationToken);
        _logger.LogInformation("Wrote ElevenLabs audio {Path} with voice {VoiceId}.", mp3Path, _options.ElevenLabsVoiceId);
    }

    public string DescribeProvider()
    {
        return string.IsNullOrWhiteSpace(ResolveApiKey())
            ? "Windows SAPI (no ElevenLabs API key)"
            : $"ElevenLabs ({_options.ElevenLabsVoiceId})";
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ElevenLabsApiKey))
        {
            return _options.ElevenLabsApiKey.Trim();
        }

        return Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")?.Trim() ?? "";
    }

    private static void SynthesizeWindowsSpeech(string script, string wavPath)
    {
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.SetOutputToWaveFile(wavPath);
        synthesizer.Rate = 0;
        synthesizer.Speak(script);
    }

    private static void GenerateToneBed(string script, string wavPath)
    {
        var words = Math.Max(8, script.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var seconds = Math.Clamp(words / 2.4, 4, 90);
        var sine = new SignalGenerator(44100, 1)
        {
            Gain = 0.08,
            Frequency = 196,
            Type = SignalGeneratorType.Sin
        }.Take(TimeSpan.FromSeconds(seconds));

        WaveFileWriter.CreateWaveFile16(wavPath, sine);
    }

    public static double ProbeDurationSeconds(string mediaPath, CancellationToken cancellationToken = default)
    {
        var output = FfmpegProcess.RunProbe([
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            mediaPath
        ], cancellationToken);

        return double.TryParse(output.Trim(), out var seconds) ? seconds : 5;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}

public static class FfmpegProcess
{
    public static void Run(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        RunInternal("ffmpeg", args, capture: false, cancellationToken);
    }

    public static string RunProbe(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        return RunInternal("ffprobe", args, capture: true, cancellationToken);
    }

    private static string RunInternal(string fileName, IReadOnlyList<string> args, bool capture, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (!process.WaitForExit(250))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (DateTime.UtcNow >= deadline)
            {
                TryKill(process);
                throw new TimeoutException($"{fileName} timed out.");
            }
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed ({process.ExitCode}): {stderr}");
        }

        return capture ? stdout : stderr;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }
}
