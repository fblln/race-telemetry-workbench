using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.UiHarness;

/// <summary>Non-persistent thread id for the test harness (no MAUI Preferences).</summary>
public sealed class InMemoryChatThreadIdentity : IChatThreadIdentity
{
    private string _id = Guid.NewGuid().ToString();
    public string GetOrCreate() => _id;
    public string Replace() => _id = Guid.NewGuid().ToString();
}
