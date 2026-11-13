using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HUMofA.Agent;

namespace HUMofA.Desktop.Native;

/// <summary>
/// Pure C# WPF Universal API &amp; SSB Evren Setup / Reconfiguration Dialog.
/// Appears on first run or anytime via the top header bar / Ctrl+K palette.
/// </summary>
public sealed class NativeSetupDialog : Window
{
    public sealed record ProviderPreset(
        string Id,
        string Title,
        string Subtitle,
        string DefaultModel,
        string DefaultEndpoint,
        bool RequiresEndpoint,
        bool RequiresApiKey,
        bool SupportsModelDiscovery);

    public static readonly IReadOnlyList<ProviderPreset> Presets =
    [
        new("ssb-evren", "SSB Evren (Kurumsal AI)", "Özel URL · Bearer/Header · Kurumsal SSL", "deepseek-v4.1-flash", "https://evren-llmapi.ssyz.org.tr/v1", true, true, true),
        new("gemini", "Google Gemini", "Gemini 2.5 Flash & Pro", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta/models", false, true, false),
        new("openai", "OpenAI", "GPT-4o · o3-mini", "gpt-4o", "https://api.openai.com/v1/chat/completions", false, true, true),
        new("anthropic", "Anthropic Claude", "Claude 3.7 Sonnet", "claude-3-7-sonnet-latest", "https://api.anthropic.com/v1/messages", false, true, false),
        new("openrouter", "OpenRouter", "200+ Model Tek API", "openrouter/auto", "https://openrouter.ai/api/v1/chat/completions", false, true, true),
        new("nous", "Nous Portal", "Hermes 3 Llama 3.1 405B", "nous/hermes-3-llama-3.1-405b", "https://inference-api.nousresearch.com/v1/chat/completions", false, true, true),
        new("ollama", "Yerel Ollama", "%100 Çevrimdışı & Yerel", "ollama/qwen2.5-coder:latest", "http://127.0.0.1:11434", true, false, false),
        new("custom", "Evrensel Özel API", "vLLM · LiteLLM · OpenWebUI · Gateway", "custom-model", "http://localhost:8000/v1/chat/completions", true, true, true)
    ];

    private readonly NativeTheme _theme;
    private readonly DesktopSettings _settings;
    private readonly MultiModelRouter _router;
    private readonly string _settingsFilePath;

    private readonly Dictionary<string, Border> _providerCards = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedProviderId = "ssb-evren";

    private StackPanel _endpointContainer = null!;
    private TextBlock _endpointLabel = null!;
    private TextBox _endpointTextBox = null!;

    private StackPanel _apiKeyContainer = null!;
    private TextBlock _apiKeyLabel = null!;
    private PasswordBox _apiKeyPasswordBox = null!;
    private TextBox _apiKeyPlainTextBox = null!;
    private CheckBox _showKeyCheckBox = null!;

    private StackPanel _advancedAuthContainer = null!;
    private TextBox _customHeaderTextBox = null!;
    private CheckBox _ignoreSslCheckBox = null!;

    private ComboBox _modelComboBox = null!;
    private Button _fetchModelsButton = null!;
    private Button _testConnectionButton = null!;
    private Border _testResultBanner = null!;
    private TextBlock _testResultText = null!;

    private ComboBox _autonomyComboBox = null!;
    private ComboBox _themeComboBox = null!;
    private TextBox _workingDirTextBox = null!;

    public string SelectedProviderId => _selectedProviderId;
    public bool WasSaved { get; private set; }

    public NativeSetupDialog(
        DesktopSettings settings,
        MultiModelRouter router,
        string settingsFilePath,
        NativeTheme? theme = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _settingsFilePath = settingsFilePath ?? throw new ArgumentNullException(nameof(settingsFilePath));
        _theme = theme ?? new NativeTheme(_settings.Appearance.Theme);

        Title = "HOFA — Evrensel API & SSB Evren Kurulum Sihirbazı";
        Width = 760;
        Height = 690;
        MinWidth = 660;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = _theme.BgPrimary;
        Foreground = _theme.TextPrimary;
        FontFamily = _theme.FontUi;

        _selectedProviderId = !string.IsNullOrWhiteSpace(_settings.PreferredProvider)
            ? _settings.PreferredProvider
            : "ssb-evren";

        BuildUi();
        SelectProvider(_selectedProviderId, initialLoad: true);
    }

    private void BuildUi()
    {
        var rootGrid = new Grid { Margin = new Thickness(22) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var titleRow = new DockPanel();
        var titleBlock = new TextBlock
        {
            Text = "⚡ Evrensel AI Sağlayıcı & SSB Evren Kurulumu",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = _theme.TextPrimary
        };
        var badge = _theme.CreatePill("Her Zaman Değiştirilebilir", _theme.AccentGlow, _theme.AccentSoftBg);
        DockPanel.SetDock(badge, Dock.Right);
        titleRow.Children.Add(badge);
        titleRow.Children.Add(titleBlock);

        var subBlock = new TextBlock
        {
            Text = "SSB Evren kurumsal portalı, bulut sağlayıcılar (Gemini, OpenAI, Claude, OpenRouter) veya yerel modellerden birini seçin. Ayarlar hem Masaüstü hem CLI ('hofa') için anında etkinleşir.",
            FontSize = 12.5,
            Foreground = _theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        headerStack.Children.Add(titleRow);
        headerStack.Children.Add(subBlock);
        Grid.SetRow(headerStack, 0);
        rootGrid.Children.Add(headerStack);

        // Scrollable Body
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0)
        };
        var bodyStack = new StackPanel();

        // Section 1: Provider Grid
        var sec1Stack = new StackPanel();
        sec1Stack.Children.Add(CreateSectionHeader("1. AI / API Sağlayıcısını Seçin"));

        var providerGrid = new UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, 6, 0, 4)
        };

