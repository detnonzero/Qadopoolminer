namespace Qadopoolminer.Models;

public sealed class AppSettings
{
    public string PoolUrl { get; set; } = "";

    public string MinerToken { get; set; } = "";

    public string[] SelectedDeviceIds { get; set; } = [];

    public int WorkerThreads { get; set; } = 1;
}
