namespace Iec104MasterConsoleApp;

using System.Buffers.Binary;
using System.Text;
using TouchSocket.Core;
using TouchSocket.Sockets;

class Program
{
    static async Task Main(string[] args)
    {
        var client = new TcpClient();
        await client.SetupAsync(new TouchSocketConfig()
            .SetRemoteIPHost(new IPHost("127.0.0.1:2404"))
            .ConfigureContainer(a => a.AddConsoleLogger())
        );

        client.Received = (c, e) =>
        {
            ProcessIncoming(e.ByteBlock.Span.ToArray());
            return EasyTask.CompletedTask;
        };

        await client.ConnectAsync();
        Console.WriteLine("IEC104 Master connected. Performing handshake...");

        // STARTDT act
        await client.SendAsync(Iec104Frames.BuildUFrame(Iec104UControl.StartDtAct));

        // wait a moment for STARTDT con
        await Task.Delay(100);

        // Send General Interrogation C_IC_NA_1 (type=100, COT=6 - ACT)
        ushort coa = 1;
        byte qoi = 0x14; // global interrogation
        var gi = Iec104Asdu.BuildInterrogation(typeId: 100, cot: 6, coa: coa, qoi: qoi);
        await client.SendAsync(Iec104Frames.BuildIFrame(_ns++, _nr, gi));

        Console.WriteLine("Sent interrogation. Press Ctrl+C to exit.");
        await Task.Delay(-1);
    }

    private static ushort _ns = 0;
    private static ushort _nr = 0;

    private static void ProcessIncoming(byte[] buffer)
    {
        int index = 0;
        while (index + 2 <= buffer.Length)
        {
            if (buffer[index] != 0x68)
            {
                index++;
                continue;
            }
            if (index + 2 > buffer.Length) break;
            int length = buffer[index + 1];
            int total = 2 + length;
            if (index + total > buffer.Length) break;

            var apciSpan = buffer.AsSpan(index + 2, 4);
            bool isI = (apciSpan[0] & 0x01) == 0;
            bool isS = (apciSpan[0] & 0x03) == 0x01;
            bool isU = !isI && !isS;

            if (isU)
            {
                var u = apciSpan[0];
                Console.WriteLine($"U frame: 0x{u:X2}");
            }
            else if (isS)
            {
                ushort nr2 = BinaryPrimitives.ReadUInt16LittleEndian(apciSpan.Slice(2, 2));
                ushort nr = (ushort)(nr2 / 2);
                Console.WriteLine($"S frame ack nr={nr}");
            }
            else
            {
                ushort ns2 = BinaryPrimitives.ReadUInt16LittleEndian(apciSpan.Slice(0, 2));
                ushort nr2 = BinaryPrimitives.ReadUInt16LittleEndian(apciSpan.Slice(2, 2));
                ushort ns = (ushort)(ns2 / 2);
                ushort nr = (ushort)(nr2 / 2);
                _nr = (ushort)(nr + 1);
                Console.WriteLine($"I frame ns={ns} nr={nr} len={length - 4}");
            }

            index += total;
        }
    }
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
        byte[] asdu = new byte[1 + 1 + 2 + 2 + 3 + 1];
        asdu[0] = typeId;
        asdu[1] = 0x01; // VSQ 1 obj
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(2, 2), cot);
        BinaryPrimitives.WriteUInt16LittleEndian(asdu.AsSpan(4, 2), coa);
        asdu[6] = 0; asdu[7] = 0; asdu[8] = 0; // IOA=0
        asdu[9] = qoi;
        return asdu;
    }
}
