using System.Diagnostics;
using System.Threading.Channels;
using Qadopoolminer.Infrastructure;
using Qadopoolminer.Models;
using Qadopoolminer.Services.OpenCl;

namespace Qadopoolminer.Services;

public sealed class MiningEngine
{
    private static readonly TimeSpan JobRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShareSummaryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReloadDebounceInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan JobLogInterval = TimeSpan.FromSeconds(15);

    private readonly PoolApiClient _poolApiClient;
    private readonly ILogSink _log;
    private readonly object _stateSync = new();

    private CancellationTokenSource? _runCts;
    private Task[]? _runTasks;
    private Channel<PendingShare>? _shareSubmitChannel;
    private string _minerToken = "";
    private PoolMiningJob? _currentJob;
    private long _currentGeneration;
    private long _nextNonceBase;
    private long _completedHashes;
    private long _lastJobReloadUnixMs;
    private long _lastJobLogUnixMs;
    private bool _isRunning;
    private bool _poolConnected;
    private double _localHashrate;
    private int _acceptedShares;
    private int _staleShares;
    private int _invalidShares;
    private int _duplicateShares;
    private int _blockCandidates;
    private int _activeWorkers;
    private int _jobReloadInFlight;
    private string _lastError = "";
    private string _lastLoggedJobHeight = "";

    public MiningEngine(PoolApiClient poolApiClient, ILogSink log)
    {
        _poolApiClient = poolApiClient;
        _log = log;
    }

    private sealed record PendingShare(PoolMiningJob Job, ulong Nonce, ulong Timestamp, byte[] HashBytes);

    public event EventHandler<MiningEngineStatusSnapshot>? SnapshotChanged;

    public bool IsRunning
    {
        get
        {
            lock (_stateSync)
            {
                return _isRunning;
            }
        }
    }

    public MiningEngineStatusSnapshot GetSnapshot()
    {
        lock (_stateSync)
        {
            return BuildSnapshotNoLock();
        }
    }

