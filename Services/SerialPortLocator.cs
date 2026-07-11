using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace LaserConsole.Services;

// Finds the Pico bridge's CDC serial port by USB VID:PID (0x9588:0x9899).
// System.IO.Ports has no cross-platform VID/PID query, so each OS is probed
// with its own mechanism; a manual override always wins.
public static class SerialPortLocator
{
    public static string? Find(int vid, int pid, string? manualOverride, Action<string>? log = null)
    {
        var available = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(manualOverride))
        {
            if (available.Contains(manualOverride)) return manualOverride;
            log?.Invoke($"configured port '{manualOverride}' not present (available: {string.Join(", ", available)})");
            return null;
        }

        if (available.Count == 0) return null;

        try
        {
            if (OperatingSystem.IsWindows())  return FindWindows(vid, pid, available, log);
            if (OperatingSystem.IsLinux())    return FindLinux(vid, pid, available, log);
            if (OperatingSystem.IsMacOS())    return FindMacOs(available, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"port auto-detect failed: {ex.Message}");
        }

        return null;
    }

    // Windows: the VID/PID is baked into the registry enum key; walk it for the port name.
    [SupportedOSPlatform("windows")]
    static string? FindWindows(int vid, int pid, HashSet<string> available, Action<string>? log)
    {
        var vidPid = $"VID_{vid:X4}&PID_{pid:X4}";
        using var usbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbKey is null) return null;

        foreach (var deviceKeyName in usbKey.GetSubKeyNames())
        {
            if (!deviceKeyName.Contains(vidPid, StringComparison.OrdinalIgnoreCase)) continue;
            using var deviceKey = usbKey.OpenSubKey(deviceKeyName);
            if (deviceKey is null) continue;

            foreach (var instanceName in deviceKey.GetSubKeyNames())
            {
                using var paramsKey = deviceKey.OpenSubKey($@"{instanceName}\Device Parameters");
                var portName = paramsKey?.GetValue("PortName") as string;
                if (portName != null && available.Contains(portName)) return portName;
            }
        }
        return null;
    }

    // Linux: /sys/class/tty/<name>/device/../{idVendor,idProduct} for USB CDC ACM ports.
    static string? FindLinux(int vid, int pid, HashSet<string> available, Action<string>? log)
    {
        const string sysTty = "/sys/class/tty";
        if (!Directory.Exists(sysTty)) return null;

        foreach (var dir in Directory.EnumerateDirectories(sysTty))
        {
            var name = Path.GetFileName(dir);
            if (!available.Contains($"/dev/{name}")) continue;

            var idVendorPath  = Path.Combine(dir, "device", "..", "idVendor");
            var idProductPath = Path.Combine(dir, "device", "..", "idProduct");
            if (!File.Exists(idVendorPath) || !File.Exists(idProductPath)) continue;

            if (int.TryParse(File.ReadAllText(idVendorPath).Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v) &&
                int.TryParse(File.ReadAllText(idProductPath).Trim(), System.Globalization.NumberStyles.HexNumber, null, out var p) &&
                v == vid && p == pid)
            {
                return $"/dev/{name}";
            }
        }
        return null;
    }

    // macOS: no VID/PID via System.IO.Ports; fall back to the usbmodem naming
    // convention. If several are plugged in, log the candidates and let the
    // user pin one via the manual override.
    static string? FindMacOs(HashSet<string> available, Action<string>? log)
    {
        var candidates = available.Where(p => Regex.IsMatch(Path.GetFileName(p), @"^cu\.usbmodem", RegexOptions.IgnoreCase)).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
            log?.Invoke($"multiple usbmodem ports found ({string.Join(", ", candidates)}) — set a manual port to disambiguate");
        return null;
    }
}
