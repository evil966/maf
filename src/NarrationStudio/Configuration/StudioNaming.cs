namespace NarrationStudio.Configuration;

public static class StudioNaming
{
    public static string AudioRange(int segmentCount) =>
        $"audio-01.mp3 ... audio-{Math.Max(1, segmentCount):00}.mp3";

    public static string VideoRange(int segmentCount) =>
        $"video-01.mp4 ... video-{Math.Max(1, segmentCount):00}.mp4";
}
