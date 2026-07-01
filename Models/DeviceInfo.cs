using System.Collections.Generic;

namespace LaserConsole.Models;

public enum ComponentStatus { Unknown, Ok, Warning, Error }

// Plain data returned by LaserService.EnumerateDevicesAsync — no Avalonia deps.
public sealed class DeviceInfo
{
    public string Name     { get; init; } = "";
    public string Role     { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string KeyHex   { get; init; } = "";

    public ComponentStatus            Status     { get; init; } = ComponentStatus.Unknown;
    public Dictionary<string, string> Attributes { get; init; } = new();
    public List<DeviceInfo>           Children   { get; init; } = new();
}
