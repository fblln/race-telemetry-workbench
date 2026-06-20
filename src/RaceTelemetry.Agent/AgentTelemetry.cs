using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RaceTelemetry.Agent;

public static class AgentTelemetry
{
    public const string SourceName = "RaceTelemetry.Agent";

    public static readonly ActivitySource Activities = new(SourceName, "1.0.0");

    private static readonly Meter Meter = new(SourceName, "1.0.0");

    // Counters
    public static readonly Counter<long> RunsStarted   = Meter.CreateCounter<long>("agent.runs.started");
    public static readonly Counter<long> RunsFinished  = Meter.CreateCounter<long>("agent.runs.finished");
    public static readonly Counter<long> RunsFailed    = Meter.CreateCounter<long>("agent.runs.failed");
    public static readonly Counter<long> ToolCalls     = Meter.CreateCounter<long>("agent.tool.calls");
    public static readonly Counter<long> ToolFailures  = Meter.CreateCounter<long>("agent.tool.failures");
    public static readonly Counter<long> LlmCalls      = Meter.CreateCounter<long>("agent.llm.calls");
    public static readonly Counter<long> ParallelToolBatches = Meter.CreateCounter<long>("agent.tool.parallel_batches");
    public static readonly Counter<long> ClaimsVerified = Meter.CreateCounter<long>("agent.claims.verified");
    public static readonly Counter<long> ClaimsRejected = Meter.CreateCounter<long>("agent.claims.rejected");

    // Histograms
    public static readonly Histogram<double> RunDuration      = Meter.CreateHistogram<double>("agent.run.duration_ms",   "ms");
    public static readonly Histogram<double> LlmTtft          = Meter.CreateHistogram<double>("agent.llm.ttft_ms",       "ms", "Time to first token from the model");
    public static readonly Histogram<double> LlmStreamDuration= Meter.CreateHistogram<double>("agent.llm.stream_ms",     "ms", "Full streaming duration per LLM call");
    public static readonly Histogram<double> ToolDuration     = Meter.CreateHistogram<double>("agent.tool.duration_ms",  "ms");

    // Gauges
    public static readonly ObservableGauge<int> ActiveSessions = Meter.CreateObservableGauge<int>(
        "agent.sessions.active", () => SessionCount, "sessions");

    public static int SessionCount;
}
