using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Qadopoolminer.Models;

namespace Qadopoolminer.Services;

public sealed class PoolApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly ILogSink _log;
    private HttpClient? _httpClient;

    public PoolApiClient(ILogSink log)
    {
        _log = log;
    }

    public string CurrentPoolUrl { get; private set; } = "";

    public void SetPoolUrl(string poolUrl)
    {
        var normalized = NormalizePoolUrl(poolUrl);
        var normalizedText = normalized.ToString().TrimEnd('/');

        HttpClient? previousClient = null;

        lock (_sync)
        {
            if (_httpClient is not null && string.Equals(CurrentPoolUrl, normalizedText, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var newClient = new HttpClient
            {
                BaseAddress = normalized,
                Timeout = TimeSpan.FromSeconds(20)
            };

            previousClient = _httpClient;
            _httpClient = newClient;
            CurrentPoolUrl = normalizedText;
        }

        previousClient?.Dispose();
    }

    public async Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
        => await SendAsync<HealthResponse>(HttpMethod.Get, "health", cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<PoolJobResponse> GetMiningJobAsync(string minerToken, CancellationToken cancellationToken = default)
    {
        return await SendAsync<PoolJobResponse>(
            HttpMethod.Get,
            "mining/job",
            minerToken: minerToken,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShareSubmitResponse> SubmitShareAsync(string minerToken, string jobId, ulong nonce, ulong timestamp, CancellationToken cancellationToken = default)
    {
        return await SendAsync<ShareSubmitResponse>(
            HttpMethod.Post,
            "mining/submit-share",
            new
            {
                jobId,
                nonce = nonce.ToString(System.Globalization.CultureInfo.InvariantCulture),
                timestamp = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            minerToken: minerToken,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<MinerStatsResponse> GetMinerStatsAsync(string minerToken, CancellationToken cancellationToken = default)
    {
        return await SendAsync<MinerStatsResponse>(
            HttpMethod.Get,
            "miner/stats",
            minerToken: minerToken,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body = null,
        string? bearerToken = null,
        string? minerToken = null,
        CancellationToken cancellationToken = default)
    {
        EnsurePoolUrlConfigured();
        var httpClient = GetConfiguredClient();

        using var request = new HttpRequestMessage(method, relativePath);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (!string.IsNullOrWhiteSpace(minerToken))
        {
            request.Headers.Add("X-Miner-Token", minerToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SerializerOptions);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            _log.Warn("PoolApi", $"{method} {relativePath} failed: {(int)response.StatusCode} {message}");
            throw new InvalidOperationException(message);
        }

        var payload = await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? throw new InvalidOperationException("Pool returned an empty response.");
    }

    private async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("error", out var errorProperty))
            {
                var text = errorProperty.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch
        {
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        return $"Request failed with status {(int)response.StatusCode}.";
    }

    private void EnsurePoolUrlConfigured()
    {
        if (GetConfiguredClient().BaseAddress is null)
        {
            throw new InvalidOperationException("Pool URL is not configured.");
        }
    }

    private HttpClient GetConfiguredClient()
    {
        lock (_sync)
        {
            return _httpClient ?? throw new InvalidOperationException("Pool URL is not configured.");
        }
    }

    public static Uri NormalizePoolUrl(string poolUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolUrl);

        var normalized = poolUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "http://" + normalized;
        }

        normalized = normalized.TrimEnd('/') + "/";
        return new Uri(normalized, UriKind.Absolute);
    }
}
