using System.Text;

namespace TILageMonitor;

/// <summary>
/// Connector Event Transport Protocol nach gemSpec_Kon TIP1-A_4596 / TAB_KON_030:
/// Magic „CETP“, 4-Byte-Länge Big-Endian, danach UTF-8-XML (Umschlag Event).
/// </summary>
public static class ConnectorCetp
{
    public static readonly byte[] Magic = "CETP"u8.ToArray();
    public const int HeaderLength = 8;
    public const int MaxPayloadBytes = 2_000_000;

    public static byte[] Frame(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[HeaderLength + payload.Length];
        Buffer.BlockCopy(Magic, 0, frame, 0, 4);
        frame[4] = (byte)((payload.Length >> 24) & 0xFF);
        frame[5] = (byte)((payload.Length >> 16) & 0xFF);
        frame[6] = (byte)((payload.Length >> 8) & 0xFF);
        frame[7] = (byte)(payload.Length & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, HeaderLength, payload.Length);
        return frame;
    }

    public static byte[] FrameXml(string xml) => Frame(Encoding.UTF8.GetBytes(xml));

    /// <summary>Inkrementeller Leser: verträgt Teilreads, mehrere Rahmen hintereinander, schließt bei irreparablem Header.</summary>
    public sealed class Reader
    {
        private readonly List<byte> _buffer = new();
        public bool Closed { get; private set; }
        public string? Error { get; private set; }

        public void Add(ReadOnlySpan<byte> data)
        {
            if (Closed)
                return;
            foreach (var b in data)
                _buffer.Add(b);
            // Hartes Limit gegen unbegrenztes Wachstum vor einem gültigen Header.
            if (_buffer.Count > MaxPayloadBytes + HeaderLength + 1024)
                Fail("CETP-Puffer überschreitet die zulässige Größe / CETP buffer exceeds the allowed size.");
        }

        public bool TryDequeue(out byte[] payload)
        {
            payload = [];
            if (Closed)
                return false;

            while (true)
            {
                if (_buffer.Count < 4)
                    return false;

                if (!HasMagicAt(0))
                {
                    var at = IndexOfMagic(1);
                    if (at < 0)
                    {
                        // Präfix von CETP am Ende behalten, Rest verwerfen oder schließen.
                        var keep = TrailingMagicPrefixLength();
                        if (_buffer.Count - keep > 64)
                        {
                            Fail("CETP-Magic fehlt – Verbindung wird geschlossen / CETP magic missing, closing.");
                            return false;
                        }
                        if (keep < _buffer.Count)
                            _buffer.RemoveRange(0, _buffer.Count - keep);
                        return false;
                    }
                    _buffer.RemoveRange(0, at);
                    continue;
                }

                if (_buffer.Count < HeaderLength)
                    return false;

                var length = (_buffer[4] << 24) | (_buffer[5] << 16) | (_buffer[6] << 8) | _buffer[7];
                if (length < 0 || length > MaxPayloadBytes)
                {
                    Fail($"CETP-Länge ungültig ({length}) / invalid CETP length.");
                    return false;
                }

                if (_buffer.Count < HeaderLength + length)
                    return false;

                payload = _buffer.GetRange(HeaderLength, length).ToArray();
                _buffer.RemoveRange(0, HeaderLength + length);
                return true;
            }
        }

        public bool TryDequeueXml(out string xml)
        {
            xml = "";
            if (!TryDequeue(out var payload))
                return false;
            xml = Encoding.UTF8.GetString(payload);
            return true;
        }

        private void Fail(string error)
        {
            Closed = true;
            Error = error;
            _buffer.Clear();
        }

        private bool HasMagicAt(int index) =>
            index + 4 <= _buffer.Count &&
            _buffer[index] == Magic[0] &&
            _buffer[index + 1] == Magic[1] &&
            _buffer[index + 2] == Magic[2] &&
            _buffer[index + 3] == Magic[3];

        private int IndexOfMagic(int start)
        {
            for (var i = start; i <= _buffer.Count - 4; i++)
            {
                if (HasMagicAt(i))
                    return i;
            }
            return -1;
        }

        private int TrailingMagicPrefixLength()
        {
            var max = Math.Min(3, _buffer.Count);
            for (var n = max; n >= 1; n--)
            {
                var ok = true;
                for (var i = 0; i < n; i++)
                {
                    if (_buffer[_buffer.Count - n + i] != Magic[i])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return n;
            }
            return 0;
        }
    }
}
