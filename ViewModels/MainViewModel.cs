using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Qadopoolminer.Infrastructure;
using Qadopoolminer.Infrastructure.Commands;
using Qadopoolminer.Models;
using Qadopoolminer.Services;
using Qadopoolminer.Services.OpenCl;

namespace Qadopoolminer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly MinerSettingsService _settingsService;
    private readonly MinerKeyService _keyService;
    private readonly PoolApiClient _poolApiClient;
    private readonly MiningEngine _miningEngine;
    private readonly ClipboardService _clipboardService;
    private readonly DialogService _dialogService;
    private readonly ILogSink _log;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _logDrainTimer;
    private readonly ConcurrentQueue<LogEntry> _pendingLogEntries = new();
    private readonly Dictionary<string, OpenClMiningDevice> _discoveredDevices = new(StringComparer.Ordinal);

    private string _storedPrivateKeyHex = "";
    private string _storedMinerToken = "";
    private bool _initialized;
    private bool _statsRefreshInFlight;
    private string _poolUrl = "";
    private string _poolConnectionStatus = "Disconnected";
    private string _privateKeyInput = "";
    private string _currentPublicKey = "";
    private string _minerTokenInput = "";
    private string _challengeMessageInput = "";
    private string _manualSignatureHex = "";
    private string _workerThreadsText = "1";
    private bool _isMining;
    private string _currentJobId = "";
    private string _currentHeight = "";
    private string _currentJobTimestamp = "";
    private string _currentShareTarget = "";
    private string _currentNetworkTarget = "";
    private string _shareDifficultyText = "";
    private string _localHashrateText = "0.00 H/s";
    private string _poolEstimatedHashrateText = "";
    private int _acceptedShares;
    private int _staleShares;
    private int _invalidShares;
    private int _duplicateShares;
    private int _blockCandidates;
    private string _poolRoundAcceptedText = "";
    private string _poolRoundStaleText = "";
    private string _poolRoundInvalidText = "";
    private string _lastShareUtcText = "";
    private string _lastError = "";

    public MainViewModel(
        MinerSettingsService settingsService,
        MinerKeyService keyService,
        PoolApiClient poolApiClient,
        MiningEngine miningEngine,
        ClipboardService clipboardService,
        DialogService dialogService,
        ILogSink log)
    {
        _settingsService = settingsService;
        _keyService = keyService;
        _poolApiClient = poolApiClient;
        _miningEngine = miningEngine;
        _clipboardService = clipboardService;
        _dialogService = dialogService;
        _log = log;

        Logs = new ObservableCollection<LogEntry>();
        OpenClDevices = new ObservableCollection<OpenClDeviceItemViewModel>();

        foreach (var entry in _log.GetSnapshot())
        {
            Logs.Add(entry);
        }

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !string.IsNullOrWhiteSpace(PoolUrl));
        GenerateKeyCommand = new RelayCommand(GenerateKey);
        AcceptKeyCommand = new AsyncRelayCommand(AcceptKeyAsync, () => !string.IsNullOrWhiteSpace(PrivateKeyInput));
        DeleteKeyCommand = new AsyncRelayCommand(DeleteKeyAsync, () => !string.IsNullOrWhiteSpace(_storedPrivateKeyHex));
        CopyPublicKeyCommand = new RelayCommand(CopyPublicKey, () => !string.IsNullOrWhiteSpace(CurrentPublicKey));
        AcceptTokenCommand = new AsyncRelayCommand(AcceptTokenAsync, () => !string.IsNullOrWhiteSpace(MinerTokenInput) && !string.IsNullOrWhiteSpace(PoolUrl));
        DeleteTokenCommand = new AsyncRelayCommand(DeleteTokenAsync, () => !string.IsNullOrWhiteSpace(_storedMinerToken));
        CopyTokenCommand = new RelayCommand(CopyToken, () => !string.IsNullOrWhiteSpace(_storedMinerToken));
        SignChallengeCommand = new AsyncRelayCommand(SignChallengeAsync, CanSignChallenge);
        CopySignatureCommand = new RelayCommand(CopySignature, () => !string.IsNullOrWhiteSpace(ManualSignatureHex));
        ClearSignatureCommand = new RelayCommand(ClearSignature);
        LoadJobCommand = new AsyncRelayCommand(LoadJobAsync, CanUseMinerToken);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, allowConcurrentExecution: false);
        StartMiningCommand = new AsyncRelayCommand(StartMiningAsync, CanStartMining);
        StopMiningCommand = new AsyncRelayCommand(StopMiningAsync, () => IsMining);

        _log.EntryAdded += HandleLogEntryAdded;
        _miningEngine.SnapshotChanged += HandleMiningSnapshotChanged;

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _statsTimer.Tick += StatsTimerTick;

        _logDrainTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _logDrainTimer.Tick += LogDrainTimerTick;
    }

    public ObservableCollection<LogEntry> Logs { get; }

    public ObservableCollection<OpenClDeviceItemViewModel> OpenClDevices { get; }

    public AsyncRelayCommand TestConnectionCommand { get; }

    public RelayCommand GenerateKeyCommand { get; }

    public AsyncRelayCommand AcceptKeyCommand { get; }

    public AsyncRelayCommand DeleteKeyCommand { get; }

    public RelayCommand CopyPublicKeyCommand { get; }

    public AsyncRelayCommand AcceptTokenCommand { get; }

    public AsyncRelayCommand DeleteTokenCommand { get; }

    public RelayCommand CopyTokenCommand { get; }

    public AsyncRelayCommand SignChallengeCommand { get; }

    public RelayCommand CopySignatureCommand { get; }

    public RelayCommand ClearSignatureCommand { get; }

    public AsyncRelayCommand LoadJobCommand { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public AsyncRelayCommand StartMiningCommand { get; }

    public AsyncRelayCommand StopMiningCommand { get; }

    public string PoolUrl
    {
        get => _poolUrl;
        set => SetProperty(ref _poolUrl, value, RefreshCommandStates);
    }

    public string PoolConnectionStatus
    {
        get => _poolConnectionStatus;
        private set => SetProperty(ref _poolConnectionStatus, value);
    }

    public string PrivateKeyInput
    {
        get => _privateKeyInput;
        set => SetProperty(ref _privateKeyInput, value, RefreshCommandStates);
    }

    public string CurrentPublicKey
    {
        get => _currentPublicKey;
        private set => SetProperty(ref _currentPublicKey, value, RefreshCommandStates);
    }

    public string MinerTokenInput
    {
        get => _minerTokenInput;
        set => SetProperty(ref _minerTokenInput, value, RefreshCommandStates);
    }

    public string ChallengeMessageInput
    {
        get => _challengeMessageInput;
        set => SetProperty(ref _challengeMessageInput, value, () =>
        {
            ManualSignatureHex = "";
            RefreshCommandStates();
        });
    }

    public string ManualSignatureHex
    {
        get => _manualSignatureHex;
        private set => SetProperty(ref _manualSignatureHex, value, RefreshCommandStates);
    }

    public string WorkerThreadsText
    {
        get => _workerThreadsText;
        set => SetProperty(ref _workerThreadsText, value, RefreshCommandStates);
    }

    public bool IsMining
    {
        get => _isMining;
        private set => SetProperty(ref _isMining, value, RefreshCommandStates);
    }

    public string CurrentJobId
    {
        get => _currentJobId;
        private set => SetProperty(ref _currentJobId, value);
    }

    public string CurrentHeight
    {
        get => _currentHeight;
        private set => SetProperty(ref _currentHeight, value);
    }

    public string CurrentJobTimestamp
    {
        get => _currentJobTimestamp;
        private set => SetProperty(ref _currentJobTimestamp, value);
    }

    public string CurrentShareTarget
    {
        get => _currentShareTarget;
        private set => SetProperty(ref _currentShareTarget, value);
    }

    public string CurrentNetworkTarget
    {
        get => _currentNetworkTarget;
        private set => SetProperty(ref _currentNetworkTarget, value);
    }

    public string ShareDifficultyText
    {
        get => _shareDifficultyText;
        private set => SetProperty(ref _shareDifficultyText, value);
    }

    public string LocalHashrateText
    {
        get => _localHashrateText;
        private set => SetProperty(ref _localHashrateText, value);
    }

    public string PoolEstimatedHashrateText
    {
        get => _poolEstimatedHashrateText;
        private set => SetProperty(ref _poolEstimatedHashrateText, value);
    }

    public int AcceptedShares
    {
        get => _acceptedShares;
        private set => SetProperty(ref _acceptedShares, value, () => OnPropertyChanged(nameof(ShareCountersText)));
    }

    public int StaleShares
    {
        get => _staleShares;
        private set => SetProperty(ref _staleShares, value, () => OnPropertyChanged(nameof(ShareCountersText)));
    }

    public int InvalidShares
    {
        get => _invalidShares;
        private set => SetProperty(ref _invalidShares, value, () => OnPropertyChanged(nameof(ShareCountersText)));
    }

    public int DuplicateShares
    {
        get => _duplicateShares;
        private set => SetProperty(ref _duplicateShares, value, () => OnPropertyChanged(nameof(ShareCountersText)));
    }

    public int BlockCandidates
    {
        get => _blockCandidates;
        private set => SetProperty(ref _blockCandidates, value);
    }

    public string PoolRoundAcceptedText
    {
        get => _poolRoundAcceptedText;
        private set => SetProperty(ref _poolRoundAcceptedText, value, () => OnPropertyChanged(nameof(PoolRoundSummaryText)));
    }

    public string PoolRoundStaleText
    {
        get => _poolRoundStaleText;
        private set => SetProperty(ref _poolRoundStaleText, value, () => OnPropertyChanged(nameof(PoolRoundSummaryText)));
    }

    public string PoolRoundInvalidText
    {
        get => _poolRoundInvalidText;
        private set => SetProperty(ref _poolRoundInvalidText, value, () => OnPropertyChanged(nameof(PoolRoundSummaryText)));
    }

    public string LastShareUtcText
    {
        get => _lastShareUtcText;
        private set => SetProperty(ref _lastShareUtcText, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value, () => OnPropertyChanged(nameof(LastErrorDisplay)));
    }

    public string MinerTokenStatus => string.IsNullOrWhiteSpace(_storedMinerToken) ? "Not present" : "Present";

    public string StoredKeyStatus => string.IsNullOrWhiteSpace(_storedPrivateKeyHex) ? "Not stored" : "Stored";

    public string SelectedDeviceSummary => $"{OpenClDevices.Count(item => item.Selected)} of {OpenClDevices.Count} selected";

    public string ShareCountersText => $"{AcceptedShares} / {StaleShares} / {InvalidShares} / {DuplicateShares}";

    public string PoolRoundSummaryText => $"{DisplayOrZero(PoolRoundAcceptedText)} / {DisplayOrZero(PoolRoundStaleText)} / {DisplayOrZero(PoolRoundInvalidText)}";

    public string LastErrorDisplay => string.IsNullOrWhiteSpace(LastError) ? "-" : LastError;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        var settings = await _settingsService.LoadAsync().ConfigureAwait(true);

        PoolUrl = settings.PoolUrl ?? "";
        _storedPrivateKeyHex = settings.PrivateKeyHex?.Trim() ?? "";
        _storedMinerToken = settings.MinerToken?.Trim() ?? "";
        PrivateKeyInput = _storedPrivateKeyHex;
        MinerTokenInput = _storedMinerToken;
        WorkerThreadsText = settings.WorkerThreads > 0 ? settings.WorkerThreads.ToString(CultureInfo.InvariantCulture) : "1";

        if (!string.IsNullOrWhiteSpace(_storedPrivateKeyHex))
        {
            try
            {
                CurrentPublicKey = _keyService.DerivePublicKey(_storedPrivateKeyHex);
            }
            catch (Exception ex)
            {
                _storedPrivateKeyHex = "";
                PrivateKeyInput = "";
                _log.Warn("Auth", $"Stored private key could not be loaded: {ex.Message}");
            }
        }
        else
        {
            CurrentPublicKey = settings.PublicKeyHex ?? "";
        }

        await RefreshDevicesAsync().ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(PoolUrl))
        {
            try
            {
                _poolApiClient.SetPoolUrl(PoolUrl);
            }
            catch (Exception ex)
            {
                PoolConnectionStatus = "Disconnected";
                LastError = ex.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(_storedMinerToken) && !string.IsNullOrWhiteSpace(PoolUrl))
        {
            try
            {
                await RefreshMinerStatsAsync(showSuccess: false, persistAfterRefresh: false).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Warn("Pool", $"Stored miner token could not be refreshed: {ex.Message}");
            }
        }

        _statsTimer.Start();
        _initialized = true;
        RefreshCommandStates();
    }

    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();
        _statsTimer.Tick -= StatsTimerTick;
        _logDrainTimer.Stop();
        _logDrainTimer.Tick -= LogDrainTimerTick;
        _log.EntryAdded -= HandleLogEntryAdded;
        _miningEngine.SnapshotChanged -= HandleMiningSnapshotChanged;
        await _miningEngine.StopAsync().ConfigureAwait(true);
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            EnsurePoolConfigured();
            var health = await _poolApiClient.GetHealthAsync().ConfigureAwait(true);
            PoolConnectionStatus = $"Connected ({health.Status})";
            LastError = "";
            _log.Info("Pool", $"Connected to {PoolUrl}.");
            await PersistSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PoolConnectionStatus = "Disconnected";
            LastError = ex.Message;
            _log.Warn("Pool", ex.Message);
            throw;
        }
    }

    private void GenerateKey()
    {
        var keyPair = _keyService.GenerateEd25519KeyPair();
        PrivateKeyInput = keyPair.PrivateKeyHex;
        _log.Info("Auth", "Generated a new Ed25519 miner private key.");
    }

    private async Task AcceptKeyAsync()
    {
        var (_, publicKey) = await StorePrivateKeyFromInputAsync(clearTokenOnChange: true).ConfigureAwait(true);
        _log.Info("Auth", $"Stored miner key. Public key: {publicKey}");
    }

    private async Task DeleteKeyAsync()
    {
        if (!_dialogService.Confirm("Delete Miner Key", "Delete the locally stored private key?"))
        {
            return;
        }

        _storedPrivateKeyHex = "";
        PrivateKeyInput = "";
        if (string.IsNullOrWhiteSpace(_storedMinerToken))
        {
            CurrentPublicKey = "";
            ShareDifficultyText = "";
        }

        await PersistSettingsAsync().ConfigureAwait(true);
        RefreshDerivedProperties();
        _log.Info("Auth", "Deleted the locally stored private key.");
    }

    private void CopyPublicKey()
    {
        if (string.IsNullOrWhiteSpace(CurrentPublicKey))
        {
            return;
        }

        _clipboardService.SetText(CurrentPublicKey);
        _log.Info("UI", "Copied public key to clipboard.");
    }

    private async Task AcceptTokenAsync()
    {
        try
        {
            EnsurePoolConfigured();
            var token = MinerTokenInput.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Enter a miner token first.");
            }

            var stats = await _poolApiClient.GetMinerStatsAsync(token).ConfigureAwait(true);
            _storedMinerToken = token;
            MinerTokenInput = token;
            ApplyMinerStats(stats);
            PoolConnectionStatus = "Connected";
            LastError = "";
            await PersistSettingsAsync().ConfigureAwait(true);
            RefreshDerivedProperties();
            _log.Info("Auth", "Accepted and validated the miner token.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Warn("Auth", ex.Message);
            throw;
        }
    }

    private async Task DeleteTokenAsync()
    {
        if (_miningEngine.IsRunning)
        {
            await _miningEngine.StopAsync().ConfigureAwait(true);
        }

        _storedMinerToken = "";
        MinerTokenInput = "";
        ShareDifficultyText = "";
        PoolEstimatedHashrateText = "";
        PoolRoundAcceptedText = "";
        PoolRoundStaleText = "";
        PoolRoundInvalidText = "";
        LastShareUtcText = "";

        if (string.IsNullOrWhiteSpace(_storedPrivateKeyHex))
        {
            CurrentPublicKey = "";
        }

        await PersistSettingsAsync().ConfigureAwait(true);
        RefreshDerivedProperties();
        _log.Info("Auth", "Deleted the stored miner token.");
    }

    private void CopyToken()
    {
        if (string.IsNullOrWhiteSpace(_storedMinerToken))
        {
            return;
        }

        _clipboardService.SetText(_storedMinerToken);
        _log.Info("UI", "Copied miner token to clipboard.");
    }

    private async Task SignChallengeAsync()
    {
        var (privateKeyHex, publicKeyHex) = await StorePrivateKeyFromInputAsync(clearTokenOnChange: false).ConfigureAwait(true);
        var message = ChallengeMessageInput.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Paste a challenge string first.");
        }

        ManualSignatureHex = _keyService.SignMessage(privateKeyHex, message);
        CurrentPublicKey = publicKeyHex;
        _log.Info("Auth", "Signed the pasted challenge string locally.");
    }

    private void CopySignature()
    {
        if (string.IsNullOrWhiteSpace(ManualSignatureHex))
        {
            return;
        }

        _clipboardService.SetText(ManualSignatureHex);
        _log.Info("UI", "Copied signature to clipboard.");
    }

    private void ClearSignature()
    {
        ChallengeMessageInput = "";
        ManualSignatureHex = "";
    }

    private async Task LoadJobAsync()
    {
        EnsurePoolConfigured();
        EnsureMinerTokenAvailable();

        var response = await _poolApiClient.GetMiningJobAsync(_storedMinerToken).ConfigureAwait(true);
        var job = PoolMiningJob.FromApiResponse(response);
        ApplyJob(job);
        PoolConnectionStatus = "Connected";
        LastError = "";
        _log.Info("Mining", $"Loaded job {job.JobId} at height {job.Height}.");
    }

    private async Task RefreshDevicesAsync()
    {
        var previouslySelectedIds = OpenClDevices.Where(item => item.Selected).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        if (previouslySelectedIds.Count == 0)
        {
            var settings = _initialized ? null : await _settingsService.LoadAsync().ConfigureAwait(true);
            foreach (var selectedId in settings?.SelectedDeviceIds ?? [])
            {
                previouslySelectedIds.Add(selectedId);
            }
        }

        var discovered = OpenClDiscovery.DiscoverDevices(_log);
        _discoveredDevices.Clear();
        OpenClDevices.Clear();

        var selectAll = previouslySelectedIds.Count == 0;
        foreach (var device in discovered)
        {
            _discoveredDevices[device.Id] = device;
            OpenClDevices.Add(new OpenClDeviceItemViewModel(
                device.Id,
                device.DisplayName,
                device.TypeLabel,
                selectAll || previouslySelectedIds.Contains(device.Id),
                RefreshDerivedProperties));
        }

        if (OpenClDevices.Count == 0)
        {
            _log.Warn("Mining", "No OpenCL devices were detected.");
        }

        if (!int.TryParse(WorkerThreadsText, NumberStyles.None, CultureInfo.InvariantCulture, out var workerCount) || workerCount <= 0)
        {
            WorkerThreadsText = Math.Max(1, OpenClDevices.Count).ToString(CultureInfo.InvariantCulture);
        }

        RefreshDerivedProperties();
    }

    private async Task StartMiningAsync()
    {
        EnsurePoolConfigured();
        EnsureMinerTokenAvailable();

        var devices = OpenClDevices
            .Where(item => item.Selected)
            .Select(item => _discoveredDevices[item.Id])
            .ToArray();

        if (devices.Length == 0)
        {
            throw new InvalidOperationException("Select at least one OpenCL device.");
        }

        if (!int.TryParse(WorkerThreadsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var workerCount) || workerCount <= 0)
        {
            throw new InvalidOperationException("Worker threads must be a positive whole number.");
        }

        var shareDifficulty = ParseShareDifficulty();
        await _miningEngine.StartAsync(_storedMinerToken, devices, workerCount, shareDifficulty).ConfigureAwait(true);
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    private async Task StopMiningAsync()
    {
        await _miningEngine.StopAsync().ConfigureAwait(true);
        ApplyMiningSnapshot(_miningEngine.GetSnapshot());
    }

    private async void StatsTimerTick(object? sender, EventArgs e)
    {
        if (_statsRefreshInFlight || string.IsNullOrWhiteSpace(_storedMinerToken) || string.IsNullOrWhiteSpace(PoolUrl))
        {
            return;
        }

        _statsRefreshInFlight = true;
        try
        {
            await RefreshMinerStatsAsync(showSuccess: false, persistAfterRefresh: false).ConfigureAwait(true);
        }
        catch
        {
        }
        finally
        {
            _statsRefreshInFlight = false;
        }
    }

    private async Task RefreshMinerStatsAsync(bool showSuccess, bool persistAfterRefresh)
    {
        EnsurePoolConfigured();
        EnsureMinerTokenAvailable();

        var stats = await _poolApiClient.GetMinerStatsAsync(_storedMinerToken).ConfigureAwait(true);
        ApplyMinerStats(stats);
        PoolConnectionStatus = "Connected";
        LastError = "";

        if (persistAfterRefresh)
        {
            await PersistSettingsAsync().ConfigureAwait(true);
        }

        if (showSuccess)
        {
            _log.Info("Pool", "Refreshed miner stats.");
        }
    }

    private async Task<(string PrivateKeyHex, string PublicKeyHex)> StorePrivateKeyFromInputAsync(bool clearTokenOnChange)
    {
        if (string.IsNullOrWhiteSpace(PrivateKeyInput))
        {
            if (string.IsNullOrWhiteSpace(_storedPrivateKeyHex))
            {
                throw new InvalidOperationException("Enter or generate a private key first.");
            }

            var existingPublicKey = _keyService.DerivePublicKey(_storedPrivateKeyHex);
            CurrentPublicKey = existingPublicKey;
            return (_storedPrivateKeyHex, existingPublicKey);
        }

        var normalized = _keyService.NormalizePrivateKey(PrivateKeyInput);
        var publicKey = _keyService.DerivePublicKey(normalized);
        var keyChanged = !string.Equals(_storedPrivateKeyHex, normalized, StringComparison.Ordinal);

        _storedPrivateKeyHex = normalized;
        PrivateKeyInput = normalized;
        CurrentPublicKey = publicKey;

        if (clearTokenOnChange && keyChanged && !string.IsNullOrWhiteSpace(_storedMinerToken))
        {
            _storedMinerToken = "";
            MinerTokenInput = "";
            ShareDifficultyText = "";
            PoolEstimatedHashrateText = "";
            PoolRoundAcceptedText = "";
            PoolRoundStaleText = "";
            PoolRoundInvalidText = "";
            LastShareUtcText = "";
            _log.Info("Auth", "Cleared the stored miner token because the miner key changed.");
        }

        await PersistSettingsAsync().ConfigureAwait(true);
        RefreshDerivedProperties();
        return (normalized, publicKey);
    }

    private async Task PersistSettingsAsync()
    {
        var settings = new AppSettings
        {
            PoolUrl = PoolUrl.Trim(),
            PrivateKeyHex = _storedPrivateKeyHex,
            MinerToken = _storedMinerToken,
            PublicKeyHex = CurrentPublicKey,
            SelectedDeviceIds = OpenClDevices.Where(item => item.Selected).Select(item => item.Id).ToArray(),
            WorkerThreads = ParseWorkerThreadsForSettings()
        };

        await _settingsService.SaveAsync(settings).ConfigureAwait(true);
    }

    private int ParseWorkerThreadsForSettings()
    {
        return int.TryParse(WorkerThreadsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var workerCount) && workerCount > 0
            ? workerCount
            : 1;
    }

    private void HandleLogEntryAdded(object? sender, LogEntry entry)
    {
        _pendingLogEntries.Enqueue(entry);
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!_logDrainTimer.IsEnabled)
            {
                _logDrainTimer.Start();
            }
        });
    }

    private void LogDrainTimerTick(object? sender, EventArgs e)
    {
        var addedAny = false;
        while (_pendingLogEntries.TryDequeue(out var entry))
        {
            Logs.Add(entry);
            addedAny = true;
        }

        if (addedAny)
        {
            while (Logs.Count > 500)
            {
                Logs.RemoveAt(0);
            }
        }

        if (_pendingLogEntries.IsEmpty)
        {
            _logDrainTimer.Stop();
        }
    }

    private void HandleMiningSnapshotChanged(object? sender, MiningEngineStatusSnapshot snapshot)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() => ApplyMiningSnapshot(snapshot));
    }

    private void ApplyMiningSnapshot(MiningEngineStatusSnapshot snapshot)
    {
        IsMining = snapshot.IsRunning;
        PoolConnectionStatus = snapshot.PoolConnected ? "Connected" : "Disconnected";
        CurrentJobId = snapshot.CurrentJobId;
        CurrentHeight = snapshot.CurrentHeight;
        CurrentJobTimestamp = snapshot.CurrentTimestamp;
        CurrentShareTarget = snapshot.CurrentShareTarget;
        CurrentNetworkTarget = snapshot.CurrentNetworkTarget;
        LocalHashrateText = HashrateUtility.Format(snapshot.LocalHashrate);
        AcceptedShares = snapshot.AcceptedShares;
        StaleShares = snapshot.StaleShares;
        InvalidShares = snapshot.InvalidShares;
        DuplicateShares = snapshot.DuplicateShares;
        BlockCandidates = snapshot.BlockCandidates;
        LastError = snapshot.LastError;
        RefreshCommandStates();
    }

    private void ApplyJob(PoolMiningJob job)
    {
        CurrentJobId = job.JobId;
        CurrentHeight = job.Height;
        CurrentJobTimestamp = job.BaseTimestampText;
        CurrentShareTarget = job.ShareTargetHex;
        CurrentNetworkTarget = job.NetworkTargetHex;
    }

    private void ApplyMinerStats(MinerStatsResponse stats)
    {
        CurrentPublicKey = stats.PublicKey;
        ShareDifficultyText = stats.ShareDifficulty.ToString("0.00", CultureInfo.InvariantCulture);
        PoolEstimatedHashrateText = double.TryParse(stats.EstimatedHashrate, NumberStyles.Float, CultureInfo.InvariantCulture, out var estimatedHashrate)
            ? HashrateUtility.Format(estimatedHashrate)
            : stats.EstimatedHashrate;
        PoolRoundAcceptedText = stats.AcceptedSharesRound.ToString(CultureInfo.InvariantCulture);
        PoolRoundStaleText = stats.StaleSharesRound.ToString(CultureInfo.InvariantCulture);
        PoolRoundInvalidText = stats.InvalidSharesRound.ToString(CultureInfo.InvariantCulture);
        LastShareUtcText = stats.LastShareUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
    }

    private double ParseShareDifficulty()
    {
        if (double.TryParse(ShareDifficultyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var shareDifficulty) && shareDifficulty > 0d)
        {
            return shareDifficulty;
        }

        throw new InvalidOperationException("Share difficulty is not available yet. Refresh miner stats or verify the miner token first.");
    }

    private void EnsurePoolConfigured()
    {
        if (string.IsNullOrWhiteSpace(PoolUrl))
        {
            throw new InvalidOperationException("Enter a pool URL first.");
        }

        _poolApiClient.SetPoolUrl(PoolUrl);
    }

    private void EnsureMinerTokenAvailable()
    {
        if (string.IsNullOrWhiteSpace(_storedMinerToken))
        {
            throw new InvalidOperationException("A miner token is required.");
        }
    }

    private bool CanSignChallenge()
        => !string.IsNullOrWhiteSpace(ChallengeMessageInput)
            && (!string.IsNullOrWhiteSpace(_storedPrivateKeyHex) || !string.IsNullOrWhiteSpace(PrivateKeyInput));

    private bool CanUseMinerToken()
        => !string.IsNullOrWhiteSpace(PoolUrl) && !string.IsNullOrWhiteSpace(_storedMinerToken);

    private bool CanStartMining()
        => !IsMining
            && !string.IsNullOrWhiteSpace(PoolUrl)
            && !string.IsNullOrWhiteSpace(_storedMinerToken)
            && OpenClDevices.Any(item => item.Selected)
            && int.TryParse(WorkerThreadsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var workerCount)
            && workerCount > 0;

    private void RefreshDerivedProperties()
    {
        OnPropertyChanged(nameof(MinerTokenStatus));
        OnPropertyChanged(nameof(StoredKeyStatus));
        OnPropertyChanged(nameof(SelectedDeviceSummary));
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        AcceptKeyCommand.NotifyCanExecuteChanged();
        DeleteKeyCommand.NotifyCanExecuteChanged();
        CopyPublicKeyCommand.NotifyCanExecuteChanged();
        AcceptTokenCommand.NotifyCanExecuteChanged();
        DeleteTokenCommand.NotifyCanExecuteChanged();
        CopyTokenCommand.NotifyCanExecuteChanged();
        SignChallengeCommand.NotifyCanExecuteChanged();
        CopySignatureCommand.NotifyCanExecuteChanged();
        ClearSignatureCommand.NotifyCanExecuteChanged();
        LoadJobCommand.NotifyCanExecuteChanged();
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        StartMiningCommand.NotifyCanExecuteChanged();
        StopMiningCommand.NotifyCanExecuteChanged();
    }

    private static string DisplayOrZero(string value)
        => string.IsNullOrWhiteSpace(value) ? "0" : value;
}
