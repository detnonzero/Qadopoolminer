namespace Qadopoolminer.Models;

public sealed record MiningEngineStatusSnapshot(
    bool IsRunning,
    bool PoolConnected,
    string CurrentJobId,
    string CurrentHeight,
    string CurrentTimestamp,
    string CurrentShareTarget,
    string CurrentNetworkTarget,
    double LocalHashrate,
    int AcceptedShares,
    int StaleShares,
    int InvalidShares,
    int DuplicateShares,
    int BlockCandidates,
    int ActiveWorkers,
    string LastError);
