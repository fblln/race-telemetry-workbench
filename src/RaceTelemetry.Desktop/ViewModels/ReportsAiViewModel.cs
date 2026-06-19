using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

public enum AiRole { User, Assistant }
public enum ReportsTab { Debrief, SessionReport, RaceStory }

public sealed record AiChatMessage(AiRole Role, string Content);

public sealed partial class ReportsAiViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly ITelemetryAgentClient _agentClient;
    private readonly ChatThreadIdentity _threadIdentity;
    private readonly AppState _appState;
    private CancellationTokenSource? _streamCts;

    private const string FollowUpMarker = "---FOLLOWUP---";

    public static IReadOnlyList<string> SuggestedPrompts { get; } =
    [
        "Tell me what happened in the race",
        "Who won and by how much?",
        "Compare pit strategies of the top 3",
        "Where did positions change hands?",
        "What was the fastest lap and who set it?",
    ];

    public ReportsAiViewModel(
        IQueryApiClient api,
        ITelemetryAgentClient agentClient,
        ChatThreadIdentity threadIdentity,
        AppState appState)
    {
        _api = api;
        _agentClient = agentClient;
        _threadIdentity = threadIdentity;
        _appState = appState;
    }

    [ObservableProperty] private ReportsTab _activeTab = ReportsTab.Debrief;
    [ObservableProperty] private RaceStoryResponse? _debrief;
    [ObservableProperty] private StandingsResponse? _standings;
    [ObservableProperty] private bool _isLoadingDebrief;
    [ObservableProperty] private string? _debriefError;
    [ObservableProperty] private bool _isChatStreaming;
    [ObservableProperty] private string _userInput = string.Empty;

    // Facts-based narrative (derived from loaded data, no AI call)
    [ObservableProperty] private string _debriefNarrative = string.Empty;

    public ObservableCollection<ChatBubble> ChatHistory { get; } = new();
    public bool CanSend => !IsChatStreaming && !string.IsNullOrWhiteSpace(UserInput);

    partial void OnIsChatStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
    }

    partial void OnUserInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SwitchTab(string? tabIndex)
    {
        if (tabIndex is not null && int.TryParse(tabIndex, out var i))
            ActiveTab = (ReportsTab)i;
    }

    [RelayCommand]
    private async Task LoadAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || IsLoadingDebrief) return;

        IsLoadingDebrief = true;
        DebriefError = null;
        try
        {
            var storyTask    = _api.GetRaceStoryAsync(sessionId);
            var standingsTask = _api.GetStandingsAsync(sessionId);
            await Task.WhenAll(storyTask, standingsTask);
            Debrief   = storyTask.Result;
            Standings = standingsTask.Result;
            if (Debrief is null) DebriefError = "No story data available for this session.";
            else DebriefNarrative = BuildNarrative(Debrief, Standings);
        }
        catch (Exception ex)
        {
            DebriefError = $"Could not load session story: {ex.Message}";
        }
        finally
        {
            IsLoadingDebrief = false;
        }
    }

    private static string BuildNarrative(RaceStoryResponse d, StandingsResponse? standings)
    {
        var sb = new StringBuilder();

        var winner = standings?.Items.FirstOrDefault(e => e.Position == 1);
        if (winner is not null)
        {
            sb.Append($"**{winner.FullName ?? winner.DriverCode}** won");
            var winnerStints = d.Stints.Where(s => s.DriverCode == winner.DriverCode).ToList();
            if (winnerStints.Count > 1)
            {
                var compounds = winnerStints
                    .Select(s => s.Compound?.ToUpperInvariant())
                    .Where(c => c is not null)
                    .Distinct();
                sb.Append($" on a {winnerStints.Count - 1}-stop {string.Join("–", compounds)} strategy");
            }
            sb.Append('.');
        }

        // Key SC/VSC/red flag moment
        var keyEvent = d.RaceControlMessages.FirstOrDefault(m =>
            m.LapNumber.HasValue &&
            (m.Category is "SafetyCar" ||
             m.Message.Contains("SAFETY CAR", StringComparison.OrdinalIgnoreCase) ||
             m.Message.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase) ||
             m.Flag is "RED"));
        if (keyEvent is not null)
            sb.Append($" **{keyEvent.Message.ToLowerInvariant().Replace("safety car", "SC").Replace("virtual safety car", "VSC")}** (L{keyEvent.LapNumber}).");

        // Most pit stops driver (anomaly detector)
        var maxStops = d.Stints.GroupBy(s => s.DriverCode)
            .Select(g => (Driver: g.Key, Stints: g.Count()))
            .OrderByDescending(x => x.Stints)
            .FirstOrDefault();
        if (maxStops.Stints > 4)
            sb.Append($" {maxStops.Driver} ran {maxStops.Stints - 1} stops — the most in the field.");

        return sb.Length > 0 ? sb.ToString() : string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = UserInput.Trim();
        if (string.IsNullOrEmpty(question)) return;

        UserInput = string.Empty;
        var assistantBubble = new ChatBubble(AiRole.Assistant, string.Empty) { IsStreaming = true };
        ChatHistory.Add(new ChatBubble(AiRole.User, question));
        ChatHistory.Add(assistantBubble);

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        IsChatStreaming = true;

        var threadId = _threadIdentity.GetOrCreate();
        var context  = BuildWorkspaceContext();

        try
        {
            string? activeToolName = null;
            await foreach (var evt in _agentClient.RunAsync(threadId, question, context, _streamCts.Token))
            {
                switch (evt.Type)
                {
                    case "TEXT_MESSAGE_CONTENT":
                        if (!string.IsNullOrEmpty(evt.Delta))
                            assistantBubble.Content += evt.Delta;
                        break;
                    case "TOOL_CALL_START":
                        activeToolName = evt.ToolCallName;
                        if (!string.IsNullOrEmpty(activeToolName))
                            assistantBubble.AddToolActivity(activeToolName, running: true);
                        break;
                    case "TOOL_CALL_END":
                        if (activeToolName is not null)
                        {
                            assistantBubble.CompleteToolActivity(activeToolName);
                            activeToolName = null;
                        }
                        break;
                    case "RUN_ERROR":
                        assistantBubble.Content = $"Error: {evt.Message ?? evt.Code ?? "Unknown error"}";
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { assistantBubble.Content = $"Error: {ex.Message}"; }
        finally
        {
            assistantBubble.IsStreaming = false;
            IsChatStreaming = false;
            // Parse and strip follow-up suggestions from the response
            ParseFollowUps(assistantBubble);
        }
    }

    private static void ParseFollowUps(ChatBubble bubble)
    {
        var content = bubble.Content;
        var markerIdx = content.IndexOf(FollowUpMarker, StringComparison.Ordinal);
        if (markerIdx < 0) return;

        bubble.Content = content[..markerIdx].TrimEnd();
        var jsonPart = content[(markerIdx + FollowUpMarker.Length)..].Trim();
        try
        {
            var questions = JsonSerializer.Deserialize<string[]>(jsonPart);
            if (questions is not null)
                foreach (var q in questions)
                    bubble.AddFollowUp(q);
        }
        catch { /* ignore malformed follow-up JSON */ }
    }

    [RelayCommand]
    private async Task SendSuggestedAsync(string prompt)
    {
        UserInput = prompt;
        await SendAsync();
    }

    [RelayCommand]
    private void CancelStream() => _streamCts?.Cancel();

    [RelayCommand(CanExecute = nameof(IsNotStreaming))]
    private async Task NewConversationAsync()
    {
        _streamCts?.Cancel();
        var threadId = _threadIdentity.GetOrCreate();
        await _agentClient.ResetAsync(threadId, CancellationToken.None);
        _threadIdentity.Replace();
        ChatHistory.Clear();
    }

    [RelayCommand]
    private async Task ExportChatAsync()
    {
        if (ChatHistory.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# Race Story — {_appState.SessionId ?? "session"}");
        sb.AppendLine();

        foreach (var bubble in ChatHistory)
        {
            if (bubble.IsUser)
            {
                sb.AppendLine($"**You:** {bubble.Content}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(bubble.Content);
                if (bubble.ToolActivities.Count > 0)
                {
                    sb.Append("*Tools: ");
                    sb.Append(string.Join(", ", bubble.ToolActivities.Select(t => t.Name)));
                    sb.AppendLine("*");
                }
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        var md = sb.ToString();
        var fileName = $"race-story-{_appState.SessionId ?? "export"}.md";

        try
        {
            var path = Path.Combine(FileSystem.CacheDirectory, fileName);
            await File.WriteAllTextAsync(path, md);
            await Share.RequestAsync(new ShareFileRequest
            {
                Title = "Export race story",
                File  = new ShareFile(path, "text/markdown"),
            });
        }
        catch { await Clipboard.SetTextAsync(md); }
    }

    private bool IsNotStreaming => !IsChatStreaming;

    private TelemetryWorkspaceContext BuildWorkspaceContext() =>
        new(
            SessionKey: _appState.SessionId,
            SelectedDrivers: _appState.SelectedDrivers.Count > 0 ? _appState.SelectedDrivers.AsReadOnly() : null,
            SelectedLap: null,
            SelectedCorner: null,
            WindowStart: null,
            WindowEnd: null,
            ActiveView: "reports-ai");
}

public sealed partial class ChatBubble : ObservableObject
{
    private readonly List<ToolActivity> _toolActivities = new();
    private readonly List<string> _followUps = new();

    public ChatBubble(AiRole role, string content)
    {
        Role = role;
        _content = content;
    }

    public AiRole Role { get; }
    public bool IsUser => Role == AiRole.User;

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;

    public IReadOnlyList<ToolActivity> ToolActivities => _toolActivities;
    public IReadOnlyList<string> FollowUps => _followUps;

    public void AddToolActivity(string toolName, bool running)
    {
        _toolActivities.Add(new ToolActivity(toolName, running));
        OnPropertyChanged(nameof(ToolActivities));
    }

    public void CompleteToolActivity(string toolName)
    {
        var activity = _toolActivities.LastOrDefault(a => a.Name == toolName && a.IsRunning);
        if (activity is not null)
        {
            activity.IsRunning = false;
            OnPropertyChanged(nameof(ToolActivities));
        }
    }

    public void AddFollowUp(string question)
    {
        _followUps.Add(question);
        OnPropertyChanged(nameof(FollowUps));
    }
}

public sealed partial class ToolActivity : ObservableObject
{
    public ToolActivity(string name, bool isRunning)
    {
        Name = name;
        _isRunning = isRunning;
    }

    public string Name { get; }
    [ObservableProperty] private bool _isRunning;
}
