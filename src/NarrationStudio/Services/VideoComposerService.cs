using System.Globalization;
using NarrationStudio.Configuration;
using NarrationStudio.Models;

namespace NarrationStudio.Services;

public sealed class VideoComposerService
{
    public string ComposeSegment(NarrationSegment segment, StudioOptions options, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(segment.AudioPath) || !File.Exists(segment.AudioPath))
        {
            throw new InvalidOperationException($"Audio for segment {segment.Number} is missing.");
        }

        var slides = segment.Slides.Where(s => File.Exists(s.ImagePath)).ToList();
        if (slides.Count == 0)
        {
            throw new InvalidOperationException($"Slides for segment {segment.Number} are missing.");
        }

        var duration = segment.AudioDurationSeconds ?? TtsService.ProbeDurationSeconds(segment.AudioPath);
        var perSlide = Math.Max(0.4, duration / slides.Count);
        var listPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $"concat-{segment.Number:00}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var writer = new StreamWriter(listPath))
        {
            foreach (var slide in slides)
            {
                writer.WriteLine($"file '{Escape(slide.ImagePath)}'");
                writer.WriteLine($"duration {perSlide.ToString("0.000", CultureInfo.InvariantCulture)}");
            }

            writer.WriteLine($"file '{Escape(slides[^1].ImagePath)}'");
        }

        FfmpegProcess.Run([
            "-y",
            "-fflags", "+genpts",
            "-f", "concat",
            "-safe", "0",
            "-i", listPath,
            "-i", segment.AudioPath,
            "-fps_mode", "vfr",
            "-vf", $"scale={options.VideoWidth}:{options.VideoHeight}:force_original_aspect_ratio=decrease,pad={options.VideoWidth}:{options.VideoHeight}:(ow-iw)/2:(oh-ih)/2,fps={options.VideoFrameRate},format=yuv420p",
            "-c:v", "libx264",
            "-tune", "stillimage",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            "-movflags", "+faststart",
            outputPath
        ]);

        return outputPath;
    }

    public string Merge(IReadOnlyList<string> videoPaths, string outputPath)
    {
        if (videoPaths.Count == 0)
        {
            throw new InvalidOperationException("No segment videos to merge.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var listPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "concat-all.txt");
        File.WriteAllLines(listPath, videoPaths.Select(path => $"file '{Escape(path)}'"));

        try
        {
            FfmpegProcess.Run([
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                outputPath
            ]);
        }
        catch
        {
            FfmpegProcess.Run([
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", listPath,
                "-c:v", "libx264",
                "-c:a", "aac",
                "-pix_fmt", "yuv420p",
                "-movflags", "+faststart",
                outputPath
            ]);
        }

        return outputPath;
    }

    private static string Escape(string path) => path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", @"'\''", StringComparison.Ordinal);
}