    public async Task StartAsync(string minerToken, IReadOnlyList<OpenClMiningDevice> devices, int workerCount, double shareDifficulty, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minerToken))
        {
            throw new InvalidOperationException("A miner token is required.");
        }

        if (devices.Count == 0)
        {
            throw new InvalidOperationException("At least one OpenCL device must be selected.");
        }

        if (workerCount <= 0)
        {
            throw new InvalidOperationException("Worker threads must be greater than zero.");
        }

        if (shareDifficulty <= 0d || double.IsNaN(shareDifficulty) || double.IsInfinity(shareDifficulty))
        {
            throw new InvalidOperationException("Share difficulty must be greater than zero.");
        }

        CancellationTokenSource runCts;
        Task[] tasks;
        Channel<PendingShare> shareSubmitChannel;

        lock (_stateSync)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Mining is already running.");
            }

            _minerToken = minerToken.Trim();
            _currentJob = null;
            _currentGeneration = 0;
            _nextNonceBase = 0;
            _completedHashes = 0;
            _lastJobReloadUnixMs = 0;
            _lastJobLogUnixMs = 0;
            _poolConnected = false;
            _localHashrate = 0;
            _acceptedShares = 0;
            _staleShares = 0;
            _invalidShares = 0;
            _duplicateShares = 0;
            _blockCandidates = 0;
            _activeWorkers = 0;
            _jobReloadInFlight = 0;
            _lastError = "";
            _lastLoggedJobHeight = "";
            _shareSubmitChannel = Channel.CreateBounded<PendingShare>(new BoundedChannelOptions(16_384)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            shareSubmitChannel = _shareSubmitChannel;
            _isRunning = true;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCts = _runCts;
        }

        RaiseSnapshotChanged();

        try
        {
            await LoadJobAsync(runCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetDisconnected(ex.Message);
            _log.Warn("Mining", $"Initial job load failed: {ex.Message}");
        }

        var workerAssignments = BuildWorkerAssignments(devices, workerCount);
        var shareSubmitterCount = Math.Clamp(workerAssignments.Count * 2, 2, 8);

        var workerTasks = new List<Task>(workerAssignments.Count + shareSubmitterCount + 2)
        {
            Task.Run(() => JobRefreshLoopAsync(runCts.Token), runCts.Token),
            Task.Run(() => MetricsLoopAsync(runCts.Token), runCts.Token)
        };

        for (var i = 0; i < shareSubmitterCount; i++)
        {
            var submitterIndex = i;
            workerTasks.Add(Task.Run(() => ShareSubmitLoopAsync(shareSubmitChannel.Reader, submitterIndex, runCts.Token), runCts.Token));
        }

        for (var i = 0; i < workerAssignments.Count; i++)
        {
            var device = workerAssignments[i];
            var workerIndex = i;
            workerTasks.Add(Task.Run(() => WorkerLoopAsync(device, workerIndex, runCts.Token), runCts.Token));
        }

        tasks = workerTasks.ToArray();

        lock (_stateSync)
        {
            _runTasks = tasks;
        }

        _log.Info("Mining", $"Started mining with {workerAssignments.Count} worker(s) across {devices.Count} selected OpenCL device(s).");
        _log.Info("Mining", $"Share submission concurrency set to {shareSubmitterCount}.");

        for (var i = 0; i < workerAssignments.Count; i++)
        {
            _log.Info("Mining", $"Worker {i + 1} assigned to {workerAssignments[i].DisplayName}.");
        }

        RaiseSnapshotChanged();
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? runCts;
        Task[]? runTasks;
        Channel<PendingShare>? shareSubmitChannel;

        lock (_stateSync)
        {
            if (!_isRunning)
            {
                return;
            }

            runCts = _runCts;
            runTasks = _runTasks;
            _runCts = null;
            _runTasks = null;
            shareSubmitChannel = _shareSubmitChannel;
            _shareSubmitChannel = null;
            _isRunning = false;
        }

        shareSubmitChannel?.Writer.TryComplete();
        runCts?.Cancel();

        if (runTasks is not null && runTasks.Length > 0)
        {
            try
            {
                var combinedTask = Task.WhenAll(runTasks);
                var completedTask = await Task.WhenAny(combinedTask, Task.Delay(StopTimeout)).ConfigureAwait(false);
                if (completedTask == combinedTask)
                {
                    await combinedTask.ConfigureAwait(false);
                }
                else
                {
                    _log.Warn("Mining", $"Shutdown timed out after {StopTimeout.TotalSeconds:0} seconds; continuing application exit.");
                    _ = combinedTask.ContinueWith(
                        task =>
                        {
                            if (task.Exception is not null)
                            {
                                _log.Warn("Mining", $"A worker completed after shutdown with an error: {task.Exception.GetBaseException().Message}");
                            }
                        },
                        TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Warn("Mining", $"A worker stopped with an error during shutdown: {ex.Message}");
            }
        }

        runCts?.Dispose();

        lock (_stateSync)
        {
            _localHashrate = 0;
            _activeWorkers = 0;
        }

        _log.Info("Mining", "Mining stopped.");
        RaiseSnapshotChanged();
    }

    private async Task JobRefreshLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var shouldRefresh = false;

                lock (_stateSync)
                {
                    shouldRefresh =
                        _currentJob is null ||
                        DateTimeOffset.UtcNow - _currentJob.ReceivedUtc >= JobRefreshInterval;
                }

                if (shouldRefresh)
                {
                    await LoadJobAsync(cancellationToken).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(1);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetDisconnected(ex.Message);
                _log.Warn("Mining", $"Job refresh failed: {ex.Message}");
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2d, 15d));
            }
        }
    }

    private async Task MetricsLoopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var previousHashes = 0L;
        var previousTimestamp = stopwatch.Elapsed;
        var lastSummaryAccepted = 0;
        var lastSummaryStale = 0;
        var lastSummaryInvalid = 0;
        var lastSummaryDuplicate = 0;
        var lastSummaryCandidates = 0;
        var lastSummaryTimestamp = stopwatch.Elapsed;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var totalHashes = Interlocked.Read(ref _completedHashes);
            var now = stopwatch.Elapsed;
            var deltaHashes = totalHashes - previousHashes;
            var deltaSeconds = Math.Max(0.001d, (now - previousTimestamp).TotalSeconds);

            lock (_stateSync)
            {
                _localHashrate = deltaHashes / deltaSeconds;
            }            

            var accepted = _acceptedShares;
            var stale = _staleShares;
            var invalid = _invalidShares;
            var duplicate = _duplicateShares;
            var candidates = _blockCandidates;

            if (now - lastSummaryTimestamp >= ShareSummaryInterval &&
                (accepted != lastSummaryAccepted ||
                 stale != lastSummaryStale ||
                 invalid != lastSummaryInvalid ||
                 duplicate != lastSummaryDuplicate ||
                 candidates != lastSummaryCandidates))
            {
                _log.Info(
                    "Mining",
                    $"Shares A/S/I/D={accepted}/{stale}/{invalid}/{duplicate}, candidates={candidates}, hashrate={HashrateUtility.Format(_localHashrate)}");

                lastSummaryAccepted = accepted;
                lastSummaryStale = stale;
                lastSummaryInvalid = invalid;
                lastSummaryDuplicate = duplicate;
                lastSummaryCandidates = candidates;
                lastSummaryTimestamp = now;
            }

            previousHashes = totalHashes;
            previousTimestamp = now;
            RaiseSnapshotChanged();
        }
    }

    private async Task WorkerLoopAsync(OpenClMiningDevice device, int workerIndex, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeWorkers);
        RaiseSnapshotChanged();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var scanner = new OpenClNonceScanner(device, _log);
                    var foundShares = new List<OpenClFoundShare>(256);
                    var loadedGeneration = -1L;
                    var loadedTimestamp = 0UL;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var (job, generation) = GetJobSnapshot();
                        if (job is null)
                        {
                            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        var effectiveTimestamp = GetEffectiveTimestamp(job);
                        if (generation != loadedGeneration || effectiveTimestamp != loadedTimestamp)
                        {
                            scanner.UploadTemplate(job.BuildZeroNonceHeaderForTimestamp(effectiveTimestamp), job.ShareTargetBytes);
                            loadedGeneration = generation;
                            loadedTimestamp = effectiveTimestamp;
                        }

                        var nonceBase = ReserveNonceRange(scanner.BatchSize);
                        var foundCount = scanner.MineBatch(nonceBase, foundShares);
                        Interlocked.Add(ref _completedHashes, scanner.BatchSize);

                        var currentGeneration = GetCurrentGeneration();
                        if (currentGeneration != loadedGeneration)
                        {
                            foundShares.Clear();
                            continue;
                        }

                        for (var i = 0; i < foundCount; i++)
                        {
                            await QueueShareAsync(job, foundShares[i].Nonce, loadedTimestamp, foundShares[i].HashBytes, cancellationToken).ConfigureAwait(false);
                        }

                        foundShares.Clear();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetDisconnected(ex.Message);
                    _log.Warn("Mining", $"Worker {workerIndex + 1} on {device.DisplayName} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
            RaiseSnapshotChanged();
        }
    }

    private async Task ShareSubmitLoopAsync(ChannelReader<PendingShare> reader, int submitterIndex, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var pendingShare))
                {
                    await HandleFoundShareAsync(pendingShare.Job, pendingShare.Nonce, pendingShare.Timestamp, pendingShare.HashBytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetDisconnected(ex.Message);
            _log.Warn("Mining", $"Share submitter {submitterIndex + 1} failed: {ex.Message}");
        }
    }

    private async ValueTask QueueShareAsync(PoolMiningJob job, ulong nonce, ulong timestamp, byte[] hashBytes, CancellationToken cancellationToken)
    {
        var shareSubmitChannel = _shareSubmitChannel;
        if (shareSubmitChannel is null)
        {
            return;
        }

        try
        {
            await shareSubmitChannel.Writer.WriteAsync(new PendingShare(job, nonce, timestamp, hashBytes), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task HandleFoundShareAsync(PoolMiningJob job, ulong nonce, ulong timestamp, byte[] hashBytes, CancellationToken cancellationToken)
    {
        try
        {
            var currentJob = GetCurrentJob();
            if (currentJob is not null && !string.Equals(currentJob.JobId, job.JobId, StringComparison.Ordinal))
            {
                return;
            }

            var response = await _poolApiClient.SubmitShareAsync(_minerToken, job.JobId, nonce, timestamp, cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                _poolConnected = true;
                _lastError = "";
            }

            if (response.Accepted)
            {
                Interlocked.Increment(ref _acceptedShares);
            }

            if (response.Duplicate)
            {
                Interlocked.Increment(ref _duplicateShares);
            }

            if (response.Stale)
            {
                Interlocked.Increment(ref _staleShares);
            }

            if (!response.Accepted && !response.Duplicate && !response.Stale)
            {
                Interlocked.Increment(ref _invalidShares);
            }

            if (response.BlockCandidate)
            {
                Interlocked.Increment(ref _blockCandidates);
                _log.Info(
                    "Mining",
                    response.BlockAccepted
                        ? $"Block candidate accepted for height={job.Height} hash={response.Hash ?? HexUtility.ToLowerHex(hashBytes)}"
                        : $"Block candidate found for height={job.Height} but node response was {response.Reason ?? "not_accepted"}");
            }

            if (response.ReloadJob && response.ShareDifficulty > 0d)
            {
                _log.Info("Mining", $"Pool requested a fresh job at difficulty {response.ShareDifficulty:0.00}.");
            }

            if (response.ReloadJob || response.Stale || response.BlockCandidate || string.Equals(response.Reason, "stale_job", StringComparison.OrdinalIgnoreCase))
            {
                await RequestJobReloadAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetDisconnected(ex.Message);
            _log.Warn("Mining", $"Share submission failed: {ex.Message}");
        }
    }

    private async Task LoadJobAsync(CancellationToken cancellationToken)
    {
        var response = await _poolApiClient.GetMiningJobAsync(_minerToken, cancellationToken).ConfigureAwait(false);
        var job = PoolMiningJob.FromApiResponse(response);
        var nowUtc = DateTimeOffset.UtcNow;
        var shouldLog = false;

        lock (_stateSync)
        {
            var templateChanged =
                _currentJob is null ||
                !_currentJob.HeaderTemplateZeroNonce.AsSpan().SequenceEqual(job.HeaderTemplateZeroNonce) ||
                !_currentJob.ShareTargetBytes.AsSpan().SequenceEqual(job.ShareTargetBytes) ||
                _currentJob.BaseTimestamp != job.BaseTimestamp;

            _currentJob = job;
            _currentGeneration++;
            _poolConnected = true;
            _lastError = "";

            if (templateChanged)
            {
                _nextNonceBase = 0;
            }

            var lastJobLogUnixMs = _lastJobLogUnixMs;
            if (!string.Equals(_lastLoggedJobHeight, job.Height, StringComparison.Ordinal) ||
                lastJobLogUnixMs == 0 ||
                nowUtc.ToUnixTimeMilliseconds() - lastJobLogUnixMs >= (long)JobLogInterval.TotalMilliseconds)
            {
                _lastLoggedJobHeight = job.Height;
                _lastJobLogUnixMs = nowUtc.ToUnixTimeMilliseconds();
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            _log.Info("Mining", $"Job ready at height {job.Height}.");
        }

        RaiseSnapshotChanged();
    }

    private (PoolMiningJob? Job, long Generation) GetJobSnapshot()
    {
        lock (_stateSync)
        {
            return (_currentJob, _currentGeneration);
        }
    }

    private IReadOnlyList<OpenClMiningDevice> BuildWorkerAssignments(IReadOnlyList<OpenClMiningDevice> devices, int requestedWorkerCount)
    {
        var assignments = new List<OpenClMiningDevice>(Math.Max(requestedWorkerCount, devices.Count));

        for (var i = 0; i < devices.Count; i++)
        {
            assignments.Add(devices[i]);
        }

        for (var i = devices.Count; i < requestedWorkerCount; i++)
        {
            assignments.Add(devices[i % devices.Count]);
        }

        if (requestedWorkerCount < devices.Count)
        {
            _log.Info(
                "Mining",
                $"Worker threads increased from {requestedWorkerCount} to {devices.Count} so every selected OpenCL device can mine.");
        }

        return assignments;
    }

    private ulong ReserveNonceRange(int batchSize)
    {
        var next = Interlocked.Add(ref _nextNonceBase, batchSize);
        var start = next - batchSize;
        return unchecked((ulong)start);
    }

    private async Task RequestJobReloadAsync(PoolMiningJob sourceJob, CancellationToken cancellationToken)
    {
        var currentJob = GetCurrentJob();
        if (currentJob is not null && !string.Equals(currentJob.JobId, sourceJob.JobId, StringComparison.Ordinal))
        {
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastReloadUnixMs = Interlocked.Read(ref _lastJobReloadUnixMs);
        if (lastReloadUnixMs > 0 && nowUnixMs - lastReloadUnixMs < (long)ReloadDebounceInterval.TotalMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _jobReloadInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastJobReloadUnixMs, nowUnixMs);
            await LoadJobAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _jobReloadInFlight, 0);
        }
    }

    private PoolMiningJob? GetCurrentJob()
    {
        lock (_stateSync)
        {
            return _currentJob;
        }
    }

    private long GetCurrentGeneration()
    {
        lock (_stateSync)
        {
            return _currentGeneration;
        }
    }

    private static ulong GetEffectiveTimestamp(PoolMiningJob job)
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Math.Max(now, job.BaseTimestamp);
    }

    private void SetDisconnected(string error)
    {
        lock (_stateSync)
        {
            _poolConnected = false;
            _lastError = error;
        }

        RaiseSnapshotChanged();
    }

    private void RaiseSnapshotChanged()
    {
        var handler = SnapshotChanged;
        if (handler is null)
        {
            return;
        }

        MiningEngineStatusSnapshot snapshot;
        lock (_stateSync)
        {
            snapshot = BuildSnapshotNoLock();
        }

        handler.Invoke(this, snapshot);
    }

    private MiningEngineStatusSnapshot BuildSnapshotNoLock()
    {
        return new MiningEngineStatusSnapshot(
            _isRunning,
            _poolConnected,
            _currentJob?.JobId ?? "",
            _currentJob?.Height ?? "",
            _currentJob?.BaseTimestampText ?? "",
            _currentJob?.ShareTargetHex ?? "",
            _currentJob?.NetworkTargetHex ?? "",
            _localHashrate,
            _acceptedShares,
            _staleShares,
            _invalidShares,
            _duplicateShares,
            _blockCandidates,
            _activeWorkers,
            _lastError);
    }
}
