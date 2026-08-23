using Microsoft.Extensions.Options;
using NarrationStudio.Configuration;
using NarrationStudio.Models;
using NarrationStudio.Service;
using NarrationStudio.Services;

namespace NarrationStudio.Tests;

public sealed class SessionStepsTests
{
    [Theory]
    [InlineData("rewrite", SessionSteps.Rewrite)]
    [InlineData("PLAN-SLIDES", SessionSteps.PlanSlides)]
    [InlineData(" tts ", SessionSteps.Tts)]
    [InlineData("verify", SessionSteps.Verify)]
    public void Normalize_accepts_known_steps(string input, string expected)
    {
        Assert.Equal(expected, SessionSteps.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("handoff")]
    [InlineData("plan_slides")]
    public void Normalize_rejects_unknown_steps(string? input)
    {
        Assert.Null(SessionSteps.Normalize(input));
    }

    [Theory]
    [InlineData(SessionSteps.PlanSlides, true)]
    [InlineData(SessionSteps.Tts, true)]
    [InlineData(SessionSteps.Rewrite, false)]
    [InlineData(SessionSteps.Segment, false)]
    [InlineData(SessionSteps.Render, false)]
    [InlineData(SessionSteps.Compose, false)]
    [InlineData(SessionSteps.Merge, false)]
    [InlineData(SessionSteps.Verify, false)]
    public void Only_plan_slides_and_tts_are_async(string step, bool expected)
    {
        Assert.Equal(expected, SessionSteps.IsAsync(step));
    }
}

public sealed class SessionStepGateTests
{
    [Fact]
    public void Missing_job_is_not_found()
    {
        var result = SessionStepGate.Evaluate(null, SessionSteps.Rewrite, "missing");

        Assert.Equal(SessionStepDecision.NotFound, result.Decision);
        Assert.Contains("missing", result.Error);
    }

    [Fact]
    public void Full_production_job_cannot_run_session_steps()
    {
        var job = NewJob(sessionMode: false);

        var result = SessionStepGate.Evaluate(job, SessionSteps.Rewrite);

        Assert.Equal(SessionStepDecision.NotASession, result.Decision);
    }

    [Fact]
    public void Unknown_step_is_rejected()
    {
        var result = SessionStepGate.Evaluate(NewJob(), "explode");

        Assert.Equal(SessionStepDecision.UnknownStep, result.Decision);
        Assert.Contains("rewrite", result.Error);
    }

    [Fact]
    public void Busy_session_cannot_start_another_step()
    {
        var job = NewJob();
        job.CurrentStep = SessionSteps.Tts;
        job.StepStatus = "running";

        var result = SessionStepGate.Evaluate(job, SessionSteps.Compose);

        Assert.Equal(SessionStepDecision.Busy, result.Decision);
        Assert.Contains(SessionSteps.Tts, result.Error);
    }

    [Fact]
    public void Complete_session_blocks_non_verify_steps()
    {
        var job = NewJob();
        job.Status = "complete";

        Assert.Equal(SessionStepDecision.AlreadyComplete, SessionStepGate.Evaluate(job, SessionSteps.Merge).Decision);
        Assert.Equal(SessionStepDecision.AcceptSync, SessionStepGate.Evaluate(job, SessionSteps.Verify).Decision);
    }

    [Fact]
    public void Routes_plan_slides_and_tts_as_async_and_others_as_sync()
    {
        var job = NewJob();

        Assert.Equal(SessionStepDecision.AcceptAsync, SessionStepGate.Evaluate(job, "plan-slides").Decision);
        Assert.Equal(SessionStepDecision.AcceptAsync, SessionStepGate.Evaluate(job, "TTS").Decision);
        Assert.Equal(SessionStepDecision.AcceptSync, SessionStepGate.Evaluate(job, "render").Decision);
    }