        foreach (var preset in Presets)
        {
            var card = CreateProviderCard(preset);
            _providerCards[preset.Id] = card;
            providerGrid.Children.Add(card);
        }

        sec1Stack.Children.Add(providerGrid);
        bodyStack.Children.Add(_theme.CreateCard(sec1Stack, new Thickness(14, 12, 14, 12)));

        // Section 2: Endpoint, Credentials & Corporate SSL (SSB Evren Ready)
        var sec2Stack = new StackPanel();
        sec2Stack.Children.Add(CreateSectionHeader("2. Bağlantı Adresi, Kimlik Doğrulama & Canlı Test"));

        _endpointContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        _endpointLabel = CreateFieldLabel("API Endpoint / Kurumsal Portal URL'si (Örn: https://evren.ssb.gov.tr/v1/chat/completions)");
        _endpointTextBox = _theme.CreateTextBox(_settings.ApiKeys.EvrenEndpoint ?? "https://evren.ssb.gov.tr/v1/chat/completions", isMonospace: true);
        _endpointContainer.Children.Add(_endpointLabel);
        _endpointContainer.Children.Add(_endpointTextBox);
        sec2Stack.Children.Add(_endpointContainer);

        _apiKeyContainer = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        var keyHeaderDock = new DockPanel();
        _apiKeyLabel = CreateFieldLabel("API Anahtarı / Bearer Token");
        _showKeyCheckBox = new CheckBox
        {
            Content = "Anahtarı Göster",
            Foreground = _theme.TextSecondary,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        _showKeyCheckBox.Checked += (_, _) =>
        {
            _apiKeyPlainTextBox.Text = _apiKeyPasswordBox.Password;
            _apiKeyPasswordBox.Visibility = Visibility.Collapsed;
            _apiKeyPlainTextBox.Visibility = Visibility.Visible;
        };
        _showKeyCheckBox.Unchecked += (_, _) =>
        {
            _apiKeyPasswordBox.Password = _apiKeyPlainTextBox.Text;
            _apiKeyPlainTextBox.Visibility = Visibility.Collapsed;
            _apiKeyPasswordBox.Visibility = Visibility.Visible;
        };
        DockPanel.SetDock(_showKeyCheckBox, Dock.Right);
        keyHeaderDock.Children.Add(_showKeyCheckBox);
        keyHeaderDock.Children.Add(_apiKeyLabel);

        _apiKeyPasswordBox = new PasswordBox
        {
            FontFamily = _theme.FontMono,
            FontSize = 13,
            Foreground = _theme.TextPrimary,
            Background = _theme.BgPrimary,
            BorderBrush = _theme.BorderStrong,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7)
        };
        _apiKeyPlainTextBox = _theme.CreateTextBox("", isMonospace: true);
        _apiKeyPlainTextBox.Visibility = Visibility.Collapsed;

