using System.Text.Json.Serialization;

namespace Qadopoolminer.Models;

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("service")]
    public string Service { get; init; } = "";

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; }
}

public sealed class PoolJobResponse
{
    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("height")]
    public string Height { get; init; } = "0";

    [JsonPropertyName("prevHash")]
    public string PrevHash { get; init; } = "";

    [JsonPropertyName("networkTarget")]
    public string NetworkTarget { get; init; } = "";

    [JsonPropertyName("shareTarget")]
    public string ShareTarget { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = "0";

    [JsonPropertyName("merkleRoot")]
    public string MerkleRoot { get; init; } = "";

    [JsonPropertyName("coinbaseAmount")]
    public string CoinbaseAmount { get; init; } = "0";

    [JsonPropertyName("txCount")]
    public int TxCount { get; init; }

    [JsonPropertyName("headerHexZeroNonce")]
    public string HeaderHexZeroNonce { get; init; } = "";

    [JsonPropertyName("precomputedCv")]
    public string PrecomputedCv { get; init; } = "";

    [JsonPropertyName("block1Base")]
    public string Block1Base { get; init; } = "";

    [JsonPropertyName("block2")]
    public string Block2 { get; init; } = "";

    [JsonPropertyName("targetWords")]
    public string[] TargetWords { get; init; } = [];
}

public sealed class ShareSubmitResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; init; }

    [JsonPropertyName("stale")]
    public bool Stale { get; init; }

    [JsonPropertyName("blockCandidate")]
    public bool BlockCandidate { get; init; }

    [JsonPropertyName("blockAccepted")]
    public bool BlockAccepted { get; init; }

    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("reloadJob")]
    public bool ReloadJob { get; init; }

    [JsonPropertyName("shareDifficulty")]
    public double ShareDifficulty { get; init; }
}

public sealed class MinerStatsResponse
{
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; init; } = "";

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";

    [JsonPropertyName("shareDifficulty")]
    public double ShareDifficulty { get; init; }

    [JsonPropertyName("acceptedSharesRound")]
    public int AcceptedSharesRound { get; init; }

    [JsonPropertyName("staleSharesRound")]
    public int StaleSharesRound { get; init; }

    [JsonPropertyName("invalidSharesRound")]
    public int InvalidSharesRound { get; init; }

    [JsonPropertyName("roundId")]
    public string RoundId { get; init; } = "0";

    [JsonPropertyName("estimatedHashrate")]
    public string EstimatedHashrate { get; init; } = "";

    [JsonPropertyName("lastShareUtc")]
    public DateTimeOffset? LastShareUtc { get; init; }
}
