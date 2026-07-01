# Component Hierarchy

The controller exposes 27 hardware components via `SystemControl.GetAllComponents()`.
The call returns a **flat** list — there is no recursive walk API. The hierarchy
shown below is reconstructed using a static class→parent mapping inferred from
the official LaserConsole UI.

All names and keys are live data fetched at runtime. Only the visual grouping
is a static heuristic (see `LaserService.ComponentParentClass`).

---

## Full tree

```
Controller  (resolved via CosNaming, port 49160)
└── SystemControl
    ├── ErrorMonitor
    ├── ProgramControl
    └── MachineControl
        ├── LIF_Driver          (generic: "LifDriver")
        ├── Interpolation
        │   └── IIF_Driver
        ├── CANOpen
        ├── IOControl
        │   ├── LIFIO           (generic: "IO")
        │   └── PLTIO           (generic: "IO")
        ├── PowerlineE          (generic: "Laser")  ← laser source
        │   ├── PowerSupply_HN800  (generic: "PowerSupply")
        │   └── PLELaserHead    (generic: "SafetyShutter")
        ├── GalvoControl
        │   ├── GalvoHeadContainer
        │   │   └── GalvoHead
        │   ├── MarkingOnTheFly
        │   └── ScannerAutocalibration
        ├── AxesControl
        │   └── AxesControllerLIF  (generic: "AxesController")
        │       ├── LIFAxis  [0]   (generic: "Axis")  ← Z axis, active
        │       └── LIFAxis  [1]   (generic: "Axis")  ← suspended/unused
        ├── GenericCommand
        ├── GenericEvent
        ├── PLC
        └── ServerHeartbeat
```

---

## Component table

All 27 components from the live `GetAllComponents()` response (Rofin EasyMark E10,
firmware as of 2026-06-30). `state` and `active` confirmed via live attribute reads.

| # | className | genericName | state | active |
|---|---|---|---|---|
| 1 | ErrorMonitor | ErrorMonitor | running | true |
| 2 | MachineControl | MachineControl | running | true |
| 3 | LIF_Driver | LifDriver | running | true |
| 4 | Interpolation | Interpolation | running | true |
| 5 | IIF_Driver | IIF_Driver | running | true |
| 6 | PLC | PLC | running | true |
| 7 | SystemControl | SystemControl | running | true |
| 8 | PowerlineE | Laser | running | true |
| 9 | PowerSupply_HN800 | PowerSupply | running | true |
| 10 | CANOpen | CANOpen | running | true |
| 11 | PLELaserHead | SafetyShutter | running | true |
| 12 | IOControl | IOControl | running | true |
| 13 | LIFIO | IO | running | true |
| 14 | PLTIO | IO | running | true |
| 15 | AxesControl | AxesControl | running | true |
| 16 | AxesControllerLIF | AxesController | running | true |
| 17 | LIFAxis | Axis | running | true |
| 18 | LIFAxis | Axis | **suspended** | **false** |
| 19 | GalvoControl | GalvoControl | running | true |
| 20 | MarkingOnTheFly | MarkingOnTheFly | **suspended** | **false** |
| 21 | ScannerAutocalibration | ScannerAutocalibration | **suspended** | **false** |
| 22 | GalvoHeadContainer | GalvoHeadContainer | running | true |
| 23 | GalvoHead | GalvoHead | running | true |
| 24 | GenericCommand | GenericCommand | running | true |
| 25 | GenericEvent | GenericEvent | running | true |
| 26 | ServerHeartbeat | ServerHeartbeat | running | true |
| 27 | ProgramControl | ProgramControl | running | true |

`SystemControl` (row 7) appears as a self-reference in the flat list and is
skipped when building the tree.

---

## `state` enum values

| Integer | String | Meaning |
|---|---|---|
| 0 | `suspended` | Component initialised but not running |
| 1 | `suspending` | Transitioning to suspended |
| 2 | `resuming` | Transitioning to running |
| 3 | `running` | Normal operational state |
| 4 | `error` | Fault condition |

---

## Why the hierarchy is static

The API has no "get children of X" call. The official LaserConsole client
uses the C# type hierarchy from `Components.dll` (IIOP.NET proxy stubs) to
determine which object type a component is, and maps that to a visual tree at
compile time. We replicate this with a static `className → parentClassName`
dictionary in `LaserService.ComponentParentClass`.

Unknown class names fall back to `MachineControl` as the default parent.

---

## Adding new components

If a future firmware version returns additional components:

1. Note the `className` from `_get_className()`
2. Look up the class in `Components.dll` using ILSpy or `ilasm`
3. Identify its IDL parent interface
4. Add an entry to `ComponentParentClass` in `LaserService.cs`