        _apiKeyContainer.Children.Add(keyHeaderDock);
        _apiKeyContainer.Children.Add(_apiKeyPasswordBox);
        _apiKeyContainer.Children.Add(_apiKeyPlainTextBox);
        sec2Stack.Children.Add(_apiKeyContainer);

        // Advanced Auth & SSL Row (Especially for SSB Evren / Custom Gateways)
        _advancedAuthContainer = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var advGrid = new Grid();
        advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var headerCol = new StackPanel();
        headerCol.Children.Add(CreateFieldLabel("Özel Yetkilendirme Başlığı (Boş = Authorization: Bearer)"));
        _customHeaderTextBox = _theme.CreateTextBox("", "Örn: x-api-key veya api-key", isMonospace: true);
        headerCol.Children.Add(_customHeaderTextBox);
        Grid.SetColumn(headerCol, 0);
        advGrid.Children.Add(headerCol);

        var sslCol = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        _ignoreSslCheckBox = new CheckBox
        {
            Content = "Kurumsal İç Ağ SSL Sertifikasını Esnet (Self-Signed TLS)",
            Foreground = _theme.TextPrimary,
            FontSize = 12,
            IsChecked = _settings.ApiKeys.EvrenIgnoreSslErrors,
            ToolTip = "SSB Evren gibi kurum içi kök sertifika kullanan sunucularda SSL hatasını önler."
        };
        sslCol.Children.Add(_ignoreSslCheckBox);
        Grid.SetColumn(sslCol, 2);
        advGrid.Children.Add(sslCol);

        _advancedAuthContainer.Children.Add(advGrid);
        sec2Stack.Children.Add(_advancedAuthContainer);

        // Test & Model Discovery Buttons Row
        var testBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        _testConnectionButton = _theme.CreateButton("⚡ Canlı Bağlantıyı Test Et", isPrimary: false, padding: new Thickness(14, 7, 14, 7));
        _testConnectionButton.Click += async (_, _) => await TestConnectionAsync();

        _fetchModelsButton = _theme.CreateButton("🔄 Sunucudan Modelleri Çek (/v1/models)", isPrimary: false, padding: new Thickness(14, 7, 14, 7));
        _fetchModelsButton.Margin = new Thickness(10, 0, 0, 0);
        _fetchModelsButton.Click += async (_, _) => await FetchModelsFromServerAsync();

        testBtnRow.Children.Add(_testConnectionButton);
        testBtnRow.Children.Add(_fetchModelsButton);
        sec2Stack.Children.Add(testBtnRow);

