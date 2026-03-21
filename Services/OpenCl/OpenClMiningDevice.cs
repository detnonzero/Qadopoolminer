using OpenCL.Net;

namespace Qadopoolminer.Services.OpenCl;

public sealed class OpenClMiningDevice
{
    internal OpenClMiningDevice(
        string id,
        int platformIndex,
        int deviceIndex,
        string platformName,
        string deviceName,
        string vendor,
        DeviceType deviceType,
        Platform platformHandle,
        Device deviceHandle)
    {
        Id = id;
        PlatformIndex = platformIndex;
        DeviceIndex = deviceIndex;
        PlatformName = platformName;
        DeviceName = deviceName;
        Vendor = vendor;
        DeviceType = deviceType;
        PlatformHandle = platformHandle;
        DeviceHandle = deviceHandle;
    }

    public string Id { get; }

    public int PlatformIndex { get; }

    public int DeviceIndex { get; }

    public string PlatformName { get; }

    public string DeviceName { get; }

    public string Vendor { get; }

    public DeviceType DeviceType { get; }

    public string DisplayName => $"{DeviceName} ({Vendor} | {PlatformName})";

    public string TypeLabel
    {
        get
        {
            if ((DeviceType & DeviceType.Gpu) == DeviceType.Gpu)
            {
                return "GPU";
            }

            if ((DeviceType & DeviceType.Cpu) == DeviceType.Cpu)
            {
                return "CPU";
            }

            if ((DeviceType & DeviceType.Accelerator) == DeviceType.Accelerator)
            {
                return "Accelerator";
            }

            return DeviceType.ToString();
        }
    }

    internal Platform PlatformHandle { get; }

    internal Device DeviceHandle { get; }

    public override string ToString() => $"{DisplayName} [{TypeLabel}]";
}
