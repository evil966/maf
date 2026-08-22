namespace NarrationStudio.Service;

public static class JobPaths
{
    public static string JobsRoot(string outputRoot) =>
        Path.Combine(outputRoot, "jobs");

    public static string JobDirectory(string outputRoot, string jobId) =>
        Path.Combine(JobsRoot(outputRoot), jobId);

    public static string InputsDirectory(string jobDirectory) =>
        Path.Combine(jobDirectory, "inputs");

    public static string TranscriptFile(string jobDirectory) =>
        Path.Combine(InputsDirectory(jobDirectory), "transcript.txt");

    public static string StubNarrationFile(string jobDirectory) =>
        Path.Combine(InputsDirectory(jobDirectory), "stub-narration.txt");

    public static string ResolveOutputRoot(string configured, string currentDirectory)
    {
        var folder = string.IsNullOrWhiteSpace(configured) ? "output" : configured;
        return Path.IsPathRooted(folder)
            ? folder
            : Path.GetFullPath(Path.Combine(currentDirectory, folder));
    }
}