        _testResultText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _theme.TextPrimary
        };
        _testResultBanner = new Border
        {
            Background = _theme.BgPrimary,
            BorderBrush = _theme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _testResultText
        };
        sec2Stack.Children.Add(_testResultBanner);

        bodyStack.Children.Add(_theme.CreateCard(sec2Stack, new Thickness(14, 12, 14, 12)));

        // Section 3: Model, Autonomy & Appearance
        var sec3Stack = new StackPanel();
        sec3Stack.Children.Add(CreateSectionHeader("3. Varsayılan Model, Otonomi & Çalışma Alanı"));

        var row3Grid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });

        var modelCol = new StackPanel();
        modelCol.Children.Add(CreateFieldLabel("Varsayılan Model Adı (Seçin veya Yazın)"));
        _modelComboBox = new ComboBox
        {
            IsEditable = true,
            FontFamily = _theme.FontMono,
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6),
            Text = _settings.Models.DefaultModel
        };
        foreach (var m in _settings.Models.AvailableModels)
            _modelComboBox.Items.Add(m);
        modelCol.Children.Add(_modelComboBox);
        Grid.SetColumn(modelCol, 0);
        row3Grid.Children.Add(modelCol);

        var autonomyCol = new StackPanel();
        autonomyCol.Children.Add(CreateFieldLabel("Otonomi Seviyesi"));
        _autonomyComboBox = new ComboBox
        {
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6)
        };
        _autonomyComboBox.Items.Add("Guarded");
        _autonomyComboBox.Items.Add("Conservative");
        _autonomyComboBox.Items.Add("FullAutonomy");
        _autonomyComboBox.SelectedItem = string.IsNullOrWhiteSpace(_settings.Permissions.DefaultAutonomyTier)
            ? "Guarded"
            : _settings.Permissions.DefaultAutonomyTier;
        autonomyCol.Children.Add(_autonomyComboBox);
        Grid.SetColumn(autonomyCol, 2);
        row3Grid.Children.Add(autonomyCol);

        var themeCol = new StackPanel();
        themeCol.Children.Add(CreateFieldLabel("Arayüz Teması"));
        _themeComboBox = new ComboBox
        {
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6)
        };
        _themeComboBox.Items.Add("dark");
        _themeComboBox.Items.Add("light");
        _themeComboBox.SelectedItem = _settings.Appearance.Theme == "light" ? "light" : "dark";
        themeCol.Children.Add(_themeComboBox);
        Grid.SetColumn(themeCol, 4);
        row3Grid.Children.Add(themeCol);

        sec3Stack.Children.Add(row3Grid);

        sec3Stack.Children.Add(CreateFieldLabel("Varsayılan Çalışma Klasörü"));
        _workingDirTextBox = _theme.CreateTextBox(
            string.IsNullOrWhiteSpace(_settings.DefaultWorkingDir) ? Directory.GetCurrentDirectory() : _settings.DefaultWorkingDir,
            isMonospace: true);
        sec3Stack.Children.Add(_workingDirTextBox);

        bodyStack.Children.Add(_theme.CreateCard(sec3Stack, new Thickness(14, 12, 14, 12)));

        scroll.Content = bodyStack;
        Grid.SetRow(scroll, 1);
        rootGrid.Children.Add(scroll);

        // Footer Buttons
        var footerDock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var skipBtn = _theme.CreateButton("Şimdilik Geç (Çevrimdışı Mod)", isPrimary: false, padding: new Thickness(14, 8, 14, 8));
        skipBtn.Click += (_, _) =>
        {
            WasSaved = false;
            DialogResult = false;
            Close();
        };

        var saveBtn = _theme.CreateButton("✓ Kaydet ve Sağlayıcıyı Etkinleştir", isPrimary: true, padding: new Thickness(20, 8, 20, 8), fontSize: 13.5);
        saveBtn.Click += (_, _) => SaveAndCompleteSetup();

        DockPanel.SetDock(saveBtn, Dock.Right);
        footerDock.Children.Add(saveBtn);
        footerDock.Children.Add(skipBtn);

        Grid.SetRow(footerDock, 2);
        rootGrid.Children.Add(footerDock);

        Content = rootGrid;
    }

    private Border CreateProviderCard(ProviderPreset preset)
    {
        var stack = new StackPanel();
        var title = new TextBlock
        {
            Text = preset.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _theme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var sub = new TextBlock
        {
            Text = preset.Subtitle,
            FontSize = 10.5,
            Foreground = _theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        stack.Children.Add(title);
        stack.Children.Add(sub);

        var card = new Border
        {
            Background = _theme.BgPrimary,
            BorderBrush = _theme.BorderStrong,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(3),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = stack
        };

        card.MouseLeftButtonDown += (_, _) => SelectProvider(preset.Id, initialLoad: false);
        return card;
    }

    public void SelectProvider(string providerId, bool initialLoad = false)
    {
        var norm = providerId.Trim().ToLowerInvariant();
        if (norm == "evren") norm = "ssb-evren";

        var preset = Presets.FirstOrDefault(p => p.Id.Equals(norm, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];
        _selectedProviderId = preset.Id;

        foreach (var (id, border) in _providerCards)
        {
            bool active = id.Equals(_selectedProviderId, StringComparison.OrdinalIgnoreCase);
            border.BorderBrush = active ? _theme.AccentGlow : _theme.BorderStrong;
            border.Background = active ? _theme.AccentSoftBg : _theme.BgPrimary;
        }

        _endpointContainer.Visibility = preset.RequiresEndpoint ? Visibility.Visible : Visibility.Collapsed;
        _apiKeyContainer.Visibility = preset.RequiresApiKey ? Visibility.Visible : Visibility.Collapsed;
        _advancedAuthContainer.Visibility = (_selectedProviderId == "ssb-evren" || _selectedProviderId == "custom")
            ? Visibility.Visible
            : Visibility.Collapsed;
        _fetchModelsButton.Visibility = preset.SupportsModelDiscovery ? Visibility.Visible : Visibility.Collapsed;

        if (_selectedProviderId == "ssb-evren")
        {
            _endpointLabel.Text = "SSB Evren Portal / API Endpoint URL (Örn: https://evren.ssb.gov.tr/v1/chat/completions)";
            _endpointTextBox.Text = FirstNonEmpty(_settings.ApiKeys.EvrenEndpoint, preset.DefaultEndpoint);
            _apiKeyLabel.Text = "SSB Evren API Anahtarı / Kurumsal Token";
            SetKeyBoxValue(_settings.ApiKeys.EvrenApiKey ?? string.Empty);
            _customHeaderTextBox.Text = _settings.ApiKeys.EvrenCustomHeader ?? string.Empty;
            _ignoreSslCheckBox.IsChecked = _settings.ApiKeys.EvrenIgnoreSslErrors;
        }
        else if (_selectedProviderId == "custom")
        {
            _endpointLabel.Text = "Özel OpenAI-Uyumlu API Endpoint URL";
            _endpointTextBox.Text = FirstNonEmpty(_settings.ApiKeys.CustomEndpoint, preset.DefaultEndpoint);
            _apiKeyLabel.Text = "API Anahtarı / Bearer Token (Opsiyonel)";
            SetKeyBoxValue(_settings.ApiKeys.CustomApiKey ?? string.Empty);
            _customHeaderTextBox.Text = _settings.ApiKeys.CustomAuthHeader ?? string.Empty;
            _ignoreSslCheckBox.IsChecked = _settings.ApiKeys.CustomIgnoreSslErrors;
        }
        else if (_selectedProviderId == "ollama")
        {
            _endpointLabel.Text = "Yerel Ollama Sunucu Adresi";
            _endpointTextBox.Text = FirstNonEmpty(_settings.ApiKeys.OllamaEndpoint, preset.DefaultEndpoint);
        }
        else
        {
            _apiKeyLabel.Text = $"{preset.Title} API Anahtarı";
            string? existing = _selectedProviderId switch
            {
                "gemini" => _settings.ApiKeys.GeminiApiKey,
                "openai" => _settings.ApiKeys.OpenAiApiKey,
                "anthropic" => _settings.ApiKeys.AnthropicApiKey,
                "openrouter" => _settings.ApiKeys.OpenRouterApiKey,
                "nous" => _settings.ApiKeys.NousApiKey,
                _ => null
            };
            SetKeyBoxValue(existing ?? string.Empty);
        }

        if (!initialLoad || string.IsNullOrWhiteSpace(_modelComboBox.Text))
        {
            _modelComboBox.Text = preset.DefaultModel;
        }
    }

    private void SetKeyBoxValue(string val)
    {
        _apiKeyPasswordBox.Password = val;
        _apiKeyPlainTextBox.Text = val;
    }

    private string GetCurrentApiKey()
    {
        return _showKeyCheckBox.IsChecked == true
            ? _apiKeyPlainTextBox.Text.Trim()
            : _apiKeyPasswordBox.Password.Trim();
    }

    private async Task FetchModelsFromServerAsync()
    {
        var endpoint = _endpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            var preset = Presets.FirstOrDefault(p => p.Id == _selectedProviderId);
            endpoint = preset?.DefaultEndpoint ?? "https://api.openai.com/v1/chat/completions";
        }

        ShowBanner("Sunucudan model listesi çekiliyor (/v1/models)...", isError: false, isSuccess: false);
        try
        {
            var models = await OpenAiCompatibleLiveClient.FetchAvailableModelsAsync(
                endpoint,
                GetCurrentApiKey(),
                _customHeaderTextBox.Text.Trim(),
                _ignoreSslCheckBox.IsChecked == true);

            if (models.Count > 0)
            {
                _modelComboBox.Items.Clear();
                foreach (var m in models)
                    _modelComboBox.Items.Add(m);
                _modelComboBox.SelectedIndex = 0;
                ShowBanner($"✓ Sunucudan {models.Count} adet model başarıyla yüklendi: {string.Join(", ", models.Take(5))}", isError: false, isSuccess: true);
            }
            else
            {
                ShowBanner("Sunucu yanıt verdi ancak model listesi boş döndü. Model adını elle yazabilirsiniz.", isError: false, isSuccess: false);
            }
        }
        catch (Exception ex)
        {
            ShowBanner($"✗ Model listesi çekilemedi: {ex.Message}", isError: true, isSuccess: false);
        }
    }

    private async Task TestConnectionAsync()
    {
        var model = string.IsNullOrWhiteSpace(_modelComboBox.Text) ? "default" : _modelComboBox.Text.Trim();
        var endpoint = _endpointContainer.Visibility == Visibility.Visible ? _endpointTextBox.Text.Trim() : null;
        var apiKey = GetCurrentApiKey();

        ShowBanner($"⚡ {_selectedProviderId.ToUpperInvariant()} ({model}) bağlantısı test ediliyor...", isError: false, isSuccess: false);

        var res = await MultiModelRouter.TestProviderConnectionAsync(
            _selectedProviderId,
            apiKey,
            model,
            endpoint,
            customHandler: null,
            ct: CancellationToken.None,
            customAuthHeader: _customHeaderTextBox.Text.Trim(),
            ignoreSslErrors: _ignoreSslCheckBox.IsChecked == true);

        if (res.Success)
        {
            ShowBanner($"✓ Bağlantı Başarılı ({res.LatencyMs} ms): {res.Message}", isError: false, isSuccess: true);
        }
        else
        {
            ShowBanner($"✗ Bağlantı Hatası ({res.LatencyMs} ms): {res.Message}", isError: true, isSuccess: false);
        }
    }

    private void ShowBanner(string text, bool isError, bool isSuccess)
    {
        _testResultText.Text = text;
        _testResultBanner.Visibility = Visibility.Visible;
        if (isError)
        {
            _testResultBanner.BorderBrush = _theme.Danger;
            _testResultText.Foreground = _theme.Danger;
        }
        else if (isSuccess)
        {
            _testResultBanner.BorderBrush = _theme.Success;
            _testResultText.Foreground = _theme.Success;
        }
        else
        {
            _testResultBanner.BorderBrush = _theme.BorderAccent;
            _testResultText.Foreground = _theme.AccentGlow;
        }
    }

    public void SaveAndCompleteSetup()
    {
        _settings.PreferredProvider = _selectedProviderId;
        _settings.OnboardingCompleted = true;

        var chosenModel = string.IsNullOrWhiteSpace(_modelComboBox.Text) ? "gemini-2.5-flash" : _modelComboBox.Text.Trim();
        _settings.Models.DefaultModel = chosenModel;
        if (!_settings.Models.AvailableModels.Contains(chosenModel, StringComparer.OrdinalIgnoreCase))
            _settings.Models.AvailableModels.Insert(0, chosenModel);

        var key = GetCurrentApiKey();
        var endpoint = _endpointTextBox.Text.Trim();

        switch (_selectedProviderId)
        {
            case "ssb-evren":
                if (!string.IsNullOrWhiteSpace(endpoint))
                    _settings.ApiKeys.EvrenEndpoint = endpoint;
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.EvrenApiKey = key;
                _settings.ApiKeys.EvrenCustomHeader = _customHeaderTextBox.Text.Trim();
                _settings.ApiKeys.EvrenIgnoreSslErrors = _ignoreSslCheckBox.IsChecked == true;
                break;

            case "custom":
                if (!string.IsNullOrWhiteSpace(endpoint))
                    _settings.ApiKeys.CustomEndpoint = endpoint;
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.CustomApiKey = key;
                _settings.ApiKeys.CustomAuthHeader = _customHeaderTextBox.Text.Trim();
                _settings.ApiKeys.CustomIgnoreSslErrors = _ignoreSslCheckBox.IsChecked == true;
                break;

            case "ollama":
                if (!string.IsNullOrWhiteSpace(endpoint))
                    _settings.ApiKeys.OllamaEndpoint = endpoint;
                break;

            case "gemini":
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.GeminiApiKey = key;
                break;

            case "openai":
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.OpenAiApiKey = key;
                break;

            case "anthropic":
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.AnthropicApiKey = key;
                break;

            case "openrouter":
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.OpenRouterApiKey = key;
                break;

            case "nous":
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains("***"))
                    _settings.ApiKeys.NousApiKey = key;
                break;
        }

        if (_autonomyComboBox.SelectedItem is string tier && !string.IsNullOrWhiteSpace(tier))
            _settings.Permissions.DefaultAutonomyTier = tier;

        if (_themeComboBox.SelectedItem is string themeStr && !string.IsNullOrWhiteSpace(themeStr))
            _settings.Appearance.Theme = themeStr;

        if (!string.IsNullOrWhiteSpace(_workingDirTextBox.Text))
            _settings.DefaultWorkingDir = _workingDirTextBox.Text.Trim();

        PersistSettingsToDisk();
        _router.ConfigureFromSettings(_settings);

        WasSaved = true;
        if (IsLoaded)
        {
            try { DialogResult = true; } catch { }
            Close();
        }
    }

    private void PersistSettingsToDisk()
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var tmp = _settingsFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _settingsFilePath, overwrite: true);

        try
        {
            var globalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".humofa-data");
            Directory.CreateDirectory(globalDir);
            var globalPath = Path.Combine(globalDir, "settings.json");
            if (!string.Equals(Path.GetFullPath(_settingsFilePath), Path.GetFullPath(globalPath), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(globalPath, json);
            }
        }
        catch { }
    }

    private TextBlock CreateSectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        FontWeight = FontWeights.Bold,
        Foreground = _theme.AccentGlow,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private TextBlock CreateFieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = _theme.TextSecondary,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static string FirstNonEmpty(string? a, string fallback) =>
        !string.IsNullOrWhiteSpace(a) ? a : fallback;
}

