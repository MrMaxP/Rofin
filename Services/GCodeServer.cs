using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LaserConsole.Services;

// Parsed representation of one GCode line.
public sealed class GCodeLine
{
    public string Raw    { get; init; } = "";
    public int?   G      { get; init; }
    public int?   M      { get; init; }
    public float? X      { get; init; }
    public float? Y      { get; init; }
    public float? Z      { get; init; }
    public float? F      { get; init; }
    public float? S      { get; init; }
    public bool   IsJog  => G is 0 or 1 or 2 or 3;
    public bool   IsEmpty { get; init; }
}

// Minimal GRBL-compatible TCP GCode server.
//
// One client is accepted at a time on the configured port.  Each line from
// the client is parsed, handed to the caller via the handler delegate, and
// then acknowledged with "ok\r\n" (or "error:X\r\n" on a parse failure that
// should abort the job).
//
// Lightburn sends commands one at a time and waits for "ok" before the next,
// so the handler can safely call blocking LaserService methods without
// buffering complexity.
public sealed class GCodeServer : IDisposable
{
    // Fired on the background accept/read thread — callers must marshal to UI.
    public event Action<string>? StatusMessage;

    public bool   IsRunning { get; private set; }
    public int    Port      { get; private set; }

    TcpListener?          _listener;
    CancellationTokenSource? _cts;
    Task?                 _serverTask;

    static readonly Regex _wordRe = new(@"([A-Za-z])([-+]?\d*\.?\d+)", RegexOptions.Compiled);

    // Start listening.  handler(line) is called for every parsed non-empty line.
    // Returns immediately; the listener runs on a background task.
    public void Start(int port, Func<GCodeLine, CancellationToken, Task> handler)
    {
        if (IsRunning) return;
        Port = port;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsRunning = true;
        Status($"GCode server listening on port {port}");
        _serverTask = Task.Run(() => AcceptLoop(handler, ct), ct);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _listener?.Stop();
        IsRunning = false;
        Status("GCode server stopped.");
    }

    async Task AcceptLoop(Func<GCodeLine, CancellationToken, Task> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Status($"Accept error: {ex.Message}");
                break;
            }

            Status("GCode client connected.");
            try   { await HandleClient(client, handler, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Status($"Client error: {ex.Message}"); }
            finally { client.Dispose(); Status("GCode client disconnected."); }
        }
    }

    async Task HandleClient(TcpClient client, Func<GCodeLine, CancellationToken, Task> handler, CancellationToken ct)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        // GRBL greeting — Lightburn uses this to detect a connected machine
        await writer.WriteLineAsync("Grbl 1.1h ['$' for help]");

        while (!ct.IsCancellationRequested)
        {
            string? raw;
            try { raw = await reader.ReadLineAsync(ct); }
            catch { break; }
            if (raw is null) break;

            // Strip comments and whitespace
            var line = raw.Split(';')[0].Trim();

            // Status query — respond inline without calling handler
            if (line == "?")
            {
                await writer.WriteLineAsync("<Idle|WPos:0.000,0.000,0.000|FS:0,0>");
                continue;
            }

            // Settings / reset queries — acknowledge silently
            if (line.StartsWith('$') || line == "\x18" || line.Length == 0)
            {
                if (line.StartsWith("$$"))
                    await writer.WriteLineAsync("ok"); // settings list omitted
                else
                    await writer.WriteLineAsync("ok");
                continue;
            }

            var parsed = Parse(raw);

            try
            {
                Status($">> {raw.Trim()}");
                await handler(parsed, ct);
                await writer.WriteLineAsync("ok");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Status($"   handler error: {ex.Message}");
                await writer.WriteLineAsync("error:1");
            }
        }
    }

    // Parse a single GCode line into a GCodeLine.
    public static GCodeLine Parse(string raw)
    {
        var stripped = raw.Split(';')[0].Trim().ToUpperInvariant();
        if (stripped.Length == 0)
            return new GCodeLine { Raw = raw, IsEmpty = true };

        int? g = null, m = null;
        float? x = null, y = null, z = null, f = null, s = null;

        foreach (Match match in _wordRe.Matches(stripped))
        {
            var letter = match.Groups[1].Value[0];
            if (!float.TryParse(match.Groups[2].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float num))
                continue;

            switch (letter)
            {
                case 'G': g = (int)num; break;
                case 'M': m = (int)num; break;
                case 'X': x = num;      break;
                case 'Y': y = num;      break;
                case 'Z': z = num;      break;
                case 'F': f = num;      break;
                case 'S': s = num;      break;
            }
        }

        return new GCodeLine { Raw = raw, G = g, M = m, X = x, Y = y, Z = z, F = f, S = s };
    }

    void Status(string msg) => StatusMessage?.Invoke(msg);

    public void Dispose() => Stop();
}
