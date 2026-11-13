using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HUMofA.Agent;
using HUMofA.Gateway;
using HUMofA.Memory;
using HUMofA.Scheduler;
using HUMofA.Sessions;
using HUMofA.Skills;
using HUMofA.SubAgents;

namespace HUMofA.Desktop.Native;

/// <summary>
/// 100% Pure C# (.NET 10 WPF) Compact, Modern, Progressive-Disclosure Desktop Workspace for HUMofA (HOFA).
/// Keeps everyday workflows clean and uncluttered while exposing full developer power via collapsible menus and drawers.
/// </summary>
public sealed class NativeMainWindow : Window
{
    private readonly AppCore _core;
    private readonly string _settingsFilePath;
    private DesktopSettings _settings;
    private NativeTheme _theme;

    private string _activeView = "chat";
    private bool _advancedMenuExpanded = false;
    private bool _devDrawerOpen = false;
    private string? _activeSessionId;
    private int _totalPromptTokens;
    private int _totalCompletionTokens;

    // Top Header Controls
    private Border _providerBadgeBorder = null!;
    private TextBlock _providerBadgeText = null!;
    private ComboBox _sessionComboBox = null!;
    private Button _devDrawerToggleBtn = null!;

    // Left Sidebar Controls
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel _advancedNavPanel = null!;
    private Button _advancedToggleBtn = null!;

    // Center Workspace Views
    private Grid _centerContentHost = null!;
    private readonly Dictionary<string, UIElement> _views = new(StringComparer.OrdinalIgnoreCase);

    // Chat View Controls
    private StackPanel _chatMessagesStack = null!;
    private ScrollViewer _chatScrollViewer = null!;
    private TextBox _chatInputBox = null!;
    private ComboBox _composerModelCombo = null!;
    private ComboBox _composerAutonomyCombo = null!;
    private TextBlock _chatStatusLabel = null!;

    // Sessions View Controls
    private StackPanel _sessionsListStack = null!;
    private TextBox _newSessionTitleInput = null!;

    // Files & Editor View Controls
    private ListBox _filesListBox = null!;
    private TextBox _editorPathInput = null!;
    private TextBox _codeEditorBox = null!;
    private TextBlock _editorStatusText = null!;

    // Skills, Kanban, Memory, Cron Controls
    private StackPanel _skillsListStack = null!;
    private TextBox _skillInstallInput = null!;
    private StackPanel _kanbanPendingStack = null!;
    private StackPanel _kanbanProgressStack = null!;
    private StackPanel _kanbanDoneStack = null!;
    private TextBox _kanbanNewTitleInput = null!;
    private StackPanel _memoryListStack = null!;
    private TextBox _memorySearchInput = null!;
    private TextBox _memoryNewTitleInput = null!;
    private TextBox _memoryNewContentInput = null!;
    private StackPanel _cronListStack = null!;
    private TextBox _cronNameInput = null!;
    private TextBox _cronPhraseInput = null!;
    private TextBox _cronPromptInput = null!;

    // Collapsible Developer Drawer Controls
    private Border _devDrawerContainer = null!;
    private GridSplitter _devDrawerSplitter = null!;
    private RowDefinition _devDrawerRow = null!;
    private TextBox _terminalCmdInput = null!;
    private TextBox _terminalOutputBox = null!;
    private TextBox _diffFilePathInput = null!;
    private TextBox _diffProposedInput = null!;
    private TextBox _diffOutputBox = null!;
    private TextBlock _telemetrySummaryText = null!;
    private ListBox _runtimeLogsList = null!;

    // Ctrl+K Command Palette Overlay
    private Border _paletteOverlay = null!;
    private TextBox _paletteSearchBox = null!;
    private ListBox _paletteResultsList = null!;
    private readonly List<(string Title, string Category, Action Execute)> _paletteActions = new();

    public string ActiveView => _activeView;
    public bool IsDevDrawerOpen => _devDrawerOpen;
    public bool IsAdvancedMenuExpanded => _advancedMenuExpanded;
    public DesktopSettings CurrentSettings => _settings;

    public NativeMainWindow(AppCore core, string? settingsFilePath = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _settingsFilePath = settingsFilePath ?? Path.Combine(_core.Desktop.WorkspaceDir, "settings.json");
        _settings = LoadSettings();
        _core.Router.ConfigureFromSettings(_settings);
        _theme = new NativeTheme(_settings.Appearance.Theme);

        Title = "HOFA — Otonom AI Ajan & Geliştirici Platformu (.NET 10 Native C#)";
        Width = 1280;
        Height = 820;
        MinWidth = 920;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        RegisterCommandPaletteActions();
        BuildWindowLayout();
        PreviewKeyDown += HandleGlobalShortcuts;

        Loaded += async (_, _) =>
        {
            await InitializeWorkspaceDataAsync();
            if (!_settings.OnboardingCompleted)
            {
                OpenSetupWizardDialog();
            }
        };
    }

    private void RegisterCommandPaletteActions()
    {
        _paletteActions.Clear();
        _paletteActions.Add(("⚡ SSB Evren / Evrensel API Kurulum Sihirbazını Aç", "Kurulum & API", OpenSetupWizardDialog));
        _paletteActions.Add(("💬 Sohbet & Görev Akışına Git", "Navigasyon", () => SwitchView("chat")));
        _paletteActions.Add(("🛠 Geliştirici Çekmecesini Aç / Kapat (Terminal, Diff, Telemetri)", "Geliştirici", ToggleDevDrawer));
        _paletteActions.Add(("📁 Dosya Gezgini ve Kod Editörünü Aç", "Navigasyon", () => SwitchView("files")));
        _paletteActions.Add(("🗂 Oturum Yönetimi", "Navigasyon", () => SwitchView("sessions")));
        _paletteActions.Add(("🧩 Beceriler (Skills Hub)", "Gelişmiş", () => { ExpandAdvancedMenu(); SwitchView("skills"); }));
        _paletteActions.Add(("🤖 Alt-Ajanlar Kanban Panosu", "Gelişmiş", () => { ExpandAdvancedMenu(); SwitchView("kanban"); }));
        _paletteActions.Add(("🧠 Kalıcı Bellek (MEMORY.md & FTS5 Arama)", "Gelişmiş", () => { ExpandAdvancedMenu(); SwitchView("memory"); }));
        _paletteActions.Add(("⏰ Zamanlanmış Görevler (Cron)", "Gelişmiş", () => { ExpandAdvancedMenu(); SwitchView("cron"); }));
        _paletteActions.Add(("⚙ Hızlı Ayarlar & Sağlayıcı Özeti", "Ayarlar", () => SwitchView("settings")));
        _paletteActions.Add(("☀/🌙 Koyu / Açık Temayı Değiştir", "Görünüm", ToggleTheme));
        _paletteActions.Add(("🛑 Acil Durdurma (PANIC STOP)", "Güvenlik", TriggerPanicStop));
    }

    private void BuildWindowLayout()
    {
        Background = _theme.BgPrimary;
        Foreground = _theme.TextPrimary;
        FontFamily = _theme.FontUi;

        var rootOverlayGrid = new Grid();

        var mainLayoutGrid = new Grid();
        mainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); // Top Compact Header
        mainLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Workspace

        // 1. Top Header Bar
        var headerBar = BuildHeaderBar();
        Grid.SetRow(headerBar, 0);
        mainLayoutGrid.Children.Add(headerBar);

        // 2. Body Split: Left Compact Sidebar + Right Main Area (with collapsible bottom Dev Drawer)
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildCompactSidebar();
        Grid.SetColumn(sidebar, 0);
        bodyGrid.Children.Add(sidebar);

