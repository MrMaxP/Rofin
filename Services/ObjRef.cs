using System;

namespace LaserConsole.Services;

sealed class ObjRef
{
    public string TypeId  { get; init; } = "";
    public string Host    { get; init; } = "";
    public int    Port    { get; init; }
    public byte[] Key     { get; init; } = Array.Empty<byte>();

    public string ShortType =>
        TypeId.Contains('/') ? TypeId[(TypeId.LastIndexOf('/') + 1)..] : TypeId;

    public override string ToString() => $"{ShortType} @ {Host}:{Port}";
}
