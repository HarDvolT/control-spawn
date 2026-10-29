The strongest explanation is **ASUS/Fan Xpert involvement in the Nuvoton hardware-monitor I/O lock, combined with a specific weakness in LHM 0.9.6’s handling of NCT6701D**. I found source-code evidence for that mechanism and an upstream report for your exact B850-F motherboard. I did **not** find evidence establishing that AMD B850 LPC decode must be enabled manually, or a public description of HWiNFO’s exact implementation on this board.

I only read the supplied files and online sources. I did not execute the probe scripts, invoke hardware/WMI methods, access the registry, change services, or install anything.

**1. Why the reads can return 0xFF**

There are three different mechanisms to distinguish:

| Mechanism | Assessment for this machine | Confidence |
|---|---|---|
| Nuvoton hardware-monitor I/O-space lock | Strongest specific lead; supported by LHM and ASUS firmware examples | Medium-high as the cause here; high that the mechanism exists |
| Concurrent ASUS accesses interfering with bank/index transactions | Plausible, with reports implicating Fan Xpert/AsusFanControlService | Medium |
| HWM logical device disabled | Possible; newer LHM source explicitly handles this for NCT6701D | Medium-low here |
| Wrong runtime base | Possible generally, but no evidence that your reported `0x290` is wrong | Low here |
| AMD LPC/eSPI decode disabled | Not established by the supplied results or sources I found | Low |
| Incorrect fan-register bank alone | Does not adequately explain an entire register dump returning `0xFF` | Low |
| An ACPI mutex simply remaining held | Not demonstrated; distinct from the chip’s hardware lock | Low |

The most useful findings are:

- **LHM 0.9.6 already attempts an initial hardware unlock.** In `LpcIO.cs`, lines 465–478, detection selects the HWM logical device, reads its base address, and calls `NuvotonDisableIOSpaceLock()` for NCT6701D. That function reads Super I/O configuration register **`0x28`** and clears **bit 4 (`0x10`)**. Thus “LHM never unlocks this chip” would be incorrect. [LHM 0.9.6 detection](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/LpcIO.cs#L465), [unlock implementation, lines 72–80 and 113](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/LpcPort.cs#L72).

- **Its recurring unlock path has a concrete NCT6701D problem.** `Nct677X.Update()` calls `DisableIOSpaceLock()`, but the 0.9.6 function’s chip list omits NCT6701D. Separately, `IsNuvotonVendor()` unconditionally returns true for NCT6701D, bypassing the actual vendor-register check. Consequently, merely adding NCT6701D to the recurring unlock list would still encounter that unconditional success check. This is consistent with detecting the chip successfully and then failing after another actor restores the lock; it does not prove that ASUS restored it on your machine. [LHM 0.9.6 `Nct677X.cs`, lines 1093–1096 and 1178–1201](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/Nct677X.cs#L1178).

- **The exact motherboard already has a relevant upstream report.** LHM issue **#1610**, titled for **ROG STRIX B850-F GAMING WIFI**, reports detection without sensor data. On February 8, 2025, the reporter said disabling Fan Xpert control and restarting Windows resolved it. Another commenter said that did not resolve their case; a later commenter reported success after removing the ASUS fan-control process. These are firsthand reports, not a controlled diagnosis. **Confidence: high that the workaround has been reported; medium that it explains your case.** [Issue #1610](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/1610).

ASUS firmware also provides a reason generic port reads and firmware-mediated reads could behave differently. The published B550-E ACPI example for `RHWM` acquires `ASMX`, enters Super I/O configuration, saves `HMLK`, clears it, selects the bank/index, reads the data, restores `HMLK`, and exits. **This is evidence for the access pattern, not proof that BIOS 1078 on B850-F implements identical AML.** [ASUS DSDT research repository, `RHWM` example](https://github.com/asus-wmi-boards-sensors/asus-board-dsdt#wm-functions).

A software mutex protects cooperating callers. The chip’s I/O lock controls hardware access. Taking an ISA-bus mutex does not itself clear the hardware lock, and direct I/O does not automatically participate in an ACPI mutex.

Also, `0x290` is the **base**, not the indexed data port: the conventional index/data pair is **`0x295`/`0x296`**. Your `rpmprobe.ps1` used that pair correctly.

**What HWiNFO does differently:** its author announced NCT6701D support in June 2024, but I found no public source or author statement identifying its exact B850-F transaction sequence. Repeated unlocks, different synchronization, and firmware-mediated access remain possible explanations. Claiming that it specifically programs an AMD decode register would be speculation. [HWiNFO author’s release announcement](https://www.hwinfo.com/forum/threads/hwinfo-v8-04-released.9792/).

**2. Windows routes without ASUS applications**

**The best new route to investigate is the banked firmware HWM-read method, rather than another generic port-read attempt.** A working all-header solution for *this exact BIOS and current software state* remains unverified.

| Candidate | What the evidence supports |
|---|---|
| LHM/PawnIO direct access | A credible eventual solution after resolving the lock/access failure. Installing PawnIO alone does not establish correct chip initialization or synchronization. |
| `WMBD` → `RHWM` | Strong architectural candidate: firmware performs the banked read and can manage its hardware lock. Windows exposure must be checked locally. |
| `asio_hw_fun21` | Particularly relevant: published Windows metadata gives inputs **`Bank, Index`** and output **`Data`**. Verify its `WmiMethodId` rather than assuming it maps to RHWM. |
| ASUS EC / LHM `Amd800` | Not an established source for all your fan headers. |
| `asio_hw_fun07/13` with an undocumented initialization | No verified initialization sequence found. Your existing results are insufficient to prove these are stubs. |
| FanControl default backend | Uses LHM, so switching applications may preserve the same underlying failure. |
| FanControl.AsusWMI | Targets the older ASUSHW sensor-enumeration interface; B850-F support is unproven. |
| Manually enabling AMD decode with PawnIO | Unsupported by the evidence and requires hardware configuration writes. |

The Linux protocol is explicit:

```text
ACPI method: WMBD
Argument 0:  0
Argument 1:  0x5248574D                 # RHWM
Argument 2: four-byte buffer containing:
            bank | (register << 8)    # little-endian packing
```

Linux identifies the firmware device using `PNP0C14` and UID `ASUSWMI` or `AsusMbSwInterface`. The historical monitoring GUID is **`466747A0-70EC-11DE-8A39-0800200C9A66`**. Linux’s change to direct ACPI evaluation means a Linux-supported `WMBD` path does **not** guarantee a usable Windows CIM method exists. Windows needs the appropriate class/method mapping. [Linux implementation, lines 111–186](https://codebrowser.dev/linux/linux/drivers/hwmon/nct6775-platform.c.html#111), [patch explaining the switch to direct ACPI calls](https://lists.openwall.net/linux-kernel/2023/01/10/1425).

The established Windows monitoring class is **`root\wmi:ASUSHW`**. The original Linux driver author’s Windows investigation lists `asio_hw_fun19` with `LDN, Index`, `asio_hw_fun21` with `Bank, Index`, and the separate `sensor_get_*` methods. Your scripts used **ASUSManagement**, so class identity and method qualifiers deserve checking before more calls. Names alone do not establish implementation. [Driver author’s PowerShell investigation](https://maidavale.org/blog/investigating-asus-wmi-sensors-from-powershell/).

Two qualifications matter:

- **Your generic-port results have incomplete status information.** `rpmprobe.ps1` extracts only `.bData` or `.Data`, discarding the complete method result. A zero output field does not distinguish a successful read from a failed/unsupported call returning a default field. It also does not validate the attempted bank change. Therefore the displayed zero RPM values are not confirmed tachometer readings. See local `rpmprobe.ps1`, functions `InB`, `OutB`, `ReadIdx`, and `rpmprobe_out.txt`.

- **Newer LHM source contains additional initialization logic, but that is not a verified fix for you.** Its current `LpcIO.cs`, lines 469–481, enables an inactive NCT6701D HWM logical device and considers alternate base registers `0x64/0x65` when the normal base is invalid. Your valid-looking `0x290` would not trigger that fallback. These operations include configuration writes. [Current detection source](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/LpcIO.cs#L469).

For EC access, LHM’s `Amd800` dictionary defines **CPU_OPT at EC `0x00B0`, two bytes**, alongside temperatures at `0x30`–`0x36`. It does not define a complete CPU/CHA1–4/AIO tachometer set there, and B850-F is absent from the board-specific EC table I inspected. Reusing the family definition would therefore be an experiment, not established support. **Confidence: high about the source; low about that address working on B850-F.** [EC board table and register definitions, lines 488–496](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/EC/EmbeddedController.cs#L488).

The community DSDT repository lists B850-F with `nct6775` status **U**, meaning “to upstream,” while its older ASUS sensor interface and EC columns are **N**. That supports pursuing the Nuvoton firmware route, but does not establish Windows functionality or compatibility with BIOS 1078. [Repository support table and legend](https://github.com/asus-wmi-boards-sensors/asus-board-dsdt#supported-boards).

FanControl’s default LHM dependency offers no independent bypass. Its ASUS WMI plugin documents testing on Crosshair VII and requires ASUSHW v3, possibly v2; that is a different claim from supporting B850-F’s raw RHWM path. [FanControl dependencies](https://github.com/Rem0o/FanControl.Releases#libraries-used), [ASUS WMI plugin compatibility](https://github.com/Mourdraug/FanControl.AsusWMI#compatibility).

**3. Most likely FanType mapping**

My provisional mapping is:

| `ASUSManagement.FanType` | Most likely meaning | Confidence |
|---|---|---|
| `0` | CPU fan policy, governing the shared CPU_FAN/CPU_OPT control | Medium |
| `1` | CHA_FAN1 policy | Low-medium |
| `2` | CHA_FAN2 policy, possibly only partly implemented | Low |
| `3` | CHA_FAN3 policy, possibly only partly implemented | Low |
| CHA_FAN4 / AIO_PUMP | No established mapping through the tested methods | Unknown |

**Do not use this table to write policies.** I found no authoritative enumeration tying those numeric values to B850-F physical headers.

Your `fanprobe_out.txt` establishes that types 0–3 return successful policy responses, but only 0–1 return successful curves. Types 2–3 return error 3 for every tested curve mode. It does not establish four independently functioning physical fan channels. The matching PWM/DC curves also provide little identifying information.

The hardware manual supplies one firm fact: **CPU_FAN and CPU_OPT share control group A**; CHA_FAN1–4 have separate control, and AIO_PUMP defaults to full speed. This explains seven physical tachometer connections but six independently presented control groups. It does not define `FanType`. [ASUS B850-F manual, printed page 23, “Fan and Pump headers”](https://dlcdnets.asus.com/pub/ASUS/mb/SocketAM5/ROG_STRIX_B850-F_GAMING_WIFI/E24372_ROG_STRIX_B850-F_GAMING_WIFI_EM_WEB.pdf#page=23).

`FanInfo.data` is worth examining from **your installed version**, if subsequently supplied for offline analysis. Community reverse engineering identifies it as Protocol Buffers, and users identify it together with `FanStore.xml` as storing fan configuration/calibration information. Neither report establishes the numeric `ASUSManagement.FanType` mapping on B850-F. Also, a calibration RPM table is not necessarily live telemetry. [Firsthand protobuf investigation](https://rog-forum.asus.com/t5/asus-software/crosshair-hero-x570-custom-waterloop-fanexpert-cant-control-pump/m-p/821827/highlight/true), [configuration-file report](https://rog-forum.asus.com/t5/armoury-crate/xpert-deletes-profiles-during-update/td-p/931686).

**4. Exact next test**

There is an important distinction in the brief’s “READ-ONLY” requirement:

- **Metadata-only inspection** can discover Windows exposure without invoking hardware methods.
- A **sensor-read operation** can still make temporary hardware writes internally: selecting a bank/index and, for ASUS’s RHWM example, clearing/restoring a hardware lock.
- A strictly zero-hardware-write test cannot directly diagnose an indexed register’s lock state.

Accordingly, the next test I recommend under the strict constraint is this **metadata-only PowerShell test**. Run in an administrator PowerShell window. It invokes no vendor method and writes no files:

```powershell
$ErrorActionPreference = 'Stop'
$targetGuid = '466747A0-70EC-11DE-8A39-0800200C9A66'

Get-CimClass -Namespace root\wmi | ForEach-Object {
    $c = $_
    $g = [string]$c.CimClassQualifiers['Guid'].Value

    if ($c.CimClassName -match 'ASUS' -or
        $g.Trim('{}') -ieq $targetGuid) {

        "CLASS=$($c.CimClassName) GUID=$g"

        foreach ($m in $c.CimClassMethods) {
            $q = $m.Qualifiers['WmiMethodId']
            if ($null -eq $q) { continue }

            $id = [uint32]$q.Value
            if ($m.Name -match 'asio_hw_fun(07|13|19|21)$' -or
                $id -eq [uint32]0x5248574D -or
                $id -eq [uint32]0x5253494F) {

                "  METHOD=$($m.Name) ID=0x$('{0:X8}' -f $id)"
                foreach ($p in $m.Parameters) {
                    "    PARAM=$($p.Name) TYPE=$($p.CimType) " +
                    "IN=$($p.Qualifiers['In'].Value) " +
                    "OUT=$($p.Qualifiers['Out'].Value)"
                }
            }
        }
    }
}
```

Interpret the output precisely:

- **A class with the target GUID and a method ID `0x5248574D` confirms that Windows exposes an RHWM method mapping.** A `Bank, Index` input signature would make a small CIM-based reader practical.
- **`asio_hw_fun21` with a different method ID does not confirm RHWM.** Its implementation needs further inspection.
- **No matching GUID/method refutes the proposed direct CIM route through the inspected namespace**, not the existence of ACPI `WMBD`, and not the hardware-lock theory.
- **This metadata test cannot confirm or refute the physical lock state.** Claiming otherwise would overstate its result.

If “read-only” allows firmware-managed sensor reads with temporary address/lock operations, the following is the minimal **conditional follow-up**. It deliberately refuses to invoke the method unless its GUID and method ID match RHWM. It performs no explicit port writes, fan-control writes, initialization, or service changes:

```powershell
$ErrorActionPreference = 'Stop'
$targetGuid = '466747A0-70EC-11DE-8A39-0800200C9A66'

$candidates = @(Get-CimClass -Namespace root\wmi | Where-Object {
    ([string]$_.CimClassQualifiers['Guid'].Value).Trim('{}') -ieq $targetGuid
})
$hits = @(foreach ($c in $candidates) {
    foreach ($m in $c.CimClassMethods) {
        $q = $m.Qualifiers['WmiMethodId']
        if ($null -ne $q -and [uint32]$q.Value -eq [uint32]0x5248574D) {
            [pscustomobject]@{ Class = $c; Method = $m }
        }
    }
})
if ($hits.Count -ne 1) { throw "Expected exactly one exposed RHWM method." }

$h = $hits[0]
$inputs = @($h.Method.Parameters | Where-Object {
    $_.Qualifiers['In'].Value -eq $true
})
if ($inputs.Count -ne 2 -or
    'Bank' -notin $inputs.Name -or 'Index' -notin $inputs.Name) {
    throw "Unexpected RHWM signature; inspect metadata before invoking."
}

$i = @(Get-CimInstance -Namespace root\wmi `
        -ClassName $h.Class.CimClassName)
if ($i.Count -ne 1) { throw "Expected exactly one instance." }

foreach ($bank in 0, 128) {
    "RHWM bank=$bank index=0x4F"
    Invoke-CimMethod -InputObject $i[0] -MethodName $h.Method.Name `
        -Arguments @{ Bank = [byte]$bank; Index = [byte]0x4F } |
        Format-List *
}
```

For the conventional Nuvoton identification scheme, the expected vendor bytes are **bank `0x00`, register `0x4F` → `0xA3`**, and **bank `0x80`, register `0x4F` → `0x5C`**. Reading the same bank twice is not expected to alternate between those bytes. [LHM vendor-register definitions](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/Nct677X.cs#L69).

- **Successful status plus `A3`/`5C`**, while direct access fails in the same state, confirms working firmware-mediated HWM access and supports an access-sequencing/lock explanation. It does not uniquely distinguish hardware locking from every routing possibility.
- **False/error status, unsupported-method results, or constant `00`/`FF`** fails to establish that route. Preserve the entire result, not just `Data`.
- After identification succeeds, tachometer registers can be checked; their physical-header labels still require validation.

For causal proof that Fan Xpert is responsible, an eventual controlled before/after test with ASUS fan control disabled and a reboot would be stronger, following issue #1610. That changes system state and therefore falls outside this research task.

Finally, I could not independently retrieve issue **#2613** during this research, so I treat its details as supplied evidence. The independently accessible exact-board issue **#1610** and the version-pinned LHM source are the stronger foundations for the conclusions above.