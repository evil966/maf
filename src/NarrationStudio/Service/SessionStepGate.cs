namespace NarrationStudio.Service;

public enum SessionStepDecision
{
    NotFound,
    NotASession,
    UnknownStep,
    Busy,
    AlreadyComplete,
    AcceptAsync,
    AcceptSync
}

public sealed record SessionStepEvaluation(SessionStepDecision Decision, string? Step, string Error);

public static class SessionStepGate
{
    public static SessionStepEvaluation Evaluate(ProductionJob? job, string? requestedStep, string? requestedJobId = null)
    {
        var id = job?.JobId ?? requestedJobId ?? "";
        if (job is null)
        {
            return new SessionStepEvaluation(
                SessionStepDecision.NotFound,
                null,
                $"Session '{id}' was not found.");
        }

        if (!job.SessionMode)
        {
            return new SessionStepEvaluation(
                SessionStepDecision.NotASession,
                null,
                $"Job '{job.JobId}' is a full production job, not a session.");
        }

        var step = SessionSteps.Normalize(requestedStep);
        if (step is null)
        {
            return new SessionStepEvaluation(
                SessionStepDecision.UnknownStep,
                null,
                $"Unknown step '{requestedStep}'. Expected one of: {string.Join(", ", SessionSteps.All)}.");
        }

        var inFlight = job.InFlightSteps();
        if (inFlight.Count > 0 && !SessionSteps.AllowsConcurrent(inFlight, step))
        {
            return new SessionStepEvaluation(
                SessionStepDecision.Busy,
                step,
                $"Session '{job.JobId}' is already running step '{string.Join(", ", inFlight)}'.");
        }

        if (string.Equals(job.Status, "complete", StringComparison.OrdinalIgnoreCase) &&
            step != SessionSteps.Verify)
        {
            return new SessionStepEvaluation(
                SessionStepDecision.AlreadyComplete,
                step,
                $"Session '{job.JobId}' is already complete.");
        }

        return SessionSteps.IsAsync(step)
            ? new SessionStepEvaluation(SessionStepDecision.AcceptAsync, step, "")
            : new SessionStepEvaluation(SessionStepDecision.AcceptSync, step, "");
    }
}
