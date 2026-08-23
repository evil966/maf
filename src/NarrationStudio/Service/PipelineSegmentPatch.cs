using NarrationStudio.Models;

namespace NarrationStudio.Service;

public static class PipelineSegmentPatch
{
    public static void ApplySlides(PipelineState current, IReadOnlyList<NarrationSegment> rendered)
    {
        foreach (var segment in rendered)
        {
            var match = current.Segments.First(s => s.Number == segment.Number);
            foreach (var slide in segment.Slides)
            {
                var dest = match.Slides.FirstOrDefault(s => s.Number == slide.Number);
                if (dest is null)
                {
                    match.Slides.Add(slide);
                    continue;
                }

                dest.ImagePath = slide.ImagePath;
                dest.Phrase = slide.Phrase;
                dest.PhraseRewritten = slide.PhraseRewritten;
                dest.SourcePhrase = slide.SourcePhrase;
            }
        }

        current.Status = "slides-rendered";
    }
}