    [Fact]
    public void Failed_session_can_retry_a_step()
    {
        var job = NewJob();
        job.Status = "failed";
        job.StepStatus = "failed";
        job.CurrentStep = SessionSteps.Rewrite;

        Assert.Equal(SessionStepDecision.AcceptSync, SessionStepGate.Evaluate(job, SessionSteps.Rewrite).Decision);
    }

    [Fact]
    public void Render_can_start_while_tts_is_running()
    {
        var job = NewJob();
        job.MarkStepStarted(SessionSteps.Tts, "running");

        Assert.Equal(SessionStepDecision.AcceptSync, SessionStepGate.Evaluate(job, SessionSteps.Render).Decision);
    }

    [Fact]
    public void Tts_can_start_while_render_is_running()
    {
        var job = NewJob();
        job.MarkStepStarted(SessionSteps.Render, "running");

        Assert.Equal(SessionStepDecision.AcceptAsync, SessionStepGate.Evaluate(job, SessionSteps.Tts).Decision);
    }

    [Fact]
    public void Compose_stays_busy_while_tts_is_running()
    {
        var job = NewJob();
        job.MarkStepStarted(SessionSteps.Tts, "running");

        var result = SessionStepGate.Evaluate(job, SessionSteps.Compose);

        Assert.Equal(SessionStepDecision.Busy, result.Decision);
        Assert.Contains(SessionSteps.Tts, result.Error);
    }

    [Fact]
    public void Second_rewrite_stays_busy()
    {
        var job = NewJob();
        job.MarkStepStarted(SessionSteps.Rewrite, "running");

        Assert.Equal(SessionStepDecision.Busy, SessionStepGate.Evaluate(job, SessionSteps.Rewrite).Decision);
    }

    [Fact]
    public void Failed_render_does_not_fail_the_job_while_tts_is_still_running()
    {
        var job = NewJob();
        job.MarkStepStarted(SessionSteps.Tts, "running");
        job.MarkStepStarted(SessionSteps.Render, "running");
        job.MarkStepFailed(SessionSteps.Render, "Skia failed");

        Assert.Equal("running", job.Status);
        Assert.Equal("failed", job.SnapshotSteps()[SessionSteps.Render]);
        Assert.Equal("running", job.SnapshotSteps()[SessionSteps.Tts]);
        Assert.Equal(SessionStepDecision.Busy, SessionStepGate.Evaluate(job, SessionSteps.Compose).Decision);
    }

    private static ProductionJob NewJob(bool sessionMode = true) =>
        new()
        {
            JobId = "session-a",
            SessionMode = sessionMode,
            Status = "ready",
            StepStatus = "idle",
            OutputDirectory = @"E:\exercises\maf\output\jobs\session-a"
        };
}

public sealed class SessionIsolationTests
{
    [Fact]
    public void JobStore_keeps_sessions_isolated()
    {
        var store = new JobStore();
        var first = new ProductionJob
        {
            JobId = "one",
            SessionMode = true,
            Status = "ready",
            OutputDirectory = @"E:\tmp\one"
        };
        var second = new ProductionJob
        {
            JobId = "two",
            SessionMode = true,
            Status = "ready",
            OutputDirectory = @"E:\tmp\two"
        };

        store.Add(first);
        store.Add(second);
        first.CurrentStep = SessionSteps.Rewrite;
        first.StepStatus = "running";

        Assert.Same(first, store.Get("one"));
        Assert.Same(second, store.Get("two"));
        Assert.Null(store.Get("two")!.CurrentStep);
        Assert.Equal("idle", store.Get("two")!.StepStatus);
        Assert.Equal(@"E:\tmp\one", store.Get("one")!.OutputDirectory);
        Assert.Equal(@"E:\tmp\two", store.Get("two")!.OutputDirectory);
        Assert.Throws<InvalidOperationException>(() => store.Add(new ProductionJob { JobId = "one" }));
    }

