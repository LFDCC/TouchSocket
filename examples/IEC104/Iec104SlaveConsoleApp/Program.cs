namespace Iec104SlaveConsoleApp;

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TouchSocket.Core;
using TouchSocket.Sockets;

class Program
{
    static async Task Main(string[] args)
    {
        var service = new TcpService();
        await service.SetupAsync(new TouchSocketConfig()
            .SetListenIPHosts("tcp://0.0.0.0:2404")
            .ConfigureContainer(a => a.AddConsoleLogger())
        );

        var connectionStates = new ConcurrentDictionary<string, Iec104LinkState>();

        service.Connected = (client, e) =>
        {
            connectionStates[client.Id] = new Iec104LinkState();
            return EasyTask.CompletedTask;
        };

        service.Closed = (client, e) =>
        {
            connectionStates.TryRemove(client.Id, out _);
            return EasyTask.CompletedTask;
        };

        service.Received = async (client, e) =>
        {
            var state = connectionStates.GetOrAdd(client.Id, _ => new Iec104LinkState());
            state.Append(e.ByteBlock.Span);

            while (TryParseApdu(state, out var apdu))
            {
                if (apdu.IsUFormat)
                {
                    switch (apdu.UControl)
                    {
                        case Iec104UControl.StartDtAct:
                            await client.SendAsync(Iec104Frames.BuildUFrame(Iec104UControl.StartDtCon));
                            state.Started = true;
                            break;
                        case Iec104UControl.TestFrAct:
                            await client.SendAsync(Iec104Frames.BuildUFrame(Iec104UControl.TestFrCon));
                            break;
                        case Iec104UControl.StopDtAct:
                            await client.SendAsync(Iec104Frames.BuildUFrame(Iec104UControl.StopDtCon));
                            state.Started = false;
                            break;
                    }
                }
                else if (apdu.IsSFormat)
                {
                    // S-frames are only acknowledgements; update NR if needed
                }
                else
                {
                    // I-frame
                    state.Nr = apdu.Nr;
                    var asdu = apdu.Asdu;
                    if (asdu.Length >= 6)
                    {
                        byte typeId = asdu[0];
                        byte vsq = asdu[1];
                        ushort cot = BinaryPrimitives.ReadUInt16LittleEndian(asdu.AsSpan(2, 2));
                        ushort coa = BinaryPrimitives.ReadUInt16LittleEndian(asdu.AsSpan(4, 2));

                        if (typeId == 100) // C_IC_NA_1 General Interrogation
                        {
                            // Respond with ACT_CON
                            var actCon = Iec104Asdu.BuildInterrogation(typeId: 100, cot: 7, coa: coa, qoi: asdu.Length > 9 ? asdu[^1] : (byte)0x14);
                            await client.SendAsync(Iec104Frames.BuildIFrame(state.NextNs(), state.Nr, actCon));

                            // Send one dummy single point information (M_SP_NA_1 = type 1)
                            var sp = Iec104Asdu.BuildSinglePoint(typeId: 1, cot: 20, coa: coa, ioa: 1, value: true);
                            await client.SendAsync(Iec104Frames.BuildIFrame(state.NextNs(), state.Nr, sp));

                            // Send ACT_TERM
                            var actTerm = Iec104Asdu.BuildInterrogation(typeId: 100, cot: 10, coa: coa, qoi: 0x14);
                            await client.SendAsync(Iec104Frames.BuildIFrame(state.NextNs(), state.Nr, actTerm));
                        }
                        else
                        {
                            // Echo with S-frame ack
                            await client.SendAsync(Iec104Frames.BuildSFrame(state.Nr));
                        }
                    }
                }
            }
        };

        await service.StartAsync();
        Console.WriteLine("IEC104 Slave started on 0.0.0.0:2404. Press Ctrl+C to exit.");
        await Task.Delay(-1);
    }

    private static bool TryParseApdu(Iec104LinkState state, out Iec104Apdu apdu)
    {
        apdu = default;
        if (state.Length < 2)
        {
            return false;
        }

        var span = state.AsSpan();
        int startIndex = span.IndexOf((byte)0x68);
        if (startIndex < 0)
        {
            state.Clear();// drop garbage
            return false;
        }

        if (startIndex > 0)
        {
            state.RemovePrefix(startIndex);
            span = state.AsSpan();
        }

        if (span.Length < 2)
        {
            return false;
        }

        byte length = span[1];
        int total = 2 + length;
        if (span.Length < total)
        {
            return false;
        }

        var apci = span.Slice(2, 4);
        var payload = span.Slice(6, length - 4).ToArray();

        if ((apci[0] & 0x01) == 0)
        {
            // I format
            ushort ns2 = BinaryPrimitives.ReadUInt16LittleEndian(apci);
            ushort nr2 = BinaryPrimitives.ReadUInt16LittleEndian(apci.Slice(2, 2));
            apdu = Iec104Apdu.I((ushort)(ns2 / 2), (ushort)(nr2 / 2), payload);
        }
        else if ((apci[0] & 0x03) == 0x01)
        {
            // S format
            ushort nr2 = BinaryPrimitives.ReadUInt16LittleEndian(apci.Slice(2, 2));
            apdu = Iec104Apdu.S((ushort)(nr2 / 2));
        }
        else
        {
            // U format
            apdu = Iec104Apdu.U((Iec104UControl)apci[0]);
        }
        state.RemovePrefix(total);
        return true;
    }
}

