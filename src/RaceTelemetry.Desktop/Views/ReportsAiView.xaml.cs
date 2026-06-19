using System.ComponentModel;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class ReportsAiView : ContentView
{
    // ── Carbon Signal tokens ──────────────────────────────────────────────────
    private static readonly Color BgCanvas   = Color.FromArgb("#14110E");
    private static readonly Color BgRaised   = Color.FromArgb("#1E1916");
    private static readonly Color BgCard     = Color.FromArgb("#141210");
    private static readonly Color Border1    = Color.FromArgb("#2E2820");
    private static readonly Color Border2    = Color.FromArgb("#3A3128");
    private static readonly Color Accent     = Color.FromArgb("#FFA60D");
    private static readonly Color AccentMuted= Color.FromArgb("#2A1E08");
    private static readonly Color Green      = Color.FromArgb("#27D98C");
    private static readonly Color Red        = Color.FromArgb("#E0524A");
    private static readonly Color TextPri    = Color.FromArgb("#F4EEE6");
    private static readonly Color TextSec    = Color.FromArgb("#BCB1A2");
    private static readonly Color TextMuted  = Color.FromArgb("#7A736B");

    private ReportsAiViewModel? _vm;
    private View? _debriefTab;
    private View? _sessionReportTab;
    private View? _chatTab;

    public ReportsAiView()
    {
        InitializeComponent();
    }

    public void Initialize(string sessionId, ReportsAiViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        SwapTab(vm.ActiveTab);
        if (vm.Debrief is null)
            _ = vm.LoadCommand.ExecuteAsync(sessionId);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsAiViewModel.ActiveTab))
            SwapTab(_vm!.ActiveTab);
    }

    private void SwapTab(ReportsTab tab)
    {
        void SetActive(Border b, bool active)
        {
            b.BackgroundColor = active ? Accent : Colors.Transparent;
            b.Stroke = active ? Accent : Border2;
            if (b.Content is Label lbl)
                lbl.TextColor = active ? BgCanvas : TextSec;
        }

        SetActive(TabDebrief,       tab == ReportsTab.Debrief);
        SetActive(TabSessionReport, tab == ReportsTab.SessionReport);
        SetActive(TabRaceStory,     tab == ReportsTab.RaceStory);

        TabHost.Content = tab switch
        {
            ReportsTab.Debrief        => _debriefTab       ??= BuildDebriefTab(),
            ReportsTab.SessionReport  => _sessionReportTab ??= BuildSessionReportTab(),
            ReportsTab.RaceStory      => _chatTab          ??= BuildChatTab(),
            _                         => _debriefTab       ??= BuildDebriefTab(),
        };
    }

    // ── MCP DEBRIEF ──────────────────────────────────────────────────────────

    private View BuildDebriefTab()
    {
        var outer = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = new GridLength(240) },
            ],
            ColumnSpacing = 12,
            Padding = new Thickness(22, 0, 22, 22),
        };

        // Left: document panel
        var docPanel = BuildDocumentPanel(
            tag: "debrief",
            content: BuildDebriefCard());
        outer.Add(docPanel, 0, 0);

        // Right: source endpoints + note
        var right = BuildSourceSidebar();
        outer.Add(right, 1, 0);

        return outer;
    }

    private View BuildDebriefCard()
    {
        // Loading
        var loading = new ActivityIndicator { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        loading.SetBinding(ActivityIndicator.IsRunningProperty, nameof(ReportsAiViewModel.IsLoadingDebrief));
        loading.SetBinding(IsVisibleProperty, nameof(ReportsAiViewModel.IsLoadingDebrief));

        // Error
        var error = new Label { TextColor = Red, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        error.SetBinding(Label.TextProperty, nameof(ReportsAiViewModel.DebriefError));
        error.SetBinding(IsVisibleProperty, new Binding(nameof(ReportsAiViewModel.DebriefError),
            converter: (IValueConverter)Application.Current!.Resources["IsNotNull"]));

        // Content (built once data arrives via property-changed)
        var contentHost = new ContentView();

        void Rebuild()
        {
            if (_vm?.Debrief is not { } d) return;
            contentHost.Content = BuildDebriefSections(d);
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ReportsAiViewModel.Debrief)) Rebuild();
            };
            Rebuild();
        }

        var invertBool = (IValueConverter)Application.Current!.Resources["InvertedBoolConverter"];
        var isNotNull  = (IValueConverter)Application.Current!.Resources["IsNotNull"];

        contentHost.SetBinding(IsVisibleProperty, new Binding(nameof(ReportsAiViewModel.Debrief), converter: isNotNull));

        var wrapper = new Grid();
        wrapper.Children.Add(loading);
        wrapper.Children.Add(error);
        wrapper.Children.Add(contentHost);
        return wrapper;
    }

    private View BuildDebriefSections(RaceStoryResponse d)
    {
        var layout = new VerticalStackLayout { Spacing = 20 };

        // Title block
        var session = d.Session;
        var weather = d.Weather;
        var dryWet  = weather is null ? string.Empty : (weather.RainfallObserved ? "wet" : "dry");
        var tempStr = weather is null ? string.Empty : $", {weather.AirTempMinC:0}–{weather.AirTempMaxC:0}°C air";
        var subtitle = $"{session.SessionId} · {session.DriverCount} drivers · {session.LapCount} laps{(dryWet.Length > 0 ? $" · {dryWet}{tempStr}" : string.Empty)}";

        layout.Add(new VerticalStackLayout
        {
            Spacing = 3,
            Children =
            {
                new Label { Text = $"{session.EventName} — race debrief", FontFamily = "InterSemiBold", FontSize = 18, TextColor = TextPri },
                new Label { Text = subtitle, FontFamily = "JetBrainsMono", FontSize = 11, TextColor = TextMuted },
            },
        });

        // WHAT HAPPENED — facts-based narrative derived from loaded standings/stint data
        var narrativeLabel = new MarkdownLabel { LineBreakMode = LineBreakMode.WordWrap };
        narrativeLabel.SetBinding(MarkdownLabel.MarkdownTextProperty, nameof(ReportsAiViewModel.DebriefNarrative));
        layout.Add(SectionBlock("WHAT HAPPENED", narrativeLabel));

        // RESULT — from standings
        if (_vm?.Standings?.Items is { Count: > 0 } standings)
        {
            var first = standings.FirstOrDefault(e => e.Position == 1);
            var last  = standings.MaxBy(e => e.Position);
            if (first is not null)
            {
                var rows = new VerticalStackLayout { Spacing = 6 };
                rows.Add(PositionRow("Race winner", first.DriverCode, "P1", Green));
                if (last is not null && last.DriverCode != first.DriverCode)
                    rows.Add(PositionRow("Classified last", last.DriverCode, $"P{last.Position}", TextMuted));
                layout.Add(SectionBlock("RESULT", rows));
            }
        }

        // KEY INCIDENTS — real incidents only, no blue flags / routine clears
        var rcMessages = d.RaceControlMessages
            .Where(m => m.LapNumber.HasValue && !string.IsNullOrWhiteSpace(m.Message))
            .Where(m => m.Flag is "RED" ||
                        m.Category is "SafetyCar" ||
                        m.Message.Contains("SAFETY CAR", StringComparison.OrdinalIgnoreCase) ||
                        m.Message.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase) ||
                        m.Message.Contains("RED FLAG", StringComparison.OrdinalIgnoreCase) ||
                        (m.Flag is "YELLOW" && (
                            m.Message.Contains("ACCIDENT", StringComparison.OrdinalIgnoreCase) ||
                            m.Message.Contains("INCIDENT", StringComparison.OrdinalIgnoreCase) ||
                            m.Message.Contains("DEBRIS", StringComparison.OrdinalIgnoreCase))))
            .OrderBy(m => m.LapNumber)
            .GroupBy(m => m.Message)
            .Select(g => g.First())
            .Take(6)
            .ToList();

        if (rcMessages.Count > 0)
        {
            var incidentRows = new VerticalStackLayout { Spacing = 8 };
            foreach (var msg in rcMessages)
            {
                var dotColor = msg.Flag is "RED" ? Red
                             : msg.Flag is "SC" or "VSC" || msg.Category is "SafetyCar" ? Accent
                             : TextMuted;
                incidentRows.Add(IncidentRow($"L{msg.LapNumber}", dotColor, msg.Message));
            }
            layout.Add(SectionBlock("KEY INCIDENTS", incidentRows));
        }

        return layout;
    }

    // ── SESSION REPORT ────────────────────────────────────────────────────────

    private View BuildSessionReportTab()
    {
        var contentHost = new ContentView { Padding = new Thickness(22, 0, 22, 22) };

        void Rebuild()
        {
            if (_vm?.Debrief is not { } d) return;
            contentHost.Content = BuildDocumentPanel("session report", BuildSessionReportCard(d));
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ReportsAiViewModel.Debrief)
                    or nameof(ReportsAiViewModel.Standings))
                    Rebuild();
            };
            Rebuild();
        }

        var loading = new ActivityIndicator { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        loading.SetBinding(ActivityIndicator.IsRunningProperty, nameof(ReportsAiViewModel.IsLoadingDebrief));
        loading.SetBinding(IsVisibleProperty, nameof(ReportsAiViewModel.IsLoadingDebrief));

        var wrapper = new Grid { Padding = new Thickness(22, 0, 22, 22) };
        wrapper.Children.Add(loading);
        wrapper.Children.Add(contentHost);
        return wrapper;
    }

    private View BuildSessionReportCard(RaceStoryResponse d)
    {
        var layout = new VerticalStackLayout { Spacing = 20 };

        // Title
        layout.Add(new Label
        {
            Text = $"{d.Session.EventName} — session report",
            FontFamily = "InterSemiBold",
            FontSize = 18,
            TextColor = TextPri,
        });
        layout.Add(new Label
        {
            Text = $"{d.Session.SessionId} · generated from Query API aggregates",
            FontFamily = "JetBrainsMono",
            FontSize = 11,
            TextColor = TextMuted,
        });

        // Stat boxes: WINNER | FASTEST LAP | STRATEGY
        var winner       = _vm?.Standings?.Items.FirstOrDefault(e => e.Position == 1)?.DriverCode ?? "—";
        var fastestLap   = FindInsightValue(d.Insights, "fastest_lap") ?? "—";
        // Stop count = max stints a driver ran minus 1 (1 stop = 2 stints)
        var stopCount    = d.Stints.Count > 0
            ? d.Stints.GroupBy(s => s.DriverCode).Max(g => g.Count()) - 1
            : 0;
        var strategyStr  = stopCount > 0 ? $"{stopCount}-stop" : "—";

        layout.Add(new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star },
            ],
            ColumnSpacing = 8,
            Children =
            {
                StatBox("WINNER",      winner,      0),
                StatBox("FASTEST LAP", fastestLap,  1),
                StatBox("STRATEGY",    strategyStr, 2),
            },
        });

        // Tire strategy bars (aggregate across all drivers, by lap count)
        var compoundTotals = d.Stints
            .Where(s => s.Compound is not null)
            .GroupBy(s => s.Compound!)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Laps));

        if (compoundTotals.Count > 0)
        {
            var total = compoundTotals.Values.Sum();
            var barRow = new Grid { ColumnDefinitions = [], HeightRequest = 30 };
            var ordered = compoundTotals.OrderByDescending(kv => kv.Value).ToList();
            for (var i = 0; i < ordered.Count; i++)
                barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ordered[i].Value, GridUnitType.Star) });

            for (var i = 0; i < ordered.Count; i++)
            {
                var compound = ordered[i].Key;
                var col = CompoundColor(compound);
                var bar = new Border
                {
                    BackgroundColor = col.bg,
                    Stroke = col.stroke,
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = i == 0 ? new CornerRadius(4, 0, 4, 0) : (i == ordered.Count - 1 ? new CornerRadius(0, 4, 0, 4) : 0) },
                    Content = new Label
                    {
                        Text = compound.ToUpperInvariant(),
                        FontFamily = "JetBrainsMonoMedium",
                        FontSize = 11,
                        TextColor = col.text,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                    },
                };
                barRow.Add(bar, i, 0);
            }

            layout.Add(SectionBlock("TIRE STRATEGY", barRow));
        }

        // Key lap comparison from insights
        var compInsight = d.Insights.FirstOrDefault(i =>
            i.Kind.Contains("lap_comparison", StringComparison.OrdinalIgnoreCase) ||
            i.Kind.Contains("best_lap", StringComparison.OrdinalIgnoreCase) ||
            i.Kind.Contains("lap_delta", StringComparison.OrdinalIgnoreCase));

        if (compInsight is not null)
        {
            var deltaColor = compInsight.Value is < 0 ? Green : (compInsight.Value is > 0 ? Red : TextSec);
            var deltaStr   = compInsight.Value.HasValue
                ? $"{compInsight.Value:+0.000;-0.000} s"
                : string.Empty;

            var compRow = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                ],
            };
            compRow.Add(new Label { Text = compInsight.Text, FontFamily = "Inter", FontSize = 14, TextColor = TextSec }, 0, 0);
            compRow.Add(new Label { Text = deltaStr, FontFamily = "JetBrainsMonoMedium", FontSize = 14, TextColor = deltaColor, HorizontalOptions = LayoutOptions.End }, 1, 0);
            layout.Add(SectionBlock("KEY LAP COMPARISON", compRow));
        }

        // Incidents — same filtered RC messages as debrief
        var sessionRcMsgs = d.RaceControlMessages
            .Where(m => m.LapNumber.HasValue && !string.IsNullOrWhiteSpace(m.Message))
            .Where(m => m.Flag is "RED" ||
                        m.Category is "SafetyCar" ||
                        m.Message.Contains("SAFETY CAR", StringComparison.OrdinalIgnoreCase) ||
                        m.Message.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase) ||
                        m.Message.Contains("RED FLAG", StringComparison.OrdinalIgnoreCase) ||
                        (m.Flag is "YELLOW" && (
                            m.Message.Contains("ACCIDENT", StringComparison.OrdinalIgnoreCase) ||
                            m.Message.Contains("INCIDENT", StringComparison.OrdinalIgnoreCase) ||
                            m.Message.Contains("DEBRIS", StringComparison.OrdinalIgnoreCase))))
            .GroupBy(m => m.Message).Select(g => g.First())
            .Take(5).ToList();

        if (sessionRcMsgs.Count > 0)
        {
            var rows = new VerticalStackLayout { Spacing = 8 };
            foreach (var msg in sessionRcMsgs)
            {
                var dotColor = msg.Flag is "RED" ? Red
                             : msg.Category is "SafetyCar" || msg.Message.Contains("SAFETY", StringComparison.OrdinalIgnoreCase)
                                 ? Accent : TextMuted;
                rows.Add(IncidentRow($"L{msg.LapNumber}", dotColor, msg.Message));
            }
            layout.Add(SectionBlock("INCIDENTS", rows));
        }

        return layout;
    }

    // ── RACE STORY (AI) ───────────────────────────────────────────────────────

    private View BuildChatTab()
    {
        // Panel header with export button
        var exportBtn = new Button
        {
            Text = "↓ Export",
            FontFamily = "JetBrainsMono",
            FontSize = 11,
            Padding = new Thickness(10, 4),
            BackgroundColor = Color.FromArgb("#1A1714"),
            TextColor = TextSec,
            BorderColor = Border2,
            BorderWidth = 1,
            CornerRadius = 5,
        };
        exportBtn.SetBinding(Button.CommandProperty, nameof(ReportsAiViewModel.ExportChatCommand));
        exportBtn.SetBinding(IsEnabledProperty, new Binding(nameof(ReportsAiViewModel.IsChatStreaming),
            converter: (IValueConverter)Application.Current!.Resources["InvertedBoolConverter"]));

        var headerGrid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            ],
            ColumnSpacing = 8,
        };
        headerGrid.Add(new Label
        {
            Text = "□  Assistant · race story",
            FontFamily = "InterSemiBold",
            FontSize = 13,
            TextColor = TextPri,
            VerticalOptions = LayoutOptions.Center,
        }, 0, 0);
        headerGrid.Add(exportBtn, 1, 0);
        headerGrid.Add(new Border
        {
            HorizontalOptions = LayoutOptions.End,
            BackgroundColor = AccentMuted,
            Stroke = Accent,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(10, 3),
            Content = new Label { Text = "MCP · LLM", FontFamily = "JetBrainsMono", FontSize = 11, TextColor = Accent },
        }, 2, 0);

        var panel = new Border
        {
            Margin = new Thickness(22, 0, 22, 22),
            Padding = new Thickness(0),
            BackgroundColor = BgRaised,
            Stroke = Border2,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
        };

        // Suggested prompt chips — always visible at the top
        var promptChips = new VerticalStackLayout
        {
            Spacing = 6,
            Padding = new Thickness(14, 12, 14, 4),
        };
        foreach (var prompt in ReportsAiViewModel.SuggestedPrompts)
        {
            var chip = new Border
            {
                Padding = new Thickness(14, 8),
                BackgroundColor = Colors.Transparent,
                Stroke = Border2,
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Content = new Label
                {
                    Text = prompt,
                    FontFamily = "Inter",
                    FontSize = 13,
                    TextColor = TextSec,
                },
            };
            var promptCopy = prompt;
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => _vm?.SendSuggestedCommand.Execute(promptCopy)),
            });
            promptChips.Children.Add(chip);
        }

        // Bubble list
        var bubbleList = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(14, 12, 14, 12) };
        bubbleList.SetBinding(BindableLayout.ItemsSourceProperty, nameof(ReportsAiViewModel.ChatHistory));
        Action<string> sendFollowUp = q => _vm?.SendSuggestedCommand.Execute(q);
        BindableLayout.SetItemTemplate(bubbleList, BuildBubbleTemplate(sendFollowUp));

        var chatScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            Content = new VerticalStackLayout
            {
                Children = { promptChips, bubbleList },
            },
        };

        // Input bar
        var entry = new Entry
        {
            Placeholder = "⊙  Ask about this race…",
            BackgroundColor = Colors.Transparent,
            FontFamily = "Inter",
            FontSize = 14,
            TextColor = TextPri,
            PlaceholderColor = TextMuted,
        };
        entry.SetBinding(Entry.TextProperty, nameof(ReportsAiViewModel.UserInput));
        entry.Completed += (_, _) => _vm?.SendCommand.Execute(null);

        var inputBar = new Border
        {
            Margin = new Thickness(14, 0, 14, 12),
            Padding = new Thickness(14, 10),
            BackgroundColor = BgCanvas,
            Stroke = Border2,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = entry,
        };

        var panelLayout = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto },
            ],
        };

        var headerBorder = new Border
        {
            Padding = new Thickness(14, 10),
            BackgroundColor = Color.FromArgb("#1A1714"),
            Stroke = Border2,
            StrokeThickness = 0,
            Content = headerGrid,
        };
        // Bottom border only (simulate separator)
        panelLayout.Add(headerBorder, 0, 0);
        panelLayout.Add(chatScroll, 0, 1);
        panelLayout.Add(inputBar, 0, 2);

        panel.Content = panelLayout;
        return panel;
    }

    private static DataTemplate BuildBubbleTemplate(Action<string> onFollowUp) => new(() =>
    {
        // ── User pill ──────────────────────────────────────────────────
        var userLabel = new Label
        {
            FontFamily = "Inter",
            FontSize = 14,
            TextColor = Color.FromArgb("#F4EEE6"),
            LineBreakMode = LineBreakMode.WordWrap,
        };
        userLabel.SetBinding(Label.TextProperty, nameof(ChatBubble.Content));

        var userBubble = new Border
        {
            HorizontalOptions = LayoutOptions.Start,
            MaximumWidthRequest = 460,
            BackgroundColor = Color.FromArgb("#2A1E08"),
            Stroke = Color.FromArgb("#FFA60D"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(14, 9),
            Content = userLabel,
        };
        userBubble.SetBinding(IsVisibleProperty, nameof(ChatBubble.IsUser));

        // ── Assistant card ─────────────────────────────────────────────
        // Tool chips strip
        var toolChips = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        toolChips.SetBinding(BindableLayout.ItemsSourceProperty, nameof(ChatBubble.ToolActivities));
        BindableLayout.SetItemTemplate(toolChips, BuildToolChipTemplate());
        toolChips.SetBinding(IsVisibleProperty, new Binding(nameof(ChatBubble.ToolActivities),
            converter: new ToolListVisibilityConverter()));

        var markdownLabel = new MarkdownLabel { LineBreakMode = LineBreakMode.WordWrap };
        markdownLabel.SetBinding(MarkdownLabel.MarkdownTextProperty, nameof(ChatBubble.Content));

        var cursor = new BlinkingCursor();
        cursor.SetBinding(IsVisibleProperty, nameof(ChatBubble.IsStreaming));

        // Follow-up question chips (populated after streaming finishes)
        var followUpChips = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        followUpChips.SetBinding(BindableLayout.ItemsSourceProperty, nameof(ChatBubble.FollowUps));
        BindableLayout.SetItemTemplate(followUpChips, new DataTemplate(() =>
        {
            var lbl = new Label { FontFamily = "Inter", FontSize = 12, TextColor = Color.FromArgb("#BCB1A2"), LineBreakMode = LineBreakMode.NoWrap };
            lbl.SetBinding(Label.TextProperty, ".");
            var chip = new Border
            {
                Padding = new Thickness(10, 5),
                BackgroundColor = Colors.Transparent,
                Stroke = Color.FromArgb("#3A3128"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Content = lbl,
            };
            var tap = new TapGestureRecognizer { Command = new Command<string>(onFollowUp) };
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, ".");
            chip.GestureRecognizers.Add(tap);
            return chip;
        }));
        followUpChips.SetBinding(IsVisibleProperty, new Binding(nameof(ChatBubble.FollowUps),
            converter: new ToolListVisibilityConverter()));

        var assistantCard = new Border
        {
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = Color.FromArgb("#100E0C"),
            Stroke = Color.FromArgb("#2E2820"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(16, 12),
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children = { markdownLabel, cursor, toolChips, followUpChips },
            },
        };
        assistantCard.SetBinding(IsVisibleProperty, new Binding(nameof(ChatBubble.IsUser),
            converter: (IValueConverter)Application.Current!.Resources["InvertedBoolConverter"]));

        var wrapper = new Grid();
        wrapper.Children.Add(userBubble);
        wrapper.Children.Add(assistantCard);
        return wrapper;
    });

    private static DataTemplate BuildToolChipTemplate() => new(() =>
    {
        var icon = new Label { FontFamily = "JetBrainsMono", FontSize = 10, VerticalOptions = LayoutOptions.Center };
        icon.SetBinding(Label.TextProperty, new Binding(nameof(ToolActivity.IsRunning),
            converter: new BoolToStringConverter("◌", "✓")));
        icon.SetBinding(Label.TextColorProperty, new Binding(nameof(ToolActivity.IsRunning),
            converter: new BoolToColorConverter2("#FFA60D", "#27D98C")));

        var name = new Label { FontFamily = "JetBrainsMono", FontSize = 10, TextColor = Color.FromArgb("#7A736B"), VerticalOptions = LayoutOptions.Center };
        name.SetBinding(Label.TextProperty, nameof(ToolActivity.Name));

        return new Border
        {
            Margin = new Thickness(0, 4, 6, 0),
            Padding = new Thickness(8, 3),
            BackgroundColor = Color.FromArgb("#1A1714"),
            Stroke = Color.FromArgb("#2E2820"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = new HorizontalStackLayout { Spacing = 5, Children = { icon, name } },
        };
    });

    private DataTemplate BuildFollowUpChipTemplate() => new(() =>
    {
        var label = new Label
        {
            FontFamily = "Inter",
            FontSize = 12,
            TextColor = Color.FromArgb("#BCB1A2"),
            LineBreakMode = LineBreakMode.NoWrap,
        };
        label.SetBinding(Label.TextProperty, ".");

        var chip = new Border
        {
            Padding = new Thickness(10, 5),
            BackgroundColor = Colors.Transparent,
            Stroke = Color.FromArgb("#3A3128"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = label,
        };

        chip.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command<string>(q => _vm?.SendSuggestedCommand.Execute(q)),
            CommandParameter = new Binding("."),
        });

        // Highlight on hover-ish: tint on press via opacity
        return chip;
    });

    // ── Shared document panel chrome ──────────────────────────────────────────

    private View BuildDocumentPanel(string tag, View content)
    {
        var header = PanelHeader(tag);
        Grid.SetRow(header, 0);

        var scrollContent = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            Padding = new Thickness(16, 14),
            Content = content,
        };
        Grid.SetRow(scrollContent, 1);

        var innerGrid = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star },
            ],
        };
        innerGrid.Children.Add(header);
        innerGrid.Children.Add(scrollContent);

        return new Border
        {
            BackgroundColor = BgRaised,
            Stroke = Border2,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = innerGrid,
        };
    }

    private View PanelHeader(string tag)
    {
        var sessionLabel = new Label
        {
            FontFamily = "JetBrainsMono",
            FontSize = 12,
            TextColor = TextSec,
            VerticalOptions = LayoutOptions.Center,
        };
        sessionLabel.SetBinding(Label.TextProperty, new Binding("Debrief.Session.SessionId",
            stringFormat: $"□  {tag} · {{0}}"));

        var border = new Border
        {
            Padding = new Thickness(14, 9),
            BackgroundColor = Color.FromArgb("#1A1714"),
            Stroke = Border2,
            StrokeThickness = 0,
            Content = sessionLabel,
        };
        Grid.SetRow(border, 0);
        return border;
    }

    private View BuildSourceSidebar()
    {
        var endpoints = new[]
        {
            "race / lap story",
            "session weather summary",
            "race-control index",
            "stint analysis",
        };

        var list = new VerticalStackLayout { Spacing = 10 };
        foreach (var ep in endpoints)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                ],
            };
            row.Add(new Label { Text = ep, FontFamily = "JetBrainsMono", FontSize = 11, TextColor = TextSec }, 0, 0);
            row.Add(new Label { Text = "ready", FontFamily = "JetBrainsMono", FontSize = 11, TextColor = Green }, 1, 0);
            list.Children.Add(row);
        }

        var sourcePanel = new Border
        {
            VerticalOptions = LayoutOptions.Start,
            Padding = new Thickness(14, 12),
            BackgroundColor = BgRaised,
            Stroke = Border2,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Source endpoints", FontFamily = "InterSemiBold", FontSize = 13, TextColor = TextPri },
                    list,
                },
            },
        };

        var note = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 10),
            BackgroundColor = AccentMuted,
            Stroke = Accent,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = new Label
            {
                Text = "Pure composition over bounded aggregates — no raw telemetry — so it stays inside the read-only MCP contract.",
                FontFamily = "Inter",
                FontSize = 12,
                TextColor = TextSec,
                LineBreakMode = LineBreakMode.WordWrap,
            },
        };

        return new VerticalStackLayout { Children = { sourcePanel, note } };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static View SectionBlock(string heading, View body)
    {
        return new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label
                {
                    Text = heading,
                    FontFamily = "JetBrainsMono",
                    FontSize = 11,
                    CharacterSpacing = 1.5,
                    TextColor = Color.FromArgb("#FFA60D"),
                },
                body,
            },
        };
    }

    private static View PositionRow(string label, string driver, string value, Color valueColor)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
            ],
        };
        row.Add(new Label { Text = label, FontFamily = "Inter", FontSize = 14, TextColor = Color.FromArgb("#BCB1A2") }, 0, 0);
        var right = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.End };
        right.Children.Add(new Label { Text = driver, FontFamily = "JetBrainsMonoMedium", FontSize = 14, TextColor = valueColor });
        right.Children.Add(new Label { Text = value, FontFamily = "JetBrainsMono", FontSize = 14, TextColor = valueColor });
        row.Add(right, 1, 0);
        return row;
    }

    private static View IncidentRow(string lap, Color dotColor, string description)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = new GridLength(40) },
                new ColumnDefinition { Width = new GridLength(14) },
                new ColumnDefinition { Width = GridLength.Star },
            ],
            ColumnSpacing = 8,
        };
        row.Add(new Label { Text = lap, FontFamily = "JetBrainsMono", FontSize = 13, TextColor = Color.FromArgb("#7A736B") }, 0, 0);
        row.Add(new Label { Text = "●", FontSize = 10, TextColor = dotColor, VerticalOptions = LayoutOptions.Center }, 1, 0);
        row.Add(new Label { Text = description, FontFamily = "Inter", FontSize = 14, TextColor = Color.FromArgb("#BCB1A2"), LineBreakMode = LineBreakMode.WordWrap }, 2, 0);
        return row;
    }

    private static View StatBox(string heading, string value, int column)
    {
        var box = new Border
        {
            Padding = new Thickness(12, 10),
            BackgroundColor = Color.FromArgb("#100E0C"),
            Stroke = Color.FromArgb("#2E2820"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = heading, FontFamily = "JetBrainsMono", FontSize = 10, CharacterSpacing = 1, TextColor = Color.FromArgb("#7A736B") },
                    new Label { Text = value, FontFamily = "InterSemiBold", FontSize = 22, TextColor = Color.FromArgb("#F4EEE6") },
                },
            },
        };
        Grid.SetColumn(box, column);
        return box;
    }

    private static string? FindInsightValue(IReadOnlyList<AnalysisInsight> insights, string kindFragment)
    {
        var insight = insights.FirstOrDefault(i =>
            i.Kind.Contains(kindFragment, StringComparison.OrdinalIgnoreCase));
        if (insight is null) return null;
        if (insight.Value.HasValue && !string.IsNullOrEmpty(insight.Unit))
            return $"{insight.Value:0.000} {insight.Unit}";
        return insight.Value.HasValue ? insight.Value.ToString() : insight.Text;
    }

    private static (Color bg, Color stroke, Color text) CompoundColor(string compound) =>
        compound.ToUpperInvariant() switch
        {
            "SOFT"   => (Color.FromArgb("#3B0A0A"), Color.FromArgb("#C83737"), Color.FromArgb("#F4EEE6")),
            "MEDIUM" => (Color.FromArgb("#43320F"), Color.FromArgb("#FFA60D"), Color.FromArgb("#141210")),
            "HARD"   => (Color.FromArgb("#2A2622"), Color.FromArgb("#BCB1A2"), Color.FromArgb("#F4EEE6")),
            "INTER"  => (Color.FromArgb("#0F2E1A"), Color.FromArgb("#27D98C"), Color.FromArgb("#F4EEE6")),
            "WET"    => (Color.FromArgb("#0D1E2E"), Color.FromArgb("#4DB8FF"), Color.FromArgb("#F4EEE6")),
            _        => (Color.FromArgb("#1E1916"), Color.FromArgb("#3A3128"), Color.FromArgb("#BCB1A2")),
        };

    private static string FormatMs(long ms)
    {
        var total = TimeSpan.FromMilliseconds(ms);
        return $"{(int)total.TotalMinutes}:{total.Seconds:00}";
    }

    private static int EstimateLap(long sessionTimeMs, int totalLaps)
    {
        if (totalLaps <= 0) return 0;
        // Rough estimate assuming ~5400 s (90 min) total
        var fraction = sessionTimeMs / 5_400_000.0;
        return Math.Clamp((int)(fraction * totalLaps) + 1, 1, totalLaps);
    }
}
