# Rofin LaserConsole

Cross-platform desktop client for the **Rofin EasyMark E10** laser marker,
built with Avalonia UI (.NET 10). Speaks raw GIOP 1.2 / CORBA over TCP —
no Rofin DLLs, no ORB library.

## What it does

- **Status & Control** — pilot laser on/off/blink with live state indicator
- **Devices** — dynamic component tree (all 27 hardware nodes enumerated live
  via `GetAllComponents`), per-component state/active/warning badges
- **Axis Control** — live Z-axis position readout (polled at 500 ms), travel
  bar between software limits, jog buttons (fast/slow up/down), stop, and
  reference drive

## Protocol

All control is over the CORBA/GIOP 1.2 protocol reverse-engineered from
Wireshark captures of the official LaserConsole client.

```
TCP :10050  resolve("Controller")           → Controller IOR @ host:49160
TCP :49160  Controller.Login(user, hash)    → SystemControl ref
            SystemControl.GetMachineControl()  → MachineControl ref
            MachineControl.GetLaser()          → PowerlineE ref
            MachineControl.GetAxesControl()    → AxesControl ref
            AxesControl.Jog(0, 2, direction)
            AxesControl.ReferenceDrive()
            PowerlineE.SetAttribute("pilotOn", any{boolean})
```

## Documentation

| File | Contents |
|---|---|
| [docs/protocol.md](docs/protocol.md) | GIOP 1.2 wire format, message framing, CDR types, IOR encoding |
| [docs/connection.md](docs/connection.md) | Full connection sequence, credentials, object lifetimes |
| [docs/api-operations.md](docs/api-operations.md) | All known API operations with arguments, return types, confidence level |
| [docs/components.md](docs/components.md) | All 27 component classes, hierarchy, state table |
| [docs/attributes.md](docs/attributes.md) | Per-class attribute names, types, R/W access, known values |

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet run
```

## Hardware

Default connection target: `192.168.0.200:10050`  
Configurable in the Connection panel at app startup.

## Notes

- **Pilot laser only** — `SetAttribute("pilotOn", true)` uses the low-power
  visible pointer. The marking beam (`beamOn`, `ExecutePrimitives`) is never
  triggered by this client.
- **Jog DOWN direction** — values `2` (slow) and `4` (fast) are inferred from
  the observed UP pattern (1/3). They have not yet been verified with a
  downward-jog capture.
- **`Components.dll`** — the IIOP.NET proxy DLL from the official LaserConsole
  install contains the full IDL type hierarchy and is useful for discovering
  method signatures. Location: Google Drive → `Rofin Laser/bin/LaserConsole/`.
