namespace RaceTelemetry.Desktop.Services;

public sealed class ChatThreadIdentity : IChatThreadIdentity
{
    private const string PreferenceKey = "race-telemetry.agui.thread-id";

    public string GetOrCreate()
    {
        var existing = Preferences.Default.Get(PreferenceKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var created = Guid.CreateVersion7().ToString();
        Preferences.Default.Set(PreferenceKey, created);
        return created;
    }

    public string Replace()
    {
        var created = Guid.CreateVersion7().ToString();
        Preferences.Default.Set(PreferenceKey, created);
        return created;
    }
}
