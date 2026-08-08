using System.Collections.ObjectModel;
using MeetingTranscriber.App.Services;

namespace MeetingTranscriber.App.ViewModels;

/// <summary>
/// View model for the settings window: STT model selection and the
/// OpenAI-compatible API endpoint used for summarization.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _original;

    private string _baseUrl;
    private string _apiKey;
    private string _model;
    private int _maxTokens;
    private string _selectedVariant;
    private bool _autoDownload;
    private string _device;
    private string _cacheDir;
    private string _outputDir;
    private string _status = "";
    private bool _isTesting;
    private string _selectedModelSuggestion = "";

    public SettingsViewModel()
    {
        _original = _settingsService.Load();

        _baseUrl = _original.Api.BaseUrl;
        _apiKey = _original.Api.ApiKey;
        _model = _original.Api.Model;
        _maxTokens = _original.Api.MaxTokens;
        _selectedVariant = _original.Stt.Variant;
        _autoDownload = _original.Stt.AutoDownload;
        _device = _original.Stt.Device;
        _cacheDir = _original.Stt.CacheDir;
        _outputDir = _original.OutputDir;

        foreach (var v in SettingsService.ValidSttVariants)
            Variants.Add(v);

        if (!Variants.Contains(_selectedVariant))
            _selectedVariant = Variants[0];

        TestConnectionCommand = new RelayCommand(_ => _ = TestAsync());
        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => OnCancel?.Invoke());
        RefreshModelsCommand = new RelayCommand(_ => _ = TestAsync(populateOnly: true));
    }

    public ObservableCollection<string> Variants { get; } = new();
    public ObservableCollection<string> ModelSuggestions { get; } = new();

    public RelayCommand TestConnectionCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RefreshModelsCommand { get; }

    public string BaseUrl
    {
        get => _baseUrl;
        set => Set(ref _baseUrl, value);
    }
    public string ApiKey
    {
        get => _apiKey;
        set => Set(ref _apiKey, value);
    }
    public string Model
    {
        get => _model;
        set => Set(ref _model, value);
    }
    public int MaxTokens
    {
        get => _maxTokens;
        set => Set(ref _maxTokens, value);
    }
    public string SelectedVariant
    {
        get => _selectedVariant;
        set => Set(ref _selectedVariant, value);
    }
    public bool AutoDownload
    {
        get => _autoDownload;
        set => Set(ref _autoDownload, value);
    }
    public string Device
    {
        get => _device;
        set => Set(ref _device, value);
    }
    public string CacheDir
    {
        get => _cacheDir;
        set => Set(ref _cacheDir, value);
    }
    public string OutputDir
    {
        get => _outputDir;
        set => Set(ref _outputDir, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }
    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            if (Set(ref _isTesting, value))
            {
                TestConnectionCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string SelectedModelSuggestion
    {
        get => _selectedModelSuggestion;
        set
        {
            if (Set(ref _selectedModelSuggestion, value) && !string.IsNullOrWhiteSpace(value))
                Model = value;
        }
    }

    public event Action? OnCancel;
    public event Action<AppSettings>? OnSaved;

    private async Task TestAsync(bool populateOnly = false)
    {
        IsTesting = true;
        Status = "Testing…";
        try
        {
            var client = new ApiClient(BuildApiSettings());
            var models = await client.ListModelsAsync();
            ModelSuggestions.Clear();
            foreach (var m in models)
                ModelSuggestions.Add(m);

            if (models.Count == 0)
            {
                Status = populateOnly
                    ? "Endpoint reachable, but no models returned."
                    : "Connected (no models reported).";
            }
            else
            {
                Status = $"Connected — {models.Count} model(s) available.";
                if (string.IsNullOrWhiteSpace(Model) || !models.Contains(Model))
                    Model = models[0];
            }
        }
        catch (Exception ex)
        {
            Status = "Connection failed: " + ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void Save()
    {
        var baseUrl = BaseUrl.Trim();
        if (
            !baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            Status = "Base URL must start with http:// or https://.";
            return;
        }
        if (MaxTokens < 1)
        {
            Status = "Max tokens must be at least 1.";
            return;
        }

        _original.Api.BaseUrl = baseUrl.TrimEnd('/');
        _original.Api.ApiKey = ApiKey.Trim();
        _original.Api.Model = Model.Trim();
        _original.Api.MaxTokens = MaxTokens;
        _original.Stt.Variant = SelectedVariant;
        _original.Stt.AutoDownload = AutoDownload;
        _original.Stt.Device = Device.Trim();
        _original.Stt.CacheDir = CacheDir.Trim();
        _original.OutputDir = OutputDir.Trim();

        _settingsService.Save(_original);
        OnSaved?.Invoke(_original);
    }

    private ApiSettings BuildApiSettings() =>
        new()
        {
            BaseUrl = BaseUrl.Trim(),
            ApiKey = ApiKey.Trim(),
            Model = Model.Trim(),
            MaxTokens = MaxTokens,
        };
}
