namespace RaceTelemetry.Desktop.Services;

/// <summary>
/// Persisted AG-UI thread id. The MAUI app backs this with Preferences; the Blazor Server
/// test harness uses an in-memory implementation.
/// </summary>
public interface IChatThreadIdentity
{
    string GetOrCreate();
    string Replace();
}
