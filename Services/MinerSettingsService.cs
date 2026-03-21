using System.IO;
using System.Text.Json;
using Qadopoolminer.Models;

namespace Qadopoolminer.Services;

public sealed class MinerSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogSink _log;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public MinerSettingsService(ILogSink log)
    {
        _log = log;

        SettingsFilePath = Path.Combine(ResolveApplicationDirectory(), "settings.json");
    }

    public string SettingsFilePath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            return await LoadFromPathAsync(SettingsFilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath)
                ?? throw new InvalidOperationException("Settings path is invalid.");
            Directory.CreateDirectory(directory);

            await using var stream = File.Create(SettingsFilePath);
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<AppSettings> LoadFromPathAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return settings ?? new AppSettings();
    }

    private static string ResolveApplicationDirectory()
    {
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
