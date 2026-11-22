using System.Text;
using Xunit;

namespace TILageMonitor.Tests;

/// <summary>
/// CETP-Rahmen nach gemSpec_Kon TIP1-A_4596: Magic CETP, 4-Byte-Länge Big-Endian, UTF-8-XML.
/// Teilreads und mehrere Nachrichten hintereinander dürfen kein Ereignis verschlucken.
/// </summary>
public sealed class ConnectorEventStreamTests
{
    private static readonly string EventXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <evt:Event xmlns:evt="http://ws.gematik.de/conn/EventService/v7.2">
          <evt:Topic>CARD/INSERTED</evt:Topic>
          <evt:Message><evt:Parameter><evt:Key>CtId</evt:Key><evt:Value>CT1</evt:Value></evt:Parameter></evt:Message>
        </evt:Event>
        """;

    [Fact]
    public void Reader_keeps_a_partial_frame_until_the_payload_arrives()
    {
        var frame = ConnectorCetp.FrameXml(EventXml);
        var reader = new ConnectorCetp.Reader();
        reader.Add(frame.AsSpan(0, 6));
        Assert.False(reader.TryDequeueXml(out _));
        Assert.False(reader.Closed);

        reader.Add(frame.AsSpan(6));
        Assert.True(reader.TryDequeueXml(out var xml));
        Assert.Contains("CARD/INSERTED", xml);
        Assert.False(reader.TryDequeueXml(out _));
    }

    [Fact]
    public void Reader_splits_payload_across_many_small_reads()
    {
        var frame = ConnectorCetp.FrameXml(EventXml);
        var reader = new ConnectorCetp.Reader();
        for (var i = 0; i < frame.Length; i++)
        {
            reader.Add(frame.AsSpan(i, 1));
            if (i < frame.Length - 1)
                Assert.False(reader.TryDequeueXml(out _));
        }
        Assert.True(reader.TryDequeueXml(out var xml));
        Assert.Contains("CT1", xml);
    }

    [Fact]
    public void Reader_reads_two_back_to_back_messages_from_one_chunk()
    {
        var first = ConnectorCetp.FrameXml("<Event><Topic>CARD/INSERTED</Topic></Event>");
        var second = ConnectorCetp.FrameXml("<Event><Topic>CARD/REMOVED</Topic></Event>");
        var all = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, all, 0, first.Length);
        Buffer.BlockCopy(second, 0, all, first.Length, second.Length);

        var reader = new ConnectorCetp.Reader();
        reader.Add(all);
        Assert.True(reader.TryDequeueXml(out var a));
        Assert.True(reader.TryDequeueXml(out var b));
        Assert.Contains("CARD/INSERTED", a);
        Assert.Contains("CARD/REMOVED", b);
        Assert.False(reader.TryDequeueXml(out _));
    }

    [Fact]
    public void Reader_closes_on_bad_magic_when_garbage_is_long()
    {
        var reader = new ConnectorCetp.Reader();
        reader.Add(Encoding.ASCII.GetBytes(new string('X', 80)));
        Assert.False(reader.TryDequeueXml(out _));
        Assert.True(reader.Closed);
        Assert.Contains("Magic", reader.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reader_resyncs_when_magic_appears_after_a_short_prefix()
    {
        var frame = ConnectorCetp.FrameXml("<Event><Topic>CT/CONNECTED</Topic></Event>");
        var reader = new ConnectorCetp.Reader();
        var prefixed = new byte[3 + frame.Length];
        prefixed[0] = (byte)'x';
        prefixed[1] = (byte)'y';
        prefixed[2] = (byte)'z';
        Buffer.BlockCopy(frame, 0, prefixed, 3, frame.Length);
        reader.Add(prefixed);
        Assert.True(reader.TryDequeueXml(out var xml));
        Assert.Contains("CT/CONNECTED", xml);
        Assert.False(reader.Closed);
    }

    [Fact]
    public void Reader_closes_on_oversize_length()
    {
        var header = new byte[] { (byte)'C', (byte)'E', (byte)'T', (byte)'P', 0x7F, 0xFF, 0xFF, 0xFF };
        var reader = new ConnectorCetp.Reader();
        reader.Add(header);
        Assert.False(reader.TryDequeueXml(out _));
        Assert.True(reader.Closed);
    }

    [Fact]
    public void Frame_roundtrip_preserves_utf8_payload()
    {
        var xml = "<Event><Topic>CERT/CARD/STATUS</Topic><Note>äöü</Note></Event>";
        var frame = ConnectorCetp.FrameXml(xml);
        Assert.Equal("CETP"u8.ToArray(), frame[..4].ToArray());
        var length = (frame[4] << 24) | (frame[5] << 16) | (frame[6] << 8) | frame[7];
        Assert.Equal(Encoding.UTF8.GetByteCount(xml), length);
        var reader = new ConnectorCetp.Reader();
        reader.Add(frame);
        Assert.True(reader.TryDequeueXml(out var recovered));
        Assert.Equal(xml, recovered);
    }

    [Fact]
    public void ParseEvent_reads_official_topic_from_a_framed_payload()
    {
        var evt = ConnectorProtocol.ParseEvent(EventXml);
        Assert.NotNull(evt);
        Assert.Equal("CARD/INSERTED", evt!.Topic);
        Assert.Equal("CT1", evt.Param("CtId"));
    }
}