readonly struct Iec104Apdu
{
    public readonly bool IsIFormat;
    public readonly bool IsSFormat;
    public readonly bool IsUFormat;
    public readonly ushort Ns;
    public readonly ushort Nr;
    public readonly Iec104UControl UControl;
    public readonly byte[] Asdu;

    private Iec104Apdu(bool i, bool s, bool u, ushort ns, ushort nr, Iec104UControl uCtrl, byte[] asdu)
    {
        IsIFormat = i; IsSFormat = s; IsUFormat = u; Ns = ns; Nr = nr; UControl = uCtrl; Asdu = asdu;
    }

    public static Iec104Apdu I(ushort ns, ushort nr, byte[] asdu) => new(true, false, false, ns, nr, default, asdu);
    public static Iec104Apdu S(ushort nr) => new(false, true, false, 0, nr, default, Array.Empty<byte>());
    public static Iec104Apdu U(Iec104UControl u) => new(false, false, true, 0, 0, u, Array.Empty<byte>());
}

enum Iec104UControl : byte
{
    StartDtAct = 0x07,
    StartDtCon = 0x0B,
    StopDtAct = 0x13,
    StopDtCon = 0x23,
    TestFrAct = 0x43,
    TestFrCon = 0x83
}

static class Iec104Frames
{
    public static byte[] BuildUFrame(Iec104UControl control)
    {
        return new byte[] { 0x68, 0x04, (byte)control, 0x00, 0x00, 0x00 };
    }

    public static byte[] BuildSFrame(ushort nr)
    {
        Span<byte> buf = stackalloc byte[6];
        buf[0] = 0x68; buf[1] = 0x04; buf[2] = 0x01; buf[3] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(4, 2), (ushort)(nr * 2));
        return buf.ToArray();
    }

    public static byte[] BuildIFrame(ushort ns, ushort nr, byte[] asdu)
    {
        int length = 4 + asdu.Length;
        byte[] frame = new byte[2 + length];
        frame[0] = 0x68; frame[1] = (byte)length;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)(ns * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)(nr * 2));
        asdu.AsSpan().CopyTo(frame.AsSpan(6));
        return frame;
    }
}

static class Iec104Asdu
{
    public static byte[] BuildInterrogation(byte typeId, ushort cot, ushort coa, byte qoi)
    {
        // VSQ = 1 object, non-seq
        // IOA = 0, then QOI
        byte[] asdu = new byte[1 + 1 + 2 + 2 + 3 + 1];
        asdu[0] = typeId;
        asdu[1] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(2, 2), cot);
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(4, 2), coa);
        asdu[6] = 0x00; asdu[7] = 0x00; asdu[8] = 0x00; // IOA=0
        asdu[9] = qoi;
        return asdu;
    }

    public static byte[] BuildSinglePoint(byte typeId, ushort cot, ushort coa, int ioa, bool value)
    {
        byte[] asdu = new byte[1 + 1 + 2 + 2 + 3 + 1];
        asdu[0] = typeId; // 1
        asdu[1] = 0x01;   // one object, non-seq
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(2, 2), cot);
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(4, 2), coa);
        asdu[6] = (byte)(ioa & 0xFF);
        asdu[7] = (byte)((ioa >> 8) & 0xFF);
        asdu[8] = (byte)((ioa >> 16) & 0xFF);
        asdu[9] = (byte)(value ? 0x01 : 0x00);
        return asdu;
    }
}

sealed class Iec104LinkState
{
    public ushort Ns { get; set; }
    public ushort Nr { get; set; }
    public bool Started { get; set; }
    private readonly List<byte> m_buffer = new List<byte>(4096);

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var arr = data.ToArray();
        m_buffer.AddRange(arr);
    }

    public int Length => m_buffer.Count;
    public void Clear() => m_buffer.Clear();
    public void RemovePrefix(int count)
    {
        if (count <= 0) return;
        if (count >= m_buffer.Count)
        {
            m_buffer.Clear();
            return;
        }
        m_buffer.RemoveRange(0, count);
    }
    public ReadOnlySpan<byte> AsSpan() => CollectionsMarshalAsSpan();
    private ReadOnlySpan<byte> CollectionsMarshalAsSpan()
    {
        if (m_buffer.Count == 0) return ReadOnlySpan<byte>.Empty;
        // Fallback: copy to array slice to produce a span without extra allocations beyond array
        // We keep an internal array to avoid frequent ToArray.
        return new ReadOnlySpan<byte>(m_buffer.ToArray());
    }

    public ushort NextNs()
    {
        var current = Ns;
        Ns++;
        return current;
    }
}