    [Fact]
    public void PipelineStore_attach_loads_only_that_session_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "narration-studio-session-iso-" + Guid.NewGuid().ToString("N"));
        var firstDir = Path.Combine(root, "jobs", "one");
        var secondDir = Path.Combine(root, "jobs", "two");
        Directory.CreateDirectory(firstDir);
        Directory.CreateDirectory(secondDir);
        var firstTranscript = Path.Combine(firstDir, "t1.txt");
        var secondTranscript = Path.Combine(secondDir, "t2.txt");
        File.WriteAllText(firstTranscript, "first talk");
        File.WriteAllText(secondTranscript, "second talk");

        try
        {
            var first = new PipelineStore(Options.Create(new StudioOptions()));
            first.Initialize(firstTranscript, firstDir);
            first.Mutate(state => state.NarrationPath = Path.Combine(firstDir, "one.txt"));

            var second = new PipelineStore(Options.Create(new StudioOptions()));
            second.Initialize(secondTranscript, secondDir);
            second.Mutate(state => state.NarrationPath = Path.Combine(secondDir, "two.txt"));

            var attached = new PipelineStore(Options.Create(new StudioOptions()));
            Assert.True(attached.AttachExisting(firstDir));
            Assert.Equal(Path.GetFullPath(firstDir), attached.OutputDirectory);
            Assert.Equal(Path.Combine(firstDir, "one.txt"), attached.Snapshot().NarrationPath);
            Assert.NotEqual(Path.GetFullPath(secondDir), attached.OutputDirectory);

            Assert.True(attached.AttachExisting(secondDir));
            Assert.Equal(Path.GetFullPath(secondDir), attached.OutputDirectory);
            Assert.Equal(Path.Combine(secondDir, "two.txt"), attached.Snapshot().NarrationPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Job_folders_do_not_share_input_paths()
    {
        var root = JobPaths.ResolveOutputRoot("output", @"E:\exercises\maf");
        var one = JobPaths.JobDirectory(root, "one");
        var two = JobPaths.JobDirectory(root, "two");

        Assert.NotEqual(one, two);
        Assert.Equal(Path.Combine(root, "jobs", "one", "inputs", "transcript.txt"), JobPaths.TranscriptFile(one));
        Assert.Equal(Path.Combine(root, "jobs", "two", "inputs", "transcript.txt"), JobPaths.TranscriptFile(two));
    }
}

public sealed class PipelineSegmentPatchTests
{
    [Fact]
    public void ApplySlides_keeps_audio_paths_already_written_by_tts()
    {
        var current = new PipelineState
        {
            Segments =
            [
                new NarrationSegment
                {
                    Number = 1,
                    AudioPath = @"E:\tmp\audio-01.mp3",
                    AudioDurationSeconds = 12.5,
                    Slides =
                    [
                        new SlideSpec { Number = 1, Phrase = "Old", SourcePhrase = "Old source", PhraseRewritten = true }
                    ]
                }
            ]
        };
        NarrationSegment[] rendered =
        [
            new NarrationSegment
            {
                Number = 1,
                Slides =
                [
                    new SlideSpec
                    {
                        Number = 1,
                        Phrase = "New sentence",
                        SourcePhrase = "Old source",
                        PhraseRewritten = true,
                        ImagePath = @"E:\tmp\segment-01-slide-01.png"
                    }
                ]
            }
        ];

        PipelineSegmentPatch.ApplySlides(current, rendered);

        Assert.Equal(@"E:\tmp\audio-01.mp3", current.Segments[0].AudioPath);
        Assert.Equal(12.5, current.Segments[0].AudioDurationSeconds);
        Assert.Equal(@"E:\tmp\segment-01-slide-01.png", current.Segments[0].Slides[0].ImagePath);
        Assert.Equal("New sentence", current.Segments[0].Slides[0].Phrase);
        Assert.Equal("slides-rendered", current.Status);
    }
}
