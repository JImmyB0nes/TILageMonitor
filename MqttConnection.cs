using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace TILageMonitor;

/// <summary>Zugang zu einem MQTT-Broker (Home Assistant: meist der Mosquitto-Add-on).</summary>
public sealed record MqttOptions(
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password,
    string ClientId,
    string WillTopic,
    string WillPayload,
    TimeSpan KeepAlive);

/// <summary>
/// Schlanker MQTT-3.1.1-Client: CONNECT mit Last Will, PUBLISH (QoS 0), PINGREQ, DISCONNECT.
/// Mehr braucht die App nicht – sie sendet nur und abonniert nichts. Die Verbindung bleibt
/// offen, damit der Broker beim Wegfall der App (PC aus, Absturz) den Last Will verschickt
/// und Home Assistant die Praxis sofort als "nicht verfügbar" zeigt.
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    private readonly MqttOptions _options;
    private TcpClient? _tcp;
    private Stream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _readLoop;
    private Task? _pingLoop;
    private int _packetId;

    public MqttConnection(MqttOptions options) => _options = options;

    /// <summary>Eingehende Nachricht (Topic, Inhalt) aus einem Abo – auf dem Lesefaden.</summary>
    public event Action<MqttConnection, string, string>? MessageReceived;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null && _readLoop is { IsCompleted: false };

    public async Task ConnectAsync(CancellationToken ct)
    {
        // Verbindungsaufbau, TLS und Anmeldung zusammen hoechstens 20 Sekunden – ein falscher
        // Port (etwa ein Webserver) nimmt die Verbindung an, antwortet aber nie mit CONNACK.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_options.Host, _options.Port, ct);
        Stream stream = _tcp.GetStream();
        if (_options.UseTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _options.Host }, ct);
            stream = ssl;
        }
        _stream = stream;

        await WriteAsync(BuildConnect(_options), ct);
        var (type, body) = await ReadPacketAsync(stream, ct);
        if (type != 2 || body.Length < 2)
            throw new IOException(LocalizationService.Pick("MQTT-Broker hat die Anmeldung nicht bestätigt.", "MQTT broker did not acknowledge the connection."));
        if (body[1] != 0)
            throw new IOException(DescribeConnackCode(body[1]));

        _loopCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(stream, _loopCts.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(_loopCts.Token));
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        WriteAsync(BuildPublish(topic, Encoding.UTF8.GetBytes(payload), retain), ct);

    /// <summary>Abonniert Topics mit QoS 0. Gespeicherte (retained) Nachrichten kommen sofort.</summary>
    public Task SubscribeAsync(IEnumerable<string> topics, CancellationToken ct)
    {
        var id = (ushort)(Interlocked.Increment(ref _packetId) % 65535 + 1);
        return WriteAsync(BuildSubscribe(id, topics), ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_stream is not null)
                await WriteAsync([0xE0, 0x00], CancellationToken.None); // DISCONNECT: kein Last Will
        }
        catch { /* Verbindung schon weg */ }
        _loopCts?.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
    }

    /// <summary>Verbindung hart kappen, ohne DISCONNECT – der Broker verschickt dann den Last Will.</summary>
    public void Abort()
    {
        _loopCts?.Cancel();
        try { _tcp?.Client?.Close(0); } catch { }
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    private async Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("MQTT: nicht verbunden.");
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(packet, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.KeepAlive.TotalSeconds / 2));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await WriteAsync([0xC0, 0x00], ct); // PINGREQ
            }
        }
        catch { /* Ende der Verbindung – der Leser merkt es ebenfalls */ }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        // PINGRESP und SUBACK nur abholen; PUBLISH aus Abos an den Empfaenger weiterreichen.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, body) = await ReadPacketAsync(stream, ct);
                if (type == 3 && TryParsePublish(body, out var topic, out var payload))
                {
                    try { MessageReceived?.Invoke(this, topic, payload); }
                    catch { /* Empfaenger darf den Lesefaden nicht beenden */ }
                }
            }
        }
        catch { /* Verbindung beendet */ }
    }

    /// <summary>PUBLISH mit QoS 0 zerlegen (Abos dieser App sind immer QoS 0).</summary>
    internal static bool TryParsePublish(byte[] body, out string topic, out string payload)
    {
        topic = payload = "";
        if (body.Length < 2)
            return false;
        var length = (body[0] << 8) | body[1];
        if (body.Length < 2 + length)
            return false;
        topic = Encoding.UTF8.GetString(body, 2, length);
        payload = Encoding.UTF8.GetString(body, 2 + length, body.Length - 2 - length);
        return true;
    }

    internal static byte[] BuildSubscribe(ushort packetId, IEnumerable<string> topics)
    {
        var body = new List<byte> { (byte)(packetId >> 8), (byte)(packetId & 0xFF) };
        foreach (var topic in topics)
        {
            AppendString(body, topic);
            body.Add(0); // QoS 0
        }
        return Frame(0x82, body);
    }

    // ------------------------------------------------------------------
    // Pakete
    // ------------------------------------------------------------------

    internal static byte[] BuildConnect(MqttOptions o)
    {
        var body = new List<byte>();
        AppendString(body, "MQTT");
        body.Add(4); // Protokollstufe 3.1.1
        byte flags = 0x02; // Clean Session
        flags |= 0x04 | 0x20; // Will-Flag, Will-Retain, QoS 0
        if (!string.IsNullOrEmpty(o.Username))
            flags |= 0x80;
        if (!string.IsNullOrEmpty(o.Username) && !string.IsNullOrEmpty(o.Password))
            flags |= 0x40;
        body.Add(flags);
        var keepAlive = (ushort)Math.Clamp(o.KeepAlive.TotalSeconds, 10, 65535);
        body.Add((byte)(keepAlive >> 8));
        body.Add((byte)(keepAlive & 0xFF));
        AppendString(body, o.ClientId);
        AppendString(body, o.WillTopic);
        AppendBytes(body, Encoding.UTF8.GetBytes(o.WillPayload));
        if (!string.IsNullOrEmpty(o.Username))
            AppendString(body, o.Username!);
        if (!string.IsNullOrEmpty(o.Username) && !string.IsNullOrEmpty(o.Password))
            AppendString(body, o.Password!);
        return Frame(0x10, body);
    }

    internal static byte[] BuildPublish(string topic, byte[] payload, bool retain)
    {
        var body = new List<byte>(topic.Length + payload.Length + 4);
        AppendString(body, topic);
        body.AddRange(payload);
        return Frame((byte)(0x30 | (retain ? 0x01 : 0x00)), body);
    }

    private static byte[] Frame(byte header, List<byte> body)
    {
        var packet = new List<byte>(body.Count + 5) { header };
        var length = body.Count;
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0)
                digit |= 0x80;
            packet.Add(digit);
        } while (length > 0);
        packet.AddRange(body);
        return packet.ToArray();
    }

    private static void AppendString(List<byte> target, string value) => AppendBytes(target, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(List<byte> target, byte[] value)
    {
        target.Add((byte)(value.Length >> 8));
        target.Add((byte)(value.Length & 0xFF));
        target.AddRange(value);
    }

    private static async Task<(int Type, byte[] Body)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadByteAsync(stream, ct);
        var multiplier = 1;
        var length = 0;
        byte digit;
        do
        {
            digit = await ReadByteAsync(stream, ct);
            length += (digit & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((digit & 0x80) != 0 && multiplier <= 128 * 128 * 128);
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), ct);
            if (n == 0)
                throw new EndOfStreamException();
            read += n;
        }
        return (header >> 4, body);
    }

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        if (await stream.ReadAsync(buffer, ct) == 0)
            throw new EndOfStreamException();
        return buffer[0];
    }

    private static string DescribeConnackCode(byte code) => code switch
    {
        1 => LocalizationService.Pick("MQTT: Protokollversion nicht unterstützt.", "MQTT: protocol version not supported."),
        2 => LocalizationService.Pick("MQTT: Client-ID abgelehnt.", "MQTT: client ID rejected."),
        3 => LocalizationService.Pick("MQTT: Broker nicht verfügbar.", "MQTT: broker unavailable."),
        4 => LocalizationService.Pick("MQTT: Benutzername oder Passwort falsch.", "MQTT: wrong username or password."),
        5 => LocalizationService.Pick("MQTT: nicht berechtigt.", "MQTT: not authorized."),
        _ => LocalizationService.Pick($"MQTT: Anmeldung abgelehnt (Code {code}).", $"MQTT: connection refused (code {code}).")
    };
}
