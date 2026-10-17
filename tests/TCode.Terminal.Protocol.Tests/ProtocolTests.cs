using System.Text;
using System.Text.Json;
using TCode.Terminal.Protocol;
using Xunit;

namespace TCode.Terminal.Protocol.Tests;

/// <summary>帧、信封、DTO、握手与记录编码（04 §16 的"帧编解码""JSON""握手"）。</summary>
public sealed class ProtocolTests
{
    [Fact]
    public void FrameHeaderRoundTrips()
    {
        Span<byte> b = stackalloc byte[12];
        new FrameHeader(100, FrameType.Output, FrameFlags.None, 7).Write(b);
        Assert.Equal(108u, BitConverter.ToUInt32(b));
        Assert.Equal(new FrameHeader(100, FrameType.Output, FrameFlags.None, 7), FrameHeader.Read(b, authenticated: true));
    }

    [Theory]
    [InlineData(4u, 0x01, 0, 0)]              // length < 8
    [InlineData(8u, 0x01, 2, 0)]              // 保留 flag 位
    [InlineData(8u, 0x01, 0, 1)]              // 保留字段
    [InlineData(8u + 20_000, 0x01, 0, 0)]     // 认证前超过 16 KiB
    public void InvalidHeadersAreRejected(uint length, byte type, byte flags, ushort reserved)
    {
        var b = new byte[12];
        BitConverter.TryWriteBytes(b, length);
        b[4] = type;
        b[5] = flags;
        BitConverter.TryWriteBytes(b.AsSpan(6), reserved);
        Assert.Throws<ProtocolException>(() => FrameHeader.Read(b, authenticated: false));
    }

    [Fact]
    public void PayloadLimitsDependOnFrameKind()
    {
        var b = new byte[12];
        new FrameHeader(FrameLimits.MaxDataPayload + 1, FrameType.Output, FrameFlags.None, 1).Write(b);
        Assert.Throws<ProtocolException>(() => FrameHeader.Read(b, authenticated: true));
        new FrameHeader(FrameLimits.MaxControlPayload, FrameType.Request, FrameFlags.None, 0).Write(b);
        Assert.Equal(FrameLimits.MaxControlPayload, FrameHeader.Read(b, authenticated: true).PayloadLength);
    }

    [Fact]
    public void EnvelopeParsesWithoutDocumentAndIgnoresUnknownFields()
    {
        var json = Envelope.Request(5, PtyMethods.SessionResize, new SessionResizeParams(3, 100, 30, 9, 18), PtyHostJsonContext.Default.SessionResizeParams);
        var withExtra = Encoding.UTF8.GetString(json).Replace("{\"id\"", "{\"future\":{\"a\":[1,2]},\"id\"");
        var e = Envelope.Parse(Encoding.UTF8.GetBytes(withExtra));
        Assert.Equal((5u, PtyMethods.SessionResize), (e.Id!.Value, e.Method));
        Assert.Equal(new SessionResizeParams(3, 100, 30, 9, 18), e.Read(PtyHostJsonContext.Default.SessionResizeParams));

        var error = Envelope.Parse(Envelope.ErrorResponse(9, PtyErrors.Forbidden, "no"));
        Assert.Equal(new ErrorDto(PtyErrors.Forbidden, "no"), error.Error);
    }

    [Fact]
    public void DeepJsonIsRejected()
    {
        var deep = "{\"id\":1,\"method\":\"x\",\"params\":" + new string('[', 40) + new string(']', 40) + "}";
        Assert.ThrowsAny<JsonException>(() => Envelope.Parse(Encoding.UTF8.GetBytes(deep)));
    }

    [Fact]
    public void DtosRoundTripThroughSourceGeneratedContext()
    {
        var create = new SessionCreateParams
        {
            Size = new TerminalSizeDto(80, 24, 8, 16),
            Launch = new LaunchDto("/bin/sh", ["-c", "echo hi"]),
            Env = new Dictionary<string, string?> { ["A"] = "1", ["B"] = null },
            Attach = new AttachParams { Snapshot = "vt", ResumeFrom = 42 },
        };
        var json = JsonSerializer.Serialize(create, PtyHostJsonContext.Default.SessionCreateParams);
        var back = JsonSerializer.Deserialize(json, PtyHostJsonContext.Default.SessionCreateParams)!;
        Assert.Equal(create.Launch.Args, back.Launch!.Args);
        Assert.Null(back.Env!["B"]);
        Assert.Equal(42ul, back.Attach!.ResumeFrom);

        var hello = new HelloParams { Protocol = ProtocolVersion.Current, ClientKind = ClientKind.Agent, ClientNonce = [1, 2, 3] };
        var helloJson = JsonSerializer.Serialize(hello, PtyHostJsonContext.Default.HelloParams);
        Assert.Contains("\"clientKind\":\"agent\"", helloJson);
        Assert.Contains("\"clientNonce\":\"AQID\"", helloJson);  // byte[] → base64
    }

    [Fact]
    public void HandshakeProofsAreDirectionalAndKeyed()
    {
        var token = Handshake.NewToken();
        var cn = Handshake.NewNonce();
        var sn = Handshake.NewNonce();
        var server = Handshake.ServerProof(token, cn, sn);
        var client = Handshake.ClientProof(token, sn, cn);
        Assert.NotEqual(server, client);
        Assert.True(Handshake.Verify(server, Handshake.ServerProof(token, cn, sn)));
        Assert.False(Handshake.Verify(server, Handshake.ServerProof(Handshake.NewToken(), cn, sn)));
        Assert.Equal(token, Handshake.FromBase64Url(Handshake.ToBase64Url(token)));
    }

    [Fact]
    public void LogRecordsRoundTrip()
    {
        var palette = new PaletteDto(0xc0c0c0, 0x101010, 0xffcc00, Enumerable.Range(0, 256).Select(i => i * 0x010101).ToArray(), "light");
        foreach (var record in new[]
                 {
                     LogRecord.Resize(120, 40, 9, 18), LogRecord.Reset(true), LogRecord.Clear(true, true), LogRecord.Clear(false, true),
                     LogRecord.SetColors(palette),
                 })
        {
            var payload = new byte[record.PayloadLength];
            record.WritePayload(payload);
            var back = LogRecord.Read(record.Kind, payload);
            if (record.Kind == RecordKind.SetColors)
            {
                Assert.Equal(palette.Palette, back.Palette!.Palette);
                Assert.Equal((palette.Foreground, palette.Background, palette.Cursor, palette.Scheme),
                    (back.Palette.Foreground, back.Palette.Background, back.Palette.Cursor, back.Palette.Scheme));
            }
            else
            {
                Assert.Equal(record, back);
            }
        }
    }
}
