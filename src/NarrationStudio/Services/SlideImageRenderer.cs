using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using NarrationStudio.Configuration;
using NarrationStudio.Models;

namespace NarrationStudio.Services;

public sealed class SlideImageRenderer
{
    public string Render(NarrationSegment segment, SlideSpec slide, StudioOptions options, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var width = options.VideoWidth;
        var height = options.VideoHeight;
        var fontFamily = ResolveFont();
        var titleFont = fontFamily.CreateFont(Math.Max(28, width / 38f), FontStyle.Bold);
        var bodyFont = fontFamily.CreateFont(Math.Max(36, width / 24f), FontStyle.Regular);
        var footerFont = fontFamily.CreateFont(Math.Max(18, width / 64f), FontStyle.Regular);

        using var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("0F1C2E"));
            ctx.Fill(Color.ParseHex("1B3A4B"), new RectangleF(0, 0, width, height * 0.18f));
            ctx.Fill(Color.ParseHex("C9A227"), new RectangleF(0, height * 0.18f, width, 8));

            var header = $"SEGMENT {segment.Number:00}  ·  SLIDE {slide.Number}/{Math.Max(1, segment.Slides.Count)}";
            ctx.DrawText(header, titleFont, Color.ParseHex("F4E9D0"), new PointF(80, 70));

            var wrapped = Wrap(slide.Phrase, bodyFont, width - 200);
            var textOptions = new RichTextOptions(bodyFont)
            {
                Origin = new PointF(100, height * 0.38f),
                WrappingLength = width - 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                LineSpacing = 1.15f
            };
            ctx.DrawText(textOptions, wrapped, Color.White);

            var footer = string.IsNullOrWhiteSpace(segment.Title)
                ? "Narration Studio"
                : segment.Title;
            ctx.DrawText(footer, footerFont, Color.ParseHex("9DB4C0"), new PointF(80, height - 80));
        });

        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        var bytes = buffer.ToArray();
        WriteAllBytesWithRetry(outputPath, bytes);
        return outputPath;
    }

    private static void WriteAllBytesWithRetry(string path, byte[] bytes)
    {
        IOException? last = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException ex) when (attempt < 6)
            {
                last = ex;
                Thread.Sleep(200 * attempt);
            }
            catch (IOException ex)
            {
                last = ex;
            }
        }

        var fallback = path + $".{Guid.NewGuid():N}.png";
        File.WriteAllBytes(fallback, bytes);
        try
        {
            File.Copy(fallback, path, overwrite: true);
            File.Delete(fallback);
        }
        catch
        {
            File.Copy(fallback, path, overwrite: true);
        }

        if (!File.Exists(path))
        {
            throw last ?? new IOException($"Could not write {path}.");
        }
    }

    private static FontFamily ResolveFont()
    {
        var collection = new FontCollection();
        foreach (var candidate in new[]
                 {
                     @"C:\Windows\Fonts\segoeui.ttf",
                     @"C:\Windows\Fonts\arial.ttf",
                     @"C:\Windows\Fonts\calibri.ttf"
                 })
        {
            if (File.Exists(candidate))
            {
                return collection.Add(candidate);
            }
        }

        return SystemFonts.Families.First();
    }

    private static string Wrap(string text, Font font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";
        foreach (var word in words)
        {
            var trial = string.IsNullOrEmpty(current) ? word : current + " " + word;
            if (TextMeasurer.MeasureSize(trial, new TextOptions(font)).Width <= maxWidth)
            {
                current = trial;
            }
            else
            {
                if (!string.IsNullOrEmpty(current))
                {
                    lines.Add(current);
                }

                current = word;
            }
        }

        if (!string.IsNullOrEmpty(current))
        {
            lines.Add(current);
        }

        return string.Join('\n', lines);
    }
}
