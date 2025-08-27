IEC 60870-5-104 (IEC104) examples with TouchSocket

Run slave (server):

```bash
cd examples/IEC104/Iec104SlaveConsoleApp
/workspace/.dotnet/dotnet run -c Release
```

Run master (client):

```bash
cd examples/IEC104/Iec104MasterConsoleApp
/workspace/.dotnet/dotnet run -c Release
```

What it does:
- Master connects to 127.0.0.1:2404, sends STARTDT act, then general interrogation (C_IC_NA_1).
- Slave replies STARTDT con, then responds to interrogation with ACT_CON, a dummy single-point (type 1), and ACT_TERM.

Notes:
- Uses TouchSocket TcpClient/TcpService with minimal IEC104 framing (U/I/S formats) implemented in the sample.
- Extend ASDU handling to fit your device model.
