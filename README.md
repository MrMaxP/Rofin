# RofinPilotTest

Minimal, dependency-free C# client that proves we can command the Rofin
EasyMark E10 from our own code, by toggling the **pilot (alignment) laser**.
It speaks raw GIOP 1.2 / CORBA over TCP — no Rofin DLLs, no ORB library — so
it ports cleanly to Python later for the LightBurn pipeline.

## What it does (and deliberately does NOT do)

It only writes the boolean attribute `pilotOn`. It never sends
`ExecutePrimitives`, `SetLaserParameters`, `beamOn`, or starts a program, so
**no marking beam is ever fired**. The pilot is the low-power visible pointer —
a safe, instantly-visible proof that the control channel works end to end.

## The protocol it replays (all reconstructed from the captures)

```
TCP :10050  resolve(["Controller"])              -> LOCATION_FORWARD (status=3)
                                                   -> Controller IOR @ host:49160
TCP :49160  Controller.Login(user,hash,..)       -> SystemControl ref
            SystemControl.GetMachineControl()    -> MachineControl ref
            MachineControl.GetLaser()            -> Laser IOR
            Laser.SetAttribute("pilotOn", any{boolean})
```

Key facts baked in: GIOP 1.2 requests are big-endian; reply byte order is read
per-message; CORBA strings include the trailing NUL while object keys do not;
the `pilotOn` value rides in an `any` whose TypeCode is `tk_boolean` (8) followed
by the boolean value (one octet) plus a padding byte (crucial for proper alignment).
The naming service normally returns LOCATION_FORWARD (status=3), which the code
handles by retrying with the forwarded object key. The `SetAttribute`, `Login`,
`resolve`, and the full request framing were verified byte-for-byte against
`WS-PilotOnOff` / `WS-LaserConsoleBoot`.

Object keys embed a per-boot instance GUID, so nothing is hardcoded — the Laser
reference is discovered live via `GetLaser()` each run.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Runs on Windows 11 and macOS.

```
dotnet build -c Release
```

## Run

```
RofinPilotTest                 # default: pilot ON, hold 3s, OFF  (safe blink)
RofinPilotTest on              # pilot ON and leave on
RofinPilotTest off             # pilot OFF
RofinPilotTest blink 5         # ON, hold 5s, OFF
RofinPilotTest --host 192.168.0.200
RofinPilotTest --no-login      # skip Login (pilot worked without it in capture)
RofinPilotTest --giop10-naming # use GIOP 1.0 for resolve() if 1.2 is rejected
```

## If something fails

- **resolve() fails** — the naming service may insist on GIOP 1.0. Add
  `--giop10-naming` (or set `Config.NamingGiopMinor = 0`). This path is also
  byte-exact against the capture.
- **Login fails** — the captured `operator` hash is replayed verbatim. If the
  controller uses a per-session challenge (the capture suggests it does not),
  this would need the live challenge. Try `--no-login` first.
- **SetAttribute returns status 2** (SYSTEM_EXCEPTION) — most likely the object
  key wasn't found or write access was denied. In the capture the pilot needed
  **no** `RefreshMasterAccess`, so this would point at a different controller
  state (e.g. LaserConsole holding an exclusive lock — try with it closed).

## Notes for the next steps

- Same plumbing extends to the real goal: `GetProgramControl()` /
  `GetMachineControl()` are already discoverable the same way, and
  `ExecutePrimitives` / `SetLaserParameters` are just more CDR bodies — but those
  fire the beam, so they belong behind the enclosure/interlock checks.
- If you later want typed proxies for the larger surface instead of hand-rolled
  CDR, the `ComponentsProxyCS.dll` you provided is IIOP.NET-based
  (`rofin.com.ControllerComponents`); referencing it plus `IIOPChannel.dll`
  would give you `Controller`/`Laser` stubs directly.
