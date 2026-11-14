namespace BottomScreen;

internal sealed record DisplayInfo(string DeviceName, string MonitorDeviceId, bool Primary);

internal static class DisplaySelection
{
    internal static int Resolve(IReadOnlyList<DisplayInfo> displays, string? monitorDeviceId, string? deviceName)
    {
        if (displays.Count == 0) return -1;
        if (!string.IsNullOrWhiteSpace(monitorDeviceId))
        {
            for (int i = 0; i < displays.Count; i++)
                if (string.Equals(displays[i].MonitorDeviceId, monitorDeviceId, StringComparison.OrdinalIgnoreCase)) return i;
            // A saved monitor identity that is no longer connected must not fall back to
            // \\.\DISPLAYn: Windows may have reassigned that ordinal to another display.
            // Only use the name when this system exposes no interface identity at all.
            bool hasIdentity = false;
            for (int i = 0; i < displays.Count; i++) if (!string.IsNullOrWhiteSpace(displays[i].MonitorDeviceId)) { hasIdentity = true; break; }
            if (hasIdentity) return PrimaryOrFirst(displays);
        }
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            for (int i = 0; i < displays.Count; i++)
                if (string.Equals(displays[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return PrimaryOrFirst(displays);
    }

    private static int PrimaryOrFirst(IReadOnlyList<DisplayInfo> displays)
    {
        for (int i = 0; i < displays.Count; i++) if (displays[i].Primary) return i;
        return 0;
    }
}
