using OpenCL.Net;

namespace Qadopoolminer.Services.OpenCl;

public static class OpenClDiscovery
{
    public static IReadOnlyList<OpenClMiningDevice> DiscoverDevices(ILogSink? log = null)
    {
        try
        {
            ErrorCode error;
            var platforms = Cl.GetPlatformIDs(out error);
            if (error != ErrorCode.Success || platforms == null || platforms.Length == 0)
            {
                return Array.Empty<OpenClMiningDevice>();
            }

            var devices = new List<OpenClMiningDevice>(8);

            for (var platformIndex = 0; platformIndex < platforms.Length; platformIndex++)
            {
                var platform = platforms[platformIndex];
                var platformName = SafeInfoString(() => Cl.GetPlatformInfo(platform, PlatformInfo.Name, out _));
                Device[] rawDevices;

                try
                {
                    rawDevices = Cl.GetDeviceIDs(platform, DeviceType.All, out error);
                }
                catch
                {
                    continue;
                }

                if (error != ErrorCode.Success || rawDevices == null || rawDevices.Length == 0)
                {
                    continue;
                }

                for (var deviceIndex = 0; deviceIndex < rawDevices.Length; deviceIndex++)
                {
                    var device = rawDevices[deviceIndex];
                    var deviceType = SafeDeviceType(device);
                    if (!IsSupportedType(deviceType))
                    {
                        continue;
                    }

                    var vendor = SafeInfoString(() => Cl.GetDeviceInfo(device, DeviceInfo.Vendor, out _));
                    var deviceName = SafeInfoString(() => Cl.GetDeviceInfo(device, DeviceInfo.Name, out _));
                    var id = BuildDeviceId(platformIndex, deviceIndex, platformName, vendor, deviceName);

                    devices.Add(new OpenClMiningDevice(
                        id,
                        platformIndex,
                        deviceIndex,
                        platformName,
                        deviceName,
                        vendor,
                        deviceType,
                        platform,
                        device));
                }
            }

            return devices
                .OrderBy(ScoreDevice)
                .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (DllNotFoundException)
        {
            log?.Warn("Mining", "OpenCL runtime not found; OpenCL mining is unavailable.");
            return Array.Empty<OpenClMiningDevice>();
        }
        catch (Exception ex)
        {
            log?.Warn("Mining", $"OpenCL discovery failed: {ex.Message}");
            return Array.Empty<OpenClMiningDevice>();
        }
    }

    private static bool IsSupportedType(DeviceType type)
        => (type & DeviceType.Gpu) == DeviceType.Gpu
            || (type & DeviceType.Cpu) == DeviceType.Cpu
            || (type & DeviceType.Accelerator) == DeviceType.Accelerator;

    private static string BuildDeviceId(int platformIndex, int deviceIndex, string platformName, string vendor, string deviceName)
        => $"{platformIndex}:{deviceIndex}:{Normalize(platformName)}|{Normalize(vendor)}|{Normalize(deviceName)}";

    private static DeviceType SafeDeviceType(Device device)
    {
        try
        {
            ErrorCode error;
            return Cl.GetDeviceInfo(device, DeviceInfo.Type, out error).CastTo<DeviceType>();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeInfoString(Func<InfoBuffer> getter)
    {
        try
        {
            return getter().ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static int ScoreDevice(OpenClMiningDevice device)
    {
        var vendor = (device.Vendor ?? string.Empty).Trim().ToLowerInvariant();

        if ((device.DeviceType & DeviceType.Gpu) == DeviceType.Gpu && vendor.Contains("intel", StringComparison.Ordinal))
        {
            return 0;
        }

        if ((device.DeviceType & DeviceType.Cpu) == DeviceType.Cpu)
        {
            return 1;
        }

        if ((device.DeviceType & DeviceType.Gpu) == DeviceType.Gpu)
        {
            return 2;
        }

        return 3;
    }
}