        var rightWorkspaceGrid = new Grid();
        rightWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _devDrawerRow = new RowDefinition { Height = new GridLength(0) };
        rightWorkspaceGrid.RowDefinitions.Add(_devDrawerRow);

        _centerContentHost = new Grid { Background = _theme.BgPrimary };
        _views["chat"] = BuildChatView();
        _views["sessions"] = BuildSessionsView();
        _views["files"] = BuildFilesAndEditorView();
        _views["skills"] = BuildSkillsView();
        _views["kanban"] = BuildKanbanView();
        _views["memory"] = BuildMemoryView();
        _views["cron"] = BuildCronView();
        _views["settings"] = BuildSettingsSummaryView();

        foreach (var (key, view) in _views)
        {
            view.Visibility = key == _activeView ? Visibility.Visible : Visibility.Collapsed;
            _centerContentHost.Children.Add(view);
        }

        Grid.SetRow(_centerContentHost, 0);
        rightWorkspaceGrid.Children.Add(_centerContentHost);

        _devDrawerSplitter = new GridSplitter
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = _theme.BorderStrong,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_devDrawerSplitter, 1);
        rightWorkspaceGrid.Children.Add(_devDrawerSplitter);

        _devDrawerContainer = BuildCollapsibleDevDrawer();
        Grid.SetRow(_devDrawerContainer, 2);
        rightWorkspaceGrid.Children.Add(_devDrawerContainer);

        Grid.SetColumn(rightWorkspaceGrid, 1);
        bodyGrid.Children.Add(rightWorkspaceGrid);

        Grid.SetRow(bodyGrid, 1);
        mainLayoutGrid.Children.Add(bodyGrid);

        rootOverlayGrid.Children.Add(mainLayoutGrid);

        // 3. Ctrl+K Command Palette Overlay
        _paletteOverlay = BuildCommandPaletteOverlay();
        rootOverlayGrid.Children.Add(_paletteOverlay);

        Content = rootOverlayGrid;
        UpdateActiveProviderBadge();
    }

    private Border BuildHeaderBar()
    {
        var headerGrid = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: Brand + Active Provider Pill + Session Dropdown
        var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = _theme.Success,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "C# .NET 10 Çekirdek Motoru Aktif"
        };
        var brandTitle = new TextBlock
        {
            Text = "HOFA",
            FontSize = 15,
            FontWeight = FontWeights.ExtraBold,
            Foreground = _theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        _providerBadgeText = new TextBlock
        {
            Text = "⚡ SSB EVREN",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _theme.AccentGlow
        };
        _providerBadgeBorder = new Border
        {
            Background = _theme.AccentSoftBg,
            BorderBrush = _theme.BorderAccent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Aktif AI Sağlayıcı ve Model (Değiştirmek için tıklayın)",
            Child = _providerBadgeText
        };
        _providerBadgeBorder.MouseLeftButtonDown += (_, _) => OpenSetupWizardDialog();

        _sessionComboBox = new ComboBox
        {
            Width = 170,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Aktif Konuşma Oturumu"
        };
        _sessionComboBox.SelectionChanged += async (_, _) =>
        {
            if (_sessionComboBox.SelectedItem is ComboBoxItem item && item.Tag is string sid && sid != _activeSessionId)
            {
                await ActivateSessionByIdAsync(sid);
            }
        };

        leftStack.Children.Add(dot);
        leftStack.Children.Add(brandTitle);
        leftStack.Children.Add(_providerBadgeBorder);
        leftStack.Children.Add(_sessionComboBox);
        Grid.SetColumn(leftStack, 0);
        headerGrid.Children.Add(leftStack);

        // Center: Raycast-Style Command Bar Trigger
        var cmdTriggerBtn = _theme.CreateButton("🔍  Komut, dosya, oturum veya API ara...   (Ctrl+K)", isPrimary: false, padding: new Thickness(18, 5, 18, 5), fontSize: 12);
        cmdTriggerBtn.HorizontalAlignment = HorizontalAlignment.Center;
        cmdTriggerBtn.VerticalAlignment = VerticalAlignment.Center;
        cmdTriggerBtn.MinWidth = 310;
        cmdTriggerBtn.Click += (_, _) => ToggleCommandPalette();
        Grid.SetColumn(cmdTriggerBtn, 1);
        headerGrid.Children.Add(cmdTriggerBtn);

        // Right: API Setup + Dev Drawer Toggle + Theme Toggle + Panic Stop
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var setupBtn = _theme.CreateButton("⚡ API / SSB Evren", isPrimary: true, padding: new Thickness(11, 5, 11, 5), fontSize: 12);
        setupBtn.Margin = new Thickness(0, 0, 6, 0);
        setupBtn.Click += (_, _) => OpenSetupWizardDialog();

        _devDrawerToggleBtn = _theme.CreateButton("🛠 Geliştirici Paneli", isPrimary: false, padding: new Thickness(11, 5, 11, 5), fontSize: 12);
        _devDrawerToggleBtn.Margin = new Thickness(0, 0, 6, 0);
        _devDrawerToggleBtn.ToolTip = "Terminal, Diff İnceleyici ve Canlı Telemetri Çekmecesini Aç/Kapat (Ctrl+J)";
        _devDrawerToggleBtn.Click += (_, _) => ToggleDevDrawer();

        var themeBtn = _theme.CreateButton(_theme.IsDark ? "☀" : "🌙", isPrimary: false, padding: new Thickness(9, 5, 9, 5), fontSize: 12);
        themeBtn.Margin = new Thickness(0, 0, 6, 0);
        themeBtn.ToolTip = "Koyu / Açık Tema Değiştir";
        themeBtn.Click += (_, _) => ToggleTheme();

        var panicBtn = _theme.CreateButton("🛑 PANIC", isDanger: true, padding: new Thickness(10, 5, 10, 5), fontSize: 11.5);
        panicBtn.ToolTip = "Acil Durdurma Anahtarı (F12)";
        panicBtn.Click += (_, _) => TriggerPanicStop();

        rightStack.Children.Add(setupBtn);
        rightStack.Children.Add(_devDrawerToggleBtn);
        rightStack.Children.Add(themeBtn);
        rightStack.Children.Add(panicBtn);
        Grid.SetColumn(rightStack, 2);
        headerGrid.Children.Add(rightStack);

        return new Border
        {
            Background = _theme.BgSecondary,
            BorderBrush = _theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid
        };
    }

    private Border BuildCompactSidebar()
    {
        var sidebarDock = new DockPanel { Margin = new Thickness(10, 12, 10, 12) };

        // Bottom CLI Info Card
        var cliHintCard = new Border
        {
            Background = _theme.BgSurface,
            BorderBrush = _theme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Terminal Kısayolları",
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = _theme.AccentGlow
                    },
                    new TextBlock
                    {
                        Text = "• hofa         → CLI Başlat\n• hofa desktop → Masaüstü\n• hofa setup   → API Kur",
                        FontFamily = _theme.FontMono,
                        FontSize = 10.5,
                        Foreground = _theme.TextSecondary,
                        Margin = new Thickness(0, 4, 0, 0)
                    }
                }
            }
        };
        DockPanel.SetDock(cliHintCard, Dock.Bottom);
        sidebarDock.Children.Add(cliHintCard);

        var navStack = new StackPanel();

        var newChatBtn = _theme.CreateButton("+ Yeni Görev / Sohbet", isPrimary: true, padding: new Thickness(12, 8, 12, 8), fontSize: 12.5);
        newChatBtn.Margin = new Thickness(0, 0, 0, 12);
        newChatBtn.Click += async (_, _) =>
        {
            await CreateAndActivateSessionAsync($"Oturum {DateTime.Now:HH:mm}");
            SwitchView("chat");
            _chatInputBox.Focus();
        };
        navStack.Children.Add(newChatBtn);

        navStack.Children.Add(CreateSidebarGroupLabel("TEMEL ÇALIŞMA ALANI"));
        AddNavButton(navStack, "chat", "💬  Sohbet & Görevler");
        AddNavButton(navStack, "sessions", "🗂  Oturum Geçmişi");
        AddNavButton(navStack, "files", "📁  Dosyalar & Editör");
        AddNavButton(navStack, "settings", "⚙  API & Ayarlar");

        // Progressive Disclosure: Collapsible Advanced Tools Menu
        _advancedToggleBtn = _theme.CreateButton("▸  Gelişmiş Araçlar (4)", isPrimary: false, padding: new Thickness(10, 7, 10, 7), fontSize: 12);
        _advancedToggleBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        _advancedToggleBtn.Margin = new Thickness(0, 12, 0, 4);
        _advancedToggleBtn.Click += (_, _) =>
        {
            _advancedMenuExpanded = !_advancedMenuExpanded;
            _advancedNavPanel.Visibility = _advancedMenuExpanded ? Visibility.Visible : Visibility.Collapsed;
            _advancedToggleBtn.Content = _advancedMenuExpanded ? "▾  Gelişmiş Araçlar (4)" : "▸  Gelişmiş Araçlar (4)";
        };
        navStack.Children.Add(_advancedToggleBtn);

        _advancedNavPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(6, 2, 0, 0)
        };
        AddNavButton(_advancedNavPanel, "skills", "🧩  Beceriler (Skills)");
        AddNavButton(_advancedNavPanel, "kanban", "🤖  Alt-Ajan Kanban");
        AddNavButton(_advancedNavPanel, "memory", "🧠  Kalıcı Bellek (FTS5)");
        AddNavButton(_advancedNavPanel, "cron", "⏰  Zamanlayıcı (Cron)");
        navStack.Children.Add(_advancedNavPanel);

        sidebarDock.Children.Add(navStack);

        return new Border
        {
            Background = _theme.BgSecondary,
            BorderBrush = _theme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebarDock
        };
    }

    private void AddNavButton(Panel parent, string viewKey, string label)
    {
        var btn = _theme.CreateButton(label, isPrimary: _activeView == viewKey, padding: new Thickness(12, 7, 12, 7), fontSize: 12.5);
        btn.Margin = new Thickness(0, 0, 0, 4);
        btn.Click += (_, _) => SwitchView(viewKey);
        _navButtons[viewKey] = btn;
        parent.Children.Add(btn);
    }

    public void ExpandAdvancedMenu()
    {
        _advancedMenuExpanded = true;
        if (_advancedNavPanel != null)
            _advancedNavPanel.Visibility = Visibility.Visible;
        if (_advancedToggleBtn != null)
            _advancedToggleBtn.Content = "▾  Gelişmiş Araçlar (4)";
    }

    public void SwitchView(string viewKey)
    {
        if (!_views.ContainsKey(viewKey)) return;
        _activeView = viewKey;

        foreach (var (k, v) in _views)
        {
            v.Visibility = k.Equals(viewKey, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var (k, btn) in _navButtons)
        {
            bool active = k.Equals(viewKey, StringComparison.OrdinalIgnoreCase);
            btn.Background = active ? _theme.AccentPrimary : _theme.BgElevated;
            btn.BorderBrush = active ? _theme.AccentGlow : _theme.BorderStrong;
        }
    }

    // =========================================================================
    // VIEW 1: Compact Chat & Autonomous Task Stream
    // =========================================================================
    private UIElement BuildChatView()
    {
        var chatGrid = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _chatMessagesStack = new StackPanel();
        _chatMessagesStack.Children.Add(BuildWelcomeHeroCard());

        _chatScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 8),
            Content = _chatMessagesStack
        };
        Grid.SetRow(_chatScrollViewer, 0);
        chatGrid.Children.Add(_chatScrollViewer);

        // Sleek Composer Bar
        var composerStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        _chatInputBox = _theme.CreateTextBox("", "Bir görev yazın, kod üretin veya soru sorun... (Göndermek için Enter, yeni satır için Shift+Enter)", isMultiline: true);
        _chatInputBox.Height = 68;
        _chatInputBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                await SendChatOrTaskPromptAsync();
            }
        };
        composerStack.Children.Add(_chatInputBox);

        var controlsDock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };

        var leftControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftControls.Children.Add(new TextBlock
        {
            Text = "Model:",
            FontSize = 11.5,
            Foreground = _theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });

        _composerModelCombo = new ComboBox
        {
            IsEditable = true,
            Width = 190,
            FontFamily = _theme.FontMono,
            FontSize = 11.5,
            Padding = new Thickness(6, 4, 6, 4),
            Text = _settings.Models.DefaultModel
        };
        foreach (var m in _settings.Models.AvailableModels)
            _composerModelCombo.Items.Add(m);
        leftControls.Children.Add(_composerModelCombo);

        leftControls.Children.Add(new TextBlock
        {
            Text = "Otonomi:",
            FontSize = 11.5,
            Foreground = _theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0)
        });

        _composerAutonomyCombo = new ComboBox
        {
            Width = 120,
            FontSize = 11.5,
            Padding = new Thickness(6, 4, 6, 4)
        };
        _composerAutonomyCombo.Items.Add("Guarded");
        _composerAutonomyCombo.Items.Add("Conservative");
        _composerAutonomyCombo.Items.Add("FullAutonomy");
        _composerAutonomyCombo.SelectedItem = _settings.Permissions.DefaultAutonomyTier;
        leftControls.Children.Add(_composerAutonomyCombo);

        _chatStatusLabel = new TextBlock
        {
            Text = "Hazır",
            FontSize = 11.5,
            Foreground = _theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        leftControls.Children.Add(_chatStatusLabel);

        var sendBtn = _theme.CreateButton("Gönder ↵", isPrimary: true, padding: new Thickness(18, 7, 18, 7), fontSize: 13);
        sendBtn.Click += async (_, _) => await SendChatOrTaskPromptAsync();
        DockPanel.SetDock(sendBtn, Dock.Right);

        controlsDock.Children.Add(sendBtn);
        controlsDock.Children.Add(leftControls);
        composerStack.Children.Add(controlsDock);

        var composerCard = _theme.CreateCard(composerStack, new Thickness(12), new Thickness(0));
        Grid.SetRow(composerCard, 1);
        chatGrid.Children.Add(composerCard);

        return chatGrid;
    }

    private UIElement BuildWelcomeHeroCard()
    {
        var heroStack = new StackPanel();
        heroStack.Children.Add(new TextBlock
        {
            Text = "HOFA — Kompakt AI Ajan & Geliştirici Çalışma Alanı",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = _theme.TextPrimary
        });
        heroStack.Children.Add(new TextBlock
        {
            Text = "SSB Evren kurumsal portalı, bulut LLM'ler veya yerel Ollama ile doğal dilde görev çalıştırın. Gelişmiş araçlar için üstteki '🛠 Geliştirici Paneli' butonunu veya sol menüdeki 'Gelişmiş Araçlar' bölümünü açabilirsiniz.",
            FontSize = 12.5,
            Foreground = _theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var chipsWrap = new WrapPanel();
        AddQuickChip(chipsWrap, "⚡ SSB Evren API Yapılandır", OpenSetupWizardDialog);
        AddQuickChip(chipsWrap, "🔍 Proje Mimarisini Analiz Et", () =>
        {
            _chatInputBox.Text = "Bu projenin mimarisini ve ana bileşenlerini özetle.";
            _ = SendChatOrTaskPromptAsync();
        });
        AddQuickChip(chipsWrap, "💻 Geliştirici Terminalini Aç", () =>
        {
            if (!_devDrawerOpen) ToggleDevDrawer();
        });
        AddQuickChip(chipsWrap, "📁 Çalışma Alanı Dosyalarını İncele", () => SwitchView("files"));

        heroStack.Children.Add(chipsWrap);
        return _theme.CreateCard(heroStack, new Thickness(16, 14, 16, 14), highlight: true);
    }

    private void AddQuickChip(Panel parent, string text, Action onClick)
    {
        var btn = _theme.CreateButton(text, isPrimary: false, padding: new Thickness(10, 5, 10, 5), fontSize: 11.5);
        btn.Margin = new Thickness(0, 0, 8, 6);
        btn.Click += (_, _) => onClick();
        parent.Children.Add(btn);
    }

    private async Task SendChatOrTaskPromptAsync()
    {
        var prompt = _chatInputBox.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        _chatInputBox.Text = string.Empty;
        var chosenModel = string.IsNullOrWhiteSpace(_composerModelCombo.Text)
            ? _settings.Models.DefaultModel
            : _composerModelCombo.Text.Trim();

        AppendChatBubble("Kullanıcı", prompt, isUser: true);
        _chatStatusLabel.Text = $"⚡ {chosenModel} yanıt üretiyor...";
        _chatStatusLabel.Foreground = _theme.AccentGlow;

        var sw = Stopwatch.StartNew();
        try
        {
            if (_activeSessionId != null)
            {
                await _core.SessionManager.AppendMessageAsync(_activeSessionId, "user", prompt, prompt.Length / 4);
            }

            var (reply, decision) = await _core.Router.RouteAndCompleteAsync(prompt, chosenModel);
            sw.Stop();

            int promptTok = Math.Max(1, prompt.Length / 4);
            int compTok = Math.Max(1, reply.Length / 4);
            _totalPromptTokens += promptTok;
            _totalCompletionTokens += compTok;

            if (_activeSessionId != null)
            {
                await _core.SessionManager.AppendMessageAsync(_activeSessionId, "assistant", reply, compTok);
            }

            AppendCompactToolPill($"Model Yönlendirici: {decision.SelectedProviderId.ToUpperInvariant()} ({decision.SelectedModel})", $"{sw.ElapsedMilliseconds} ms · {(decision.UsedOfflineFallback ? "Çevrimdışı Yedek" : "Canlı API")}");
            AppendChatBubble($"HOFA ({decision.SelectedProviderId}:{decision.SelectedModel})", reply, isUser: false);

            _chatStatusLabel.Text = $"✓ Tamamlandı ({sw.ElapsedMilliseconds} ms)";
            _chatStatusLabel.Foreground = _theme.Success;
            UpdateTelemetrySummary();
            AppendRuntimeLog($"[LLM] {decision.SelectedProviderId}/{decision.SelectedModel} yanıtladı ({sw.ElapsedMilliseconds} ms).");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendChatBubble("Sistem Hatası", ex.Message, isUser: false, isError: true);
            _chatStatusLabel.Text = "✗ Hata oluştu";
            _chatStatusLabel.Foreground = _theme.Danger;
            AppendRuntimeLog($"[HATA] {ex.Message}");
        }
    }

    private void AppendChatBubble(string sender, string content, bool isUser, bool isError = false)
    {
        var stack = new StackPanel();
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var roleLabel = new TextBlock
        {
            Text = sender,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = isError ? _theme.Danger : (isUser ? _theme.AccentGlow : _theme.Success)
        };
        var copyBtn = _theme.CreateButton("📋 Kopyala", isPrimary: false, padding: new Thickness(7, 2, 7, 2), fontSize: 10.5);
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(content); } catch { }
        };
        DockPanel.SetDock(copyBtn, Dock.Right);
        headerRow.Children.Add(copyBtn);
        headerRow.Children.Add(roleLabel);

        var bodyText = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = _theme.TextPrimary,
            FontFamily = content.Contains("```") || content.Contains("public ") ? _theme.FontMono : _theme.FontUi,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(headerRow);
        stack.Children.Add(bodyText);

        var card = new Border
        {
            Background = isUser ? _theme.AccentSoftBg : _theme.BgSurface,
            BorderBrush = isError ? _theme.Danger : (isUser ? _theme.BorderAccent : _theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(isUser ? 40 : 0, 0, isUser ? 0 : 40, 8),
            Child = stack
        };

        _chatMessagesStack.Children.Add(card);
        _chatScrollViewer.ScrollToEnd();
    }

    private void AppendCompactToolPill(string title, string detail)
    {
        var detailBlock = new TextBlock
        {
            Text = detail,
            FontFamily = _theme.FontMono,
            FontSize = 11,
            Foreground = _theme.TextSecondary,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var headerText = new TextBlock
        {
            Text = $"▸ 🛠 {title}   ·   {detail}",
            FontSize = 11,
            Foreground = _theme.TextSecondary
        };

        var stack = new StackPanel();
        stack.Children.Add(headerText);
        stack.Children.Add(detailBlock);

        var pillBorder = new Border
        {
            Background = _theme.BgSecondary,
            BorderBrush = _theme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 40, 6),
            Cursor = Cursors.Hand,
            Child = stack
        };
        pillBorder.MouseLeftButtonDown += (_, _) =>
        {
            detailBlock.Visibility = detailBlock.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        };

        _chatMessagesStack.Children.Add(pillBorder);
    }

    // =========================================================================
    // VIEW 2: Sessions View
    // =========================================================================
    private UIElement BuildSessionsView()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = "🗂 Oturum Geçmişi & Yönetimi",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var createRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var createBtn = _theme.CreateButton("+ Oturum Oluştur", isPrimary: true);
        createBtn.Margin = new Thickness(8, 0, 0, 0);
        createBtn.Click += async (_, _) =>
        {
            var title = string.IsNullOrWhiteSpace(_newSessionTitleInput.Text) ? $"Oturum {DateTime.Now:HH:mm}" : _newSessionTitleInput.Text.Trim();
            _newSessionTitleInput.Text = string.Empty;
            await CreateAndActivateSessionAsync(title);
        };
        DockPanel.SetDock(createBtn, Dock.Right);
        _newSessionTitleInput = _theme.CreateTextBox("", "Yeni oturum başlığı...");
        createRow.Children.Add(createBtn);
        createRow.Children.Add(_newSessionTitleInput);
        stack.Children.Add(createRow);

        _sessionsListStack = new StackPanel();
        stack.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 520,
            Content = _sessionsListStack
        });

        return stack;
    }

    // =========================================================================
    // VIEW 3: Files & Code Editor View
    // =========================================================================
    private UIElement BuildFilesAndEditorView()
    {
        var grid = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftStack = new DockPanel();
        var refreshBtn = _theme.CreateButton("🔄 Dosyaları Yenile", isPrimary: false);
        refreshBtn.Margin = new Thickness(0, 0, 0, 8);
        refreshBtn.Click += (_, _) => RefreshFilesList();
        DockPanel.SetDock(refreshBtn, Dock.Top);
        leftStack.Children.Add(refreshBtn);

        _filesListBox = new ListBox
        {
            Background = _theme.BgSurface,
            Foreground = _theme.TextPrimary,
            BorderBrush = _theme.BorderSubtle,
            FontFamily = _theme.FontMono,
            FontSize = 12
        };
        _filesListBox.SelectionChanged += (_, _) =>
        {
            if (_filesListBox.SelectedItem is string relPath)
            {
                LoadFileIntoEditor(relPath);
            }
        };
        leftStack.Children.Add(_filesListBox);
        Grid.SetColumn(leftStack, 0);
        grid.Children.Add(leftStack);

        var rightDock = new DockPanel();
        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var saveBtn = _theme.CreateButton("💾 Kaydet", isPrimary: true);
        saveBtn.Margin = new Thickness(8, 0, 0, 0);
        saveBtn.Click += (_, _) => SaveEditorFile();
        DockPanel.SetDock(saveBtn, Dock.Right);

        _editorPathInput = _theme.CreateTextBox("", "Dosya yolu...", isMonospace: true);
        topBar.Children.Add(saveBtn);
        topBar.Children.Add(_editorPathInput);
        DockPanel.SetDock(topBar, Dock.Top);
        rightDock.Children.Add(topBar);

        _editorStatusText = new TextBlock
        {
            Text = "Soldaki listeden bir dosya seçin veya yeni dosya adı yazıp kaydedin.",
            FontSize = 11.5,
            Foreground = _theme.TextSecondary,
            Margin = new Thickness(0, 6, 0, 0)
        };
        DockPanel.SetDock(_editorStatusText, Dock.Bottom);
        rightDock.Children.Add(_editorStatusText);

        _codeEditorBox = _theme.CreateTextBox("", "", isMonospace: true, isMultiline: true);
        rightDock.Children.Add(_codeEditorBox);

        Grid.SetColumn(rightDock, 2);
        grid.Children.Add(rightDock);

        return grid;
    }

    // =========================================================================
    // VIEW 4: Advanced Views (Skills, Kanban, Memory, Cron, Settings)
    // =========================================================================
    private UIElement BuildSkillsView()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = "🧩 Beceriler (Skills Hub & Güvenlik Taraması)",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var installRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var installBtn = _theme.CreateButton("Yükle & Tara", isPrimary: true);
        installBtn.Margin = new Thickness(8, 0, 0, 0);
        installBtn.Click += async (_, _) =>
        {
            var name = _skillInstallInput.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            await _core.SkillManager.AutoGenerateSkillFromTaskAsync(name, $"Kullanıcı tanımlı beceri ({name})", new[] { "Beceri adımlarını uygula" });
            _skillInstallInput.Text = string.Empty;
            RefreshSkillsView();
        };
        DockPanel.SetDock(installBtn, Dock.Right);
        _skillInstallInput = _theme.CreateTextBox("", "Yeni beceri adı...");
        installRow.Children.Add(installBtn);
        installRow.Children.Add(_skillInstallInput);
        stack.Children.Add(installRow);

        _skillsListStack = new StackPanel();
        stack.Children.Add(_skillsListStack);
        return stack;
    }

    private UIElement BuildKanbanView()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = "🤖 Alt-Ajanlar & Görev Kanban Panosu",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var addRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var addBtn = _theme.CreateButton("+ Görev Kartı Ekle", isPrimary: true);
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        addBtn.Click += (_, _) =>
        {
            var title = _kanbanNewTitleInput.Text.Trim();
            if (string.IsNullOrEmpty(title)) return;
            _core.SubAgents.Kanban.CreateCard(title, "Native UI üzerinden eklendi", "subagent-1");
            _kanbanNewTitleInput.Text = string.Empty;
            RefreshKanbanView();
        };
        DockPanel.SetDock(addBtn, Dock.Right);
        _kanbanNewTitleInput = _theme.CreateTextBox("", "Alt-ajan görev başlığı...");
        addRow.Children.Add(addBtn);
        addRow.Children.Add(_kanbanNewTitleInput);
        stack.Children.Add(addRow);

        var colsGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        _kanbanPendingStack = new StackPanel { Margin = new Thickness(4) };
        _kanbanProgressStack = new StackPanel { Margin = new Thickness(4) };
        _kanbanDoneStack = new StackPanel { Margin = new Thickness(4) };
        colsGrid.Children.Add(_theme.CreateCard(_kanbanPendingStack));
        colsGrid.Children.Add(_theme.CreateCard(_kanbanProgressStack));
        colsGrid.Children.Add(_theme.CreateCard(_kanbanDoneStack));
        stack.Children.Add(colsGrid);

        return stack;
    }

    private UIElement BuildMemoryView()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = "🧠 Kalıcı Bellek (SQLite FTS5 BM25 & MEMORY.md)",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        _memorySearchInput = _theme.CreateTextBox("", "Bellekte FTS5 BM25 araması yapın...");
        _memorySearchInput.Margin = new Thickness(0, 0, 0, 10);
        _memorySearchInput.TextChanged += async (_, _) => await RefreshMemoryViewAsync(_memorySearchInput.Text.Trim());
        stack.Children.Add(_memorySearchInput);

        var addCardStack = new StackPanel();
        _memoryNewTitleInput = _theme.CreateTextBox("", "Bellek kayıt başlığı...");
        _memoryNewTitleInput.Margin = new Thickness(0, 0, 0, 6);
        _memoryNewContentInput = _theme.CreateTextBox("", "Kalıcı bilgi içeriği...", isMultiline: true);
        _memoryNewContentInput.Height = 54;
        var saveMemBtn = _theme.CreateButton("+ Belleğe Kaydet", isPrimary: true);
        saveMemBtn.Margin = new Thickness(0, 6, 0, 0);
        saveMemBtn.HorizontalAlignment = HorizontalAlignment.Right;
        saveMemBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_memoryNewTitleInput.Text) || string.IsNullOrWhiteSpace(_memoryNewContentInput.Text))
                return;
            await _core.MemoryStore.AppendMemoryEntryAsync(MemoryDocumentType.Memory, _memoryNewTitleInput.Text.Trim(), _memoryNewContentInput.Text.Trim());
            _memoryNewTitleInput.Text = string.Empty;
            _memoryNewContentInput.Text = string.Empty;
            await RefreshMemoryViewAsync();
        };
        addCardStack.Children.Add(_memoryNewTitleInput);
        addCardStack.Children.Add(_memoryNewContentInput);
        addCardStack.Children.Add(saveMemBtn);
        stack.Children.Add(_theme.CreateCard(addCardStack));

        _memoryListStack = new StackPanel();
        stack.Children.Add(_memoryListStack);
        return stack;
    }

    private UIElement BuildCronView()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = "⏰ Zamanlanmış Otonom Görevler (Natural Cron)",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var formStack = new StackPanel();
        _cronNameInput = _theme.CreateTextBox("Günlük Rapor", "");
        _cronNameInput.Margin = new Thickness(0, 0, 0, 6);
        _cronPhraseInput = _theme.CreateTextBox("every 30 minutes", "");
        _cronPhraseInput.Margin = new Thickness(0, 0, 0, 6);
        _cronPromptInput = _theme.CreateTextBox("Sistem durumunu kontrol et ve özetle", "");
        var addCronBtn = _theme.CreateButton("+ Zamanlayıcı Ekle", isPrimary: true);
        addCronBtn.Margin = new Thickness(0, 6, 0, 0);
        addCronBtn.HorizontalAlignment = HorizontalAlignment.Right;
        addCronBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_cronNameInput.Text) || string.IsNullOrWhiteSpace(_cronPhraseInput.Text))
                return;
            var promptText = _cronPromptInput.Text.Trim();
            _core.Scheduler.RegisterJob(_cronNameInput.Text.Trim(), _cronPhraseInput.Text.Trim(), _ => Task.FromResult($"Executed: {promptText}"));
            RefreshCronView();
        };
        formStack.Children.Add(_cronNameInput);
        formStack.Children.Add(_cronPhraseInput);
        formStack.Children.Add(_cronPromptInput);
        formStack.Children.Add(addCronBtn);
        stack.Children.Add(_theme.CreateCard(formStack));

        _cronListStack = new StackPanel();
        stack.Children.Add(_cronListStack);
        return stack;
    }

    private UIElement BuildSettingsSummaryView()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = "⚙ Evrensel API Sağlayıcıları & SSB Evren Yapılandırması",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var infoCardStack = new StackPanel();
        infoCardStack.Children.Add(new TextBlock
        {
            Text = "Tüm API sağlayıcılarını (SSB Evren kurumsal portalı, Google Gemini, OpenAI, Anthropic Claude, OpenRouter, Nous Portal, Yerel Ollama ve Özel vLLM/LiteLLM sunucuları) tek pencereden yapılandırabilir ve canlı bağlantı testi yapabilirsiniz.",
            FontSize = 13,
            Foreground = _theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var openWizardBtn = _theme.CreateButton("⚡ Evrensel API & SSB Evren Kurulum Sihirbazını Aç", isPrimary: true, padding: new Thickness(16, 10, 16, 10), fontSize: 13.5);
        openWizardBtn.HorizontalAlignment = HorizontalAlignment.Left;
        openWizardBtn.Click += (_, _) => OpenSetupWizardDialog();
        infoCardStack.Children.Add(openWizardBtn);

        stack.Children.Add(_theme.CreateCard(infoCardStack, highlight: true));
        return stack;
    }

    // =========================================================================
    // COLLAPSIBLE DEVELOPER DRAWER (Terminal, Diff Viewer, Live Telemetry)
    // =========================================================================
    private Border BuildCollapsibleDevDrawer()
    {
        var tabs = new TabControl
        {
            Background = _theme.BgSecondary,
            BorderBrush = _theme.BorderSubtle,
            Foreground = _theme.TextPrimary,
            Margin = new Thickness(8, 4, 8, 8)
        };

        // Tab 1: Integrated Terminal
        var termDock = new DockPanel { Margin = new Thickness(8) };
        var termInputRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var runTermBtn = _theme.CreateButton("Çalıştır ↵", isPrimary: true);
        runTermBtn.Margin = new Thickness(8, 0, 0, 0);
        runTermBtn.Click += async (_, _) => await RunIntegratedTerminalCommandAsync();
        DockPanel.SetDock(runTermBtn, Dock.Right);

        _terminalCmdInput = _theme.CreateTextBox("", "Komut girin (örn: dotnet --version, git status)...", isMonospace: true);
        _terminalCmdInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RunIntegratedTerminalCommandAsync();
            }
        };
        termInputRow.Children.Add(runTermBtn);
        termInputRow.Children.Add(_terminalCmdInput);
        DockPanel.SetDock(termInputRow, Dock.Bottom);
        termDock.Children.Add(termInputRow);

        _terminalOutputBox = _theme.CreateTextBox("[HOFA Entegre Terminal Hazır]\n", "", isMonospace: true, isMultiline: true);
        _terminalOutputBox.IsReadOnly = true;
        termDock.Children.Add(_terminalOutputBox);

        tabs.Items.Add(new TabItem { Header = "  >_ Entegre Terminal  ", Content = termDock });

        // Tab 2: Diff & Change Approval
        var diffGrid = new Grid { Margin = new Thickness(8) };
        diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var diffLeft = new DockPanel();
        var diffTopRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var previewDiffBtn = _theme.CreateButton("Diff Hesapla", isPrimary: false);
        previewDiffBtn.Margin = new Thickness(6, 0, 0, 0);
        previewDiffBtn.Click += (_, _) => ComputePreviewDiff();
        var applyDiffBtn = _theme.CreateButton("✓ Onayla & Uygula", isPrimary: true);
        applyDiffBtn.Margin = new Thickness(6, 0, 0, 0);
        applyDiffBtn.Click += async (_, _) => await ApproveAndApplyDiffAsync();
        DockPanel.SetDock(applyDiffBtn, Dock.Right);
        DockPanel.SetDock(previewDiffBtn, Dock.Right);
        _diffFilePathInput = _theme.CreateTextBox("README.md", "Dosya adı...", isMonospace: true);
        diffTopRow.Children.Add(applyDiffBtn);
        diffTopRow.Children.Add(previewDiffBtn);
        diffTopRow.Children.Add(_diffFilePathInput);
        DockPanel.SetDock(diffTopRow, Dock.Top);
        diffLeft.Children.Add(diffTopRow);

        _diffProposedInput = _theme.CreateTextBox("", "Önerilen yeni dosya içeriği...", isMonospace: true, isMultiline: true);
        diffLeft.Children.Add(_diffProposedInput);
        Grid.SetColumn(diffLeft, 0);
        diffGrid.Children.Add(diffLeft);

        _diffOutputBox = _theme.CreateTextBox("Unified diff önizlemesi burada görünür...", "", isMonospace: true, isMultiline: true);
        _diffOutputBox.IsReadOnly = true;
        Grid.SetColumn(_diffOutputBox, 2);
        diffGrid.Children.Add(_diffOutputBox);

        tabs.Items.Add(new TabItem { Header = "  ± Diff & Değişiklik Onayı  ", Content = diffGrid });

        // Tab 3: Live Telemetry & Logs
        var telemetryDock = new DockPanel { Margin = new Thickness(8) };
        _telemetrySummaryText = new TextBlock
        {
            Text = "Prompt: 0 tok  |  Yanıt: 0 tok  |  Toplam: 0 tok  |  Tahmini Maliyet: $0.0000",
            FontFamily = _theme.FontMono,
            FontSize = 12,
            Foreground = _theme.AccentGlow,
            Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(_telemetrySummaryText, Dock.Top);
        telemetryDock.Children.Add(_telemetrySummaryText);

        _runtimeLogsList = new ListBox
        {
            Background = _theme.BgPrimary,
            Foreground = _theme.TextSecondary,
            BorderBrush = _theme.BorderSubtle,
            FontFamily = _theme.FontMono,
            FontSize = 11.5
        };
        _runtimeLogsList.Items.Add($"[{DateTime.Now:HH:mm:ss}] [Çekirdek] Saf C# WPF Masaüstü Arayüzü başlatıldı.");
        telemetryDock.Children.Add(_runtimeLogsList);

        tabs.Items.Add(new TabItem { Header = "  📊 Canlı Telemetri & Loglar  ", Content = telemetryDock });

        return new Border
        {
            Background = _theme.BgSecondary,
            BorderBrush = _theme.BorderStrong,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = tabs
        };
    }

    public void ToggleDevDrawer()
    {
        _devDrawerOpen = !_devDrawerOpen;
        if (_devDrawerOpen)
        {
            _devDrawerRow.Height = new GridLength(240);
            _devDrawerContainer.Visibility = Visibility.Visible;
            _devDrawerSplitter.Visibility = Visibility.Visible;
            _devDrawerToggleBtn.Background = _theme.AccentPrimary;
        }
        else
        {
            _devDrawerRow.Height = new GridLength(0);
            _devDrawerContainer.Visibility = Visibility.Collapsed;
            _devDrawerSplitter.Visibility = Visibility.Collapsed;
            _devDrawerToggleBtn.Background = _theme.BgElevated;
        }
    }

    // =========================================================================
    // CTRL+K COMMAND PALETTE OVERLAY
    // =========================================================================
    private Border BuildCommandPaletteOverlay()
    {
        var cardStack = new StackPanel();
        _paletteSearchBox = _theme.CreateTextBox("", "Komut ara... (Esc ile kapat)");
        _paletteSearchBox.FontSize = 14;
        _paletteSearchBox.Margin = new Thickness(0, 0, 0, 8);
        _paletteSearchBox.TextChanged += (_, _) => FilterPaletteItems(_paletteSearchBox.Text);
        _paletteSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                ToggleCommandPalette();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _paletteResultsList.SelectedIndex >= 0)
            {
                ExecuteSelectedPaletteItem();
                e.Handled = true;
            }
        };

        _paletteResultsList = new ListBox
        {
            Background = _theme.BgPrimary,
            Foreground = _theme.TextPrimary,
            BorderBrush = _theme.BorderSubtle,
            FontSize = 13,
            MaxHeight = 280
        };
        _paletteResultsList.MouseDoubleClick += (_, _) => ExecuteSelectedPaletteItem();

        cardStack.Children.Add(_paletteSearchBox);
        cardStack.Children.Add(_paletteResultsList);

        var card = new Border
        {
            Width = 560,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 80, 0, 0),
            Background = _theme.BgSurface,
            BorderBrush = _theme.BorderAccent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = cardStack
        };

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 9, 13, 22)),
            Visibility = Visibility.Collapsed,
            Child = card
        };
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay))
                ToggleCommandPalette();
        };

        return overlay;
    }

    public void ToggleCommandPalette()
    {
        if (_paletteOverlay.Visibility == Visibility.Visible)
        {
            _paletteOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            FilterPaletteItems(string.Empty);
            _paletteSearchBox.Text = string.Empty;
            _paletteOverlay.Visibility = Visibility.Visible;
            _paletteSearchBox.Focus();
        }
    }

    private void FilterPaletteItems(string query)
    {
        _paletteResultsList.Items.Clear();
        var q = query.Trim();
        var matches = string.IsNullOrEmpty(q)
            ? _paletteActions
            : _paletteActions.Where(a => a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Category.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var m in matches)
        {
            var item = new ListBoxItem
            {
                Content = $"{m.Title}   [{m.Category}]",
                Tag = m.Execute,
                Padding = new Thickness(8, 6, 8, 6)
            };
            _paletteResultsList.Items.Add(item);
        }

        if (_paletteResultsList.Items.Count > 0)
            _paletteResultsList.SelectedIndex = 0;
    }

    private void ExecuteSelectedPaletteItem()
    {
        if (_paletteResultsList.SelectedItem is ListBoxItem item && item.Tag is Action act)
        {
            _paletteOverlay.Visibility = Visibility.Collapsed;
            act();
        }
    }

    private void HandleGlobalShortcuts(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.K)
        {
            e.Handled = true;
            ToggleCommandPalette();
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.J)
        {
            e.Handled = true;
            ToggleDevDrawer();
        }
        else if (e.Key == Key.F12)
        {
            e.Handled = true;
            TriggerPanicStop();
        }
    }

    // =========================================================================
    // DATA INITIALIZATION & ACTIONS
    // =========================================================================
    private async Task InitializeWorkspaceDataAsync()
    {
        var sessions = await _core.SessionManager.ListSessionsAsync();
        if (sessions.Count == 0)
        {
            var created = await _core.SessionManager.CreateSessionAsync("Ana Çalışma Oturumu", _settings.Models.DefaultModel, Directory.GetCurrentDirectory());
            _activeSessionId = created.Id;
        }
        else
        {
            var active = sessions.FirstOrDefault(s => s.IsActive) ?? sessions[0];
            _activeSessionId = active.Id;
        }

        await RefreshSessionsUiAsync();
        RefreshFilesList();
        RefreshSkillsView();
        RefreshKanbanView();
        await RefreshMemoryViewAsync();
        RefreshCronView();
    }

    public void OpenSetupWizardDialog()
    {
        var dlg = new NativeSetupDialog(_settings, _core.Router, _settingsFilePath, _theme)
        {
            Owner = IsLoaded ? this : null
        };
        dlg.ShowDialog();
        if (dlg.WasSaved)
        {
            UpdateActiveProviderBadge();
            _composerModelCombo.Text = _settings.Models.DefaultModel;
            if (!_composerModelCombo.Items.Contains(_settings.Models.DefaultModel))
                _composerModelCombo.Items.Insert(0, _settings.Models.DefaultModel);
            AppendRuntimeLog($"[Kurulum] Aktif sağlayıcı '{_settings.PreferredProvider}' ({_settings.Models.DefaultModel}) olarak güncellendi.");
        }
    }

    private void UpdateActiveProviderBadge()
    {
        var prov = string.IsNullOrWhiteSpace(_settings.PreferredProvider) ? "ssb-evren" : _settings.PreferredProvider.ToUpperInvariant();
        var mod = string.IsNullOrWhiteSpace(_settings.Models.DefaultModel) ? "default" : _settings.Models.DefaultModel;
        _providerBadgeText.Text = $"⚡ {prov} · {mod}";
    }

    private async Task CreateAndActivateSessionAsync(string title)
    {
        var s = await _core.SessionManager.CreateSessionAsync(title, _settings.Models.DefaultModel, Directory.GetCurrentDirectory());
        await ActivateSessionByIdAsync(s.Id);
    }

    private async Task ActivateSessionByIdAsync(string sessionId)
    {
        _activeSessionId = sessionId;
        await _core.SessionManager.SetActiveSessionAsync(sessionId);
        await RefreshSessionsUiAsync();
    }

    private async Task RefreshSessionsUiAsync()
    {
        var sessions = await _core.SessionManager.ListSessionsAsync();
        _sessionComboBox.Items.Clear();
        _sessionsListStack.Children.Clear();

        foreach (var s in sessions)
        {
            var cbItem = new ComboBoxItem { Content = s.Title, Tag = s.Id };
            _sessionComboBox.Items.Add(cbItem);
            if (s.Id == _activeSessionId)
                _sessionComboBox.SelectedItem = cbItem;

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var switchBtn = _theme.CreateButton(s.Id == _activeSessionId ? "✓ Aktif" : "Seç", isPrimary: s.Id == _activeSessionId);
            var sid = s.Id;
            switchBtn.Click += async (_, _) => await ActivateSessionByIdAsync(sid);
            DockPanel.SetDock(switchBtn, Dock.Right);

            var lbl = new TextBlock
            {
                Text = $"{s.Title}  ·  {s.Model}  ({s.MessageCount} mesaj)",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _theme.TextPrimary
            };
            row.Children.Add(switchBtn);
            row.Children.Add(lbl);
            _sessionsListStack.Children.Add(_theme.CreateCard(row, new Thickness(10, 8, 10, 8), new Thickness(0, 0, 0, 6)));
        }
    }

    private void RefreshFilesList()
    {
        _filesListBox.Items.Clear();
        var root = Directory.GetCurrentDirectory();
        try
        {
            foreach (var f in Directory.GetFiles(root, "*.*", SearchOption.TopDirectoryOnly).Take(60))
            {
                _filesListBox.Items.Add(Path.GetFileName(f));
            }
        }
        catch { }
    }

    private void LoadFileIntoEditor(string relativePath)
    {
        var full = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
        if (!File.Exists(full)) return;
        _editorPathInput.Text = relativePath;
        _codeEditorBox.Text = File.ReadAllText(full);
        _editorStatusText.Text = $"Yüklendi: {relativePath} ({_codeEditorBox.Text.Length} bayt)";
    }

    private void SaveEditorFile()
    {
        var rel = _editorPathInput.Text.Trim();
        if (string.IsNullOrEmpty(rel)) return;
        var full = Path.Combine(Directory.GetCurrentDirectory(), rel);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, _codeEditorBox.Text);
        _editorStatusText.Text = $"✓ Kaydedildi: {rel} ({DateTime.Now:HH:mm:ss})";
        RefreshFilesList();
    }

    private void RefreshSkillsView()
    {
        _skillsListStack.Children.Clear();
        foreach (var sk in _core.SkillManager.InstalledSkills.Values)
        {
            _skillsListStack.Children.Add(_theme.CreateCard(new TextBlock
            {
                Text = $"🧩 {sk.Name} ({sk.Provider}) — {sk.Description}",
                Foreground = _theme.TextPrimary
            }, new Thickness(10, 8, 10, 8), new Thickness(0, 0, 0, 6)));
        }
    }

    private void RefreshKanbanView()
    {
        _kanbanPendingStack.Children.Clear();
        _kanbanProgressStack.Children.Clear();
        _kanbanDoneStack.Children.Clear();

        _kanbanPendingStack.Children.Add(CreateSidebarGroupLabel("BEKLEYEN (PENDING)"));
        _kanbanProgressStack.Children.Add(CreateSidebarGroupLabel("ÇALIŞIYOR (IN PROGRESS)"));
        _kanbanDoneStack.Children.Add(CreateSidebarGroupLabel("TAMAMLANDI (DONE)"));

        foreach (var card in _core.SubAgents.Kanban.AllCards)
        {
            var tb = new TextBlock { Text = $"• {card.Title}", Foreground = _theme.TextPrimary, Margin = new Thickness(0, 4, 0, 4) };
            switch (card.Column)
            {
                case KanbanColumn.Pending:
                    _kanbanPendingStack.Children.Add(tb);
                    break;
                case KanbanColumn.InProgress:
                    _kanbanProgressStack.Children.Add(tb);
                    break;
                case KanbanColumn.Done:
                    _kanbanDoneStack.Children.Add(tb);
                    break;
            }
        }
    }

    private async Task RefreshMemoryViewAsync(string? query = null)
    {
        _memoryListStack.Children.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            var doc = await _core.MemoryStore.ReadDocumentAsync(MemoryDocumentType.Memory);
            _memoryListStack.Children.Add(_theme.CreateCard(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(doc) ? "🧠 Henüz kayıtlı bellek girdisi yok." : doc.Trim(),
                Foreground = _theme.TextPrimary,
                TextWrapping = TextWrapping.Wrap
            }, new Thickness(10, 8, 10, 8), new Thickness(0, 0, 0, 6)));
        }
        else
        {
            var hits = await _core.MemoryStore.SearchMemoriesAsync(query, null, 15);
            foreach (var h in hits)
            {
                _memoryListStack.Children.Add(_theme.CreateCard(new TextBlock
                {
                    Text = $"🧠 {h.Title}: {h.Content}",
                    Foreground = _theme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap
                }, new Thickness(10, 8, 10, 8), new Thickness(0, 0, 0, 6)));
            }
        }
    }

    private void RefreshCronView()
    {
        _cronListStack.Children.Clear();
        foreach (var job in _core.Scheduler.ListJobs())
        {
            _cronListStack.Children.Add(_theme.CreateCard(new TextBlock
            {
                Text = $"⏰ {job.Name} ({job.CronExpression}) — Sonraki: {job.NextRun:HH:mm:ss}",
                Foreground = _theme.TextPrimary
            }, new Thickness(10, 8, 10, 8), new Thickness(0, 0, 0, 6)));
        }
    }

    private async Task RunIntegratedTerminalCommandAsync()
    {
        var cmd = _terminalCmdInput.Text.Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        _terminalCmdInput.Text = string.Empty;

        _terminalOutputBox.AppendText($"\n❯ {cmd}\n");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c \"{cmd}\"" : $"-c \"{cmd}\"",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (!string.IsNullOrEmpty(stdout)) _terminalOutputBox.AppendText(stdout);
                if (!string.IsNullOrEmpty(stderr)) _terminalOutputBox.AppendText(stderr);
            }
        }
        catch (Exception ex)
        {
            _terminalOutputBox.AppendText($"Hata: {ex.Message}\n");
        }
        _terminalOutputBox.ScrollToEnd();
    }

    private void ComputePreviewDiff()
    {
        var path = _diffFilePathInput.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;
        var full = Path.Combine(Directory.GetCurrentDirectory(), path);
        var existing = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
        var doc = _core.DiffService.ComputeDiff(path, existing, _diffProposedInput.Text);
        _diffOutputBox.Text = _core.DiffService.FormatUnifiedDiff(doc);
    }

    private async Task ApproveAndApplyDiffAsync()
    {
        var path = _diffFilePathInput.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;
        var full = Path.Combine(Directory.GetCurrentDirectory(), path);
        var existing = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
        var doc = _core.DiffService.ComputeDiff(full, existing, _diffProposedInput.Text);
        await _core.DiffService.ApplyDiffAsync(doc, createBackup: true);
        _diffOutputBox.Text = $"✓ Değişiklik onaylandı ve uygulandı: {path}";
    }

    private void UpdateTelemetrySummary()
    {
        int total = _totalPromptTokens + _totalCompletionTokens;
        double cost = total * 0.000002;
        _telemetrySummaryText.Text = $"Prompt: {_totalPromptTokens} tok  |  Yanıt: {_totalCompletionTokens} tok  |  Toplam: {total} tok  |  Tahmini Maliyet: ${cost:F4}";
    }

    private void AppendRuntimeLog(string message)
    {
        _runtimeLogsList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        _runtimeLogsList.ScrollIntoView(_runtimeLogsList.Items[^1]);
    }

    private void ToggleTheme()
    {
        var next = _theme.IsDark ? "light" : "dark";
        _settings.Appearance.Theme = next;
        _theme = new NativeTheme(next);
        BuildWindowLayout();
    }

    private void TriggerPanicStop()
    {
        _core.Controller.PanicKillSwitch?.TriggerPanic("Native WPF Panic Button");
        AppendRuntimeLog("[PANIC STOP] Donanım/Ajan acil durdurma anahtarı tetiklendi!");
        MessageBox.Show(this, "Acil Durdurma (PANIC STOP) tetiklendi. Tüm otonom işlemler durduruldu.", "HOFA Güvenlik", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private DesktopSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<DesktopSettings>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                if (loaded != null && loaded.OnboardingCompleted) return loaded;
            }

            var globalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".humofa-data", "settings.json");
            if (File.Exists(globalPath))
            {
                var globalJson = File.ReadAllText(globalPath);
                var globalLoaded = JsonSerializer.Deserialize<DesktopSettings>(globalJson, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                if (globalLoaded != null) return globalLoaded;
            }
        }
        catch { }
        return new DesktopSettings { PreferredProvider = "ssb-evren", DefaultWorkingDir = Directory.GetCurrentDirectory() };
    }

    private TextBlock CreateSidebarGroupLabel(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeights.Bold,
        Foreground = _theme.TextMuted,
        Margin = new Thickness(4, 4, 0, 6)
    };
}
