using NarrationStudio.Configuration;

namespace NarrationStudio.Service;

public sealed class ProductionJob
{
    private readonly object _stepGate = new();
    private readonly Dictionary<string, string> _steps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _stepResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _stepErrors = new(StringComparer.OrdinalIgnoreCase);

    public string JobId { get; init; } = "";
    public string? Title { get; init; }
    public string Orchestration { get; init; } = "direct";
    public string? CallbackUrl { get; init; }
    public bool KeepArtifacts { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public StudioOptions Studio { get; init; } = new();
    public OllamaOptions Model { get; init; } = new();
    public string OutputDirectory { get; init; } = "";
    public string TranscriptPath { get; init; } = "";

    public bool SessionMode { get; init; }
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public string? MergedVideoPath { get; set; }
    public string? EventsPath { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CurrentStep { get; set; }
    public string StepStatus { get; set; } = "idle";
    public string? StepResult { get; set; }

    public IReadOnlyList<string> InFlightSteps()
    {
        lock (_stepGate)
        {
            var fromMap = _steps
                .Where(pair => pair.Value is "queued" or "running")
                .Select(pair => pair.Key)
                .ToArray();
            if (fromMap.Length > 0)
            {
                return fromMap;
            }

            if (StepStatus is "queued" or "running" && !string.IsNullOrWhiteSpace(CurrentStep))
            {
                return [CurrentStep];
            }

            return [];
        }
    }

    public IReadOnlyDictionary<string, string> SnapshotSteps()
    {
        lock (_stepGate)
        {
            return new Dictionary<string, string>(_steps, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyDictionary<string, string> SnapshotStepErrors()
    {
        lock (_stepGate)
        {
            return new Dictionary<string, string>(_stepErrors, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void MarkStepStarted(string step, string status)
    {
        lock (_stepGate)
        {
            CurrentStep = step;
            StepStatus = status;
            StepResult = null;
            _steps[step] = status;
            _stepResults.Remove(step);
            _stepErrors.Remove(step);
            Status = "running";
        }
    }

    public void MarkStepSucceeded(string step, string result)
    {
        lock (_stepGate)
        {
            _steps[step] = "complete";
            _stepResults[step] = result;
            _stepErrors.Remove(step);
            StepResult = result;
            if (string.Equals(CurrentStep, step, StringComparison.OrdinalIgnoreCase))
            {
                StepStatus = "complete";
            }

            if (InFlightUnlocked().Count == 0)
            {
                if (step == SessionSteps.Verify)
                {
                    Status = "complete";
                    CompletedAt = DateTimeOffset.Now;
                }
                else
                {
                    Status = "ready";
                }
            }
            else
            {
                Status = "running";
            }
        }
    }

    public void MarkStepFailed(string step, string error)
    {
        lock (_stepGate)
        {
            _steps[step] = "failed";
            _stepErrors[step] = error;
            Error = error;
            if (string.Equals(CurrentStep, step, StringComparison.OrdinalIgnoreCase))
            {
                StepStatus = "failed";
            }

            if (InFlightUnlocked().Count == 0)
            {
                Status = "failed";
                CompletedAt = DateTimeOffset.Now;
            }
            else
            {
                Status = "running";
            }
        }
    }

    private List<string> InFlightUnlocked() =>
        _steps.Where(pair => pair.Value is "queued" or "running").Select(pair => pair.Key).ToList();
}
