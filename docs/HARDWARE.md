# Control Spawn — verified hardware facts (tested on this PC, 2026-10-28)

| Part | Model | Control path (verified) |
|---|---|---|
| Motherboard | ASUS ROG STRIX B850-F GAMING WIFI, BIOS 1078 | RGB: USB HID AURA controller 0B05:19AF via OpenRGB 1.1 ✅ |
| LED strip | ARGB header on the board | OpenRGB (part of the board controller) ✅ |
| RAM | Corsair Vengeance RGB DDR5 2×16 GB (CMH32GX5M2B6000Z30) | OpenRGB over SMBus, needs PawnIO + admin ✅ |
| GPU | ASRock Radeon RX 9070 XT Taichi 16GB OC | RGB via OpenRGB ✅ (fans stay with AMD driver) |
| CPU | AMD Ryzen 7 9800X3D | Temps via LibreHardwareMonitorLib 0.9.6 (Tctl/Tdie, CCD1) ✅ |
| Fan chip | Nuvoton NCT6701D (chip id 0xD806, base 0x290) | LibreHardwareMonitorLib + PawnIO 2.2, admin ✅ |
| Lamp | Yeelight colour bulb, model yeelink.light.color5 | Yeelight LAN protocol (TCP 55443, SSDP discovery on 239.255.255.250:1982), LAN Control must be on |
| Router | ZLT X17M 5G (Ooredoo TN) | see E:\Ai\Claude\router-signal-widget-package (Python reference) |

## Fan map (from live tests)

| App name | LHM sensor | Physical | Notes |
|---|---|---|---|
| Radiator | Fan #2 / Control #2 | CPU_FAN header, radiator fans | 1268 rpm idle, 2987 rpm at 100% |
| Case fans | Fan #6 / Control #6 | front 3 + rear on one splitter | 622 rpm idle, 1636 rpm at 100%, ~480 at 20% |
| AIO pump | Fan #7 / Control #7 | AIO_PUMP | ~2800 rpm, NEVER slow it down: fixed 100% |
| (empty) | Fan #1, #3, #4, #5 | — | 0 rpm, show when a fan is plugged in |

## Critical facts

- **AsusFanControlService must be stopped/disabled.** While it runs, every NCT6701D register reads 0xFF/0 (all RPM = 0). Disabled + reboot → real readings. Same as LibreHardwareMonitor issue #1610 on this board.
- Control: `ISensor.Control.SetSoftware(percent)` works; `SetDefault()` hands the header back to BIOS. Always call `SetDefault()` on exit/crash.
- ASUS firmware WMI `root\wmi ASUSManagement` has GetFanPolicy/SetFanPolicy (FanType 0–3, profiles like STANDARD) and Get/SetManualFanCurvePro (8 points). Its port/RHWM methods (asio_hw_fun07/13/21) return 0 on this BIOS: do not use them.
- OpenRGB: its SDK server is TCP 6742. iCUE and the ASUS AURA services fight over the same chips, so they must be stopped when Control Spawn controls lighting.
- PawnIO kernel driver 2.2 is installed (service "PawnIO"). Hardware access needs admin, so the hardware runs in an elevated Windows service, and the UI talks to it over a named pipe.

## Lighting findings (live tests, 2026-10-29)

- All devices take Control Spawn's frames through OpenRGB 1.1: 2x Vengeance RGB DDR5 (10 LEDs), RX 9070 XT Taichi (4),
  ROG STRIX B850-F (board LEDs + ARGB header, resizable). Fans' RGB rings are not in OpenRGB's device list yet.
- GPU lighting chip: max 10 fps from Control Spawn, still frames refreshed only every 10 s, and writes take turns with GPU
  sensor reads (`BusTurns.Gpu`). Red flashes on the GPU came from AMD Software / Armoury Crate also using the bus.
- RAM lighting shares the chipset SMBus with the DIMM temperature sensors: writes take turns (`BusTurns.Smbus`).
- OpenRGB "device list updated" notices must be handled by refreshing in place. A full reconnect re-initialises
  every controller (RAM went dark and the ROG logo flashed every ~5 s, measured frame by frame from a phone video).
- Final check: 30 s static blue, GPU 0 / RAM 0 / board 0 flickers, 0 RGB reconnects, with AMD Software running.
- ArmouryCrateService sometimes refuses to stop; uninstalling Armoury Crate and iCUE is the clean end state.
