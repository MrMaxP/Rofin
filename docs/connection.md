# Connection Sequence

The full connection sequence to reach a controllable object on the Rofin
EasyMark E10. Every object reference is discovered dynamically — nothing
except the naming service port is hardcoded.

---

## Step-by-step

```
[1]  TCP connect to host:10050  (CosNaming service)

[2]  resolve("Controller")
     Request:  resolve on key=b"NameService", GIOP 1.2
               Name component: [{ id="Controller", kind="" }]
     Reply:    LOCATION_FORWARD → IOR for Controller @ host:49160
               (The naming service immediately forwards; follow the IOR.)

[3]  TCP connect to host:49160  (Controller object server)
     One persistent connection is used for all subsequent calls.

[4]  Controller.Login(user, hash, flag)
     → SystemControl ref

     Observed credentials:
       user  = "operator"
       hash  = "4b583376b2767b923c3e1da60d10de59"   (MD5 of password, pre-hashed)
       flag  = false

     The hash is replayed verbatim from the capture. The controller does not
     appear to use a per-session challenge — the same hash works across
     reboots. If authentication fails, try without Login (the pilot worked
     in an earlier capture without it).

[5]  SystemControl.GetMachineControl()
     → MachineControl ref

[6]  MachineControl.GetLaser()
     → PowerlineE ref  (the laser source object, generic name "Laser")

[7]  MachineControl.GetAxesControl()
     → AxesControl ref  (call fresh before each axis operation)

[8]  SystemControl.GetAllComponents()
     → flat list of 27 component IORs
     Used to discover: LIFAxis (for position), IOControl, etc.
```

---

## Object reference lifetimes

| Object | Lifetime | Notes |
|---|---|---|
| Controller | Per-boot (GUID in key) | Stable across sessions |
| SystemControl | Per-Login session | Re-Login gives a new ref |
| MachineControl | Stable while connected | Can cache |
| PowerlineE (Laser) | Stable while connected | Can cache |
| AxesControl | Unknown — re-fetch each use | Official client does this |
| LIFAxis | Stable while connected | Can cache key for polling |
| All other components | Stable while connected | Safe to cache after GetAllComponents |

---

## Login hash derivation

The hash appears to be the MD5 hex digest of the plaintext password with a
fixed salt or format. The exact derivation is not confirmed — the captured
value works for the `operator` role. Do not store plaintext passwords.

Known accounts seen in captures:
- `operator` — hash `4b583376b2767b923c3e1da60d10de59`

---

## GIOP version compatibility

| Step | GIOP version |
|---|---|
| resolve() on naming service | 1.2 (works); fall back to 1.0 if rejected |
| All controller operations | 1.2 |

The naming service at port 10050 accepted GIOP 1.2 in our captures. Some
older firmware may require GIOP 1.0 for the naming resolve; the
`NamingGiopMinor` setting in `ConnectionSettings` controls this.

---

## Disconnect / shutdown

There is no explicit logout operation in the captures. The controller closes
the TCP connection when the client disconnects. Disposal order:

1. Send `Jog(stop)` if an axis is moving
2. Set `pilotOn = false` if pilot is on
3. Close the TCP socket — the server cleans up automatically
