#requires -Version 5.1
<#
    YeManCC Intel TDP 天花板
    ========================================================================
    问题
    ------------------------------------------------------------------------
    这台 AYANEO AIR PLUS Intel（i3-1215U）的固件在 MCHBAR 里把 PL1 钉成了
    15W（+0x59A0 = 0x00DD8078）。PCODE 对 MSR 0x610 与 MCHBAR 两个来源取
    min()，所以只写 MSR（YeManTdpCtl 走的路径）永远突破不了 15W —— 原厂
    AYASpace 在 Intel 机型上同样调不上去（反汇编确认它只写 MSR 0x610）。

    做法
    ------------------------------------------------------------------------
    把 MCHBAR 的 PL1/PL2 抬到设定值（默认 25W）当作**天花板**，之后面板里
    任何 MSR 侧 TDP 都按 min(MSR, 天花板) 生效：
        · 面板设 12W → 实际 12W
        · 面板设 30W → 实际被限到 25W（即"游戏时最高不超过 25"）
    天花板每个开机周期落一次即可，不必跟着滑块反复写。

    依赖
    ------------------------------------------------------------------------
    写 MCHBAR 需要"把物理地址映射进用户态"的能力，PawnIO 没有（其上游
    IntelMCHBAR 模块只有读）。这里复用一份签名有效的 MMIO 驱动副本：
        <PowerControl>\mmio\YeManMmio.sys
    服务名必须是 ThrottleStop —— 该驱动的设备名由服务名推导，换名字会在
    DriverEntry 返回 STATUS_OBJECT_NAME_INVALID（已实测）。

    用法
    ------------------------------------------------------------------------
        YeManTdpCeiling.ps1                 # 按设置应用（默认 25W）
        YeManTdpCeiling.ps1 -Watts 20       # 临时指定上限
        YeManTdpCeiling.ps1 -Status         # 只读当前值
        YeManTdpCeiling.ps1 -Revert         # 还原出厂 15W / 55W

    输出：stdout 恒为单行 JSON（供前端解析）；过程日志写 tdp-ceiling.log。
#>
[CmdletBinding()]
param(
    [int]$Watts = 0,
    # 保证天花板不低于该值：已经够高时直接跳过写入。TDP 下发后的自动联动用它，
    # 这样自动浮动在区间内反复调整不会每次都真正写一次 MCHBAR。
    [int]$MinWatts = 0,
    # 同时改写 MSR 0x610。默认关闭：MSR 是面板实际下发的"目标值"，由 YeManTdpCtl
    # 精确管理（含 PL2 短时预算）。这里只负责把 MCHBAR 的固件上界抬高，碰 MSR 会
    # 把用户设定的 TDP/PL2 覆盖成天花板值（例如设 16W 却变成 25W）。仅排障时显式开启。
    [switch]$WithMsr,
    [switch]$Revert,
    [switch]$Status,
    [string]$SettingsPath = 'C:\SOFT\YeMan\PowerControl\yeman-settings.json',
    [string]$ServiceName = 'ThrottleStop'
)

$ErrorActionPreference = 'Stop'

# ── 硬件常量（本机实测，Intel ADL-P i3-1215U）──────────────────────────
# PowerShell 会把 >int32 的十六进制字面量解析成负数，必须显式按 hex 解析。
function ConvertFrom-Hex([string]$Text) { [uint64]::Parse($Text, [Globalization.NumberStyles]::HexNumber) }

$MCHBAR_PHYS = ConvertFrom-Hex 'FEDC0000'
$MAP_SIZE    = [uint32]0x6000
$OFF_PL1     = 0x59A0
$OFF_PL2     = 0x59A4
$POWER_UNIT  = 0.125          # MSR 0x606 的 power unit，本机 exponent=3
$FACTORY_PL1 = 15
$FACTORY_PL2 = 55
$MAX_ALLOWED = 30             # 脚本层硬上限，防止误配把机器推爆
$DEFAULT_CAP = 25

# ── IOCTL（逆向自驱动，DeviceType 0x22）────────────────────────────────
$IOCTL_MAP   = [uint32](ConvertFrom-Hex '8000645C')   # {phys:u64, size:u32} -> {userVa:u64}
$IOCTL_RDMSR = [uint32](ConvertFrom-Hex '80006448')   # {index:u32} -> {value:u64}
$IOCTL_WRMSR = [uint32](ConvertFrom-Hex '8000644C')   # {index:u32, value:u64}
$GENERIC_READ_WRITE = [uint32](ConvertFrom-Hex 'C0000000')
$MSR_PKG_POWER_LIMIT = [uint32]0x610

$powerControlDir = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$DriverPath = Join-Path (Join-Path $powerControlDir 'mmio') 'YeManMmio.sys'

$logDir = Join-Path $env:LOCALAPPDATA 'YeManCC野蛮系统控制中心'
if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logPath = Join-Path $logDir 'tdp-ceiling.log'

function Write-Log([string]$Message) {
    $line = ('{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), $Message)
    try { Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8 } catch { }
}

function Send-Result($Object) {
    $Object | ConvertTo-Json -Compress -Depth 5
}

# ── P/Invoke ──────────────────────────────────────────────────────────
if (-not ('YmccMmio' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class YmccMmio
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
'@
}

function Get-Setting {
    param($Object, [string[]]$Path, $Default)
    $cursor = $Object
    foreach ($key in $Path) {
        if ($null -eq $cursor) { return $Default }
        $prop = $cursor.PSObject.Properties[$key]
        if ($null -eq $prop) { return $Default }
        $cursor = $prop.Value
    }
    if ($null -eq $cursor) { return $Default }
    return $cursor
}

# ── 读取面板设置 ───────────────────────────────────────────────────────
$settings = $null
if (Test-Path -LiteralPath $SettingsPath) {
    try { $settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch {
        Write-Log ("设置文件解析失败，改用默认值: " + $_.Exception.Message)
    }
}
$ceilingCfg  = Get-Setting $settings @('tdp', 'intelCeiling') $null
# 默认开启：与前端 normalizeTdpCeiling 保持一致。设置文件里没有该项时也要生效，
# 否则"打开面板就能用 25W"会退化成必须先去设置页点一次。
$ceilingOn   = if ($null -ne $ceilingCfg -and $null -ne $ceilingCfg.PSObject.Properties['enabled']) { [bool]$ceilingCfg.enabled } else { $true }
$configuredW = Get-Setting $ceilingCfg @('watts') $DEFAULT_CAP

# 显式 -Watts / -MinWatts 都是明确意图，不受设置里的 enabled 开关限制（供面板下发与排障）。
$onlyRaise = $false
if ($MinWatts -gt 0) { $Watts = $MinWatts; $onlyRaise = $true }
$explicitWatts = ($PSBoundParameters.ContainsKey('Watts') -and $Watts -gt 0) -or ($MinWatts -gt 0)
if ($Watts -le 0) { $Watts = [int]$configuredW }
if ($Watts -le 0) { $Watts = $DEFAULT_CAP }

$hardMax = [Math]::Min($MAX_ALLOWED, [Math]::Max(5, [int](Get-Setting $ceilingCfg @('hardMax') 25)))
if ($Watts -gt $hardMax) {
    Write-Log ("请求 {0}W 超过本机允许上限 {1}W，已钳制" -f $Watts, $hardMax)
    $Watts = $hardMax
}

# ── 平台门禁 ───────────────────────────────────────────────────────────
# 0xFEDC0000 / +0x59A0 这组偏移是 Intel ADL-P 的 RAPL 布局；在 AMD 或其它
# Intel 代际上含义不同，盲写可能改到无关寄存器。所以先确认是 GenuineIntel，
# 且读出的 PL1 落在合理量程内，否则一律不动硬件。
function Test-IntelPlatform {
    try {
        $vendor = (Get-ItemProperty -Path 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' `
            -Name VendorIdentifier -ErrorAction Stop).VendorIdentifier
        return ($vendor -eq 'GenuineIntel')
    } catch {
        return $false
    }
}

# ── 设备与服务 ─────────────────────────────────────────────────────────
function Open-MmioDevice {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        # 首次部署：驱动副本在但没有服务项时按需注册（面板以管理员运行）。
        if (-not (Test-Path -LiteralPath $DriverPath)) {
            # 驱动只随 TDP 助手/ThrottleStop 释放到临时目录，装面板时不一定带上；
            # 两份是同一个已签名驱动，服务名继续用 ThrottleStop 复用已通过的校验。
            $fallback = Join-Path $env:TEMP 'ThrottleStop.sys'
            if (Test-Path -LiteralPath $fallback) {
                try {
                    New-Item -ItemType Directory -Path (Split-Path -Parent $DriverPath) -Force | Out-Null
                    Copy-Item -LiteralPath $fallback -Destination $DriverPath -Force
                    Write-Log ('已从临时目录复制 MMIO 驱动 → ' + $DriverPath)
                } catch { Write-Log ('复制 MMIO 驱动失败: ' + $_.Exception.Message) }
            }
        }
        if (-not (Test-Path -LiteralPath $DriverPath)) {
            Write-Log ("MMIO 驱动不存在: " + $DriverPath)
            return $null
        }
        try {
            $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $ServiceName
            New-Service -Name $ServiceName -BinaryPathName ('\??\' + $DriverPath) `
                -DisplayName 'YeManCC MMIO' -StartupType Manual -ErrorAction Stop | Out-Null
            Set-ItemProperty -Path $svcKey -Name Type -Value 1 -Type DWord
            Set-ItemProperty -Path $svcKey -Name ImagePath -Value ('\??\' + $DriverPath) -Type String
            Write-Log ("已注册 MMIO 服务 " + $ServiceName)
            $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        } catch {
            Write-Log ("注册 MMIO 服务失败: " + $_.Exception.Message)
            return $null
        }
    }
    if ($null -eq $svc) { return $null }
    if ($svc.Status -ne 'Running') {
        try { Start-Service -Name $ServiceName -ErrorAction Stop } catch {
            Write-Log ("MMIO 驱动启动失败: " + $_.Exception.Message)
            return $null
        }
        Start-Sleep -Milliseconds 350
    }
    $handle = [YmccMmio]::CreateFileW('\\.\' + $ServiceName, $GENERIC_READ_WRITE, [uint32]0,
        [IntPtr]::Zero, [uint32]3, [uint32]0x80, [IntPtr]::Zero)
    if ($handle -eq [IntPtr]::Zero -or $handle -eq [IntPtr](-1)) {
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Log ("打开驱动设备失败, Win32Error={0}" -f $err)
        return $null
    }
    return $handle
}

function Invoke-Ioctl([IntPtr]$Handle, [uint32]$Code, [byte[]]$In, [int]$OutSize) {
    if ($null -eq $In) { $In = New-Object byte[] 1 }
    $out = New-Object byte[] ([Math]::Max($OutSize, 1))
    $returned = 0
    $ok = [YmccMmio]::DeviceIoControl($Handle, $Code, $In, $In.Length, $out, $OutSize, [ref]$returned, [IntPtr]::Zero)
    if (-not $ok) {
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw ("DeviceIoControl 0x{0:X8} 失败, Win32Error={1}" -f $Code, $err)
    }
    return ,$out
}

function Read-Msr([IntPtr]$Handle, [uint32]$Index) {
    $in = [BitConverter]::GetBytes($Index)
    $out = Invoke-Ioctl $Handle $IOCTL_RDMSR $in 8
    return [BitConverter]::ToUInt64($out, 0)
}

function Write-Msr([IntPtr]$Handle, [uint32]$Index, [uint64]$Value) {
    $in = New-Object byte[] 12
    [Array]::Copy([BitConverter]::GetBytes($Index), 0, $in, 0, 4)
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $in, 4, 8)
    Invoke-Ioctl $Handle $IOCTL_WRMSR $in 0 | Out-Null
}

function Get-CeilingState([IntPtr]$Handle) {
    $in = New-Object byte[] 12
    [Array]::Copy([BitConverter]::GetBytes([uint64]$MCHBAR_PHYS), 0, $in, 0, 8)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$MAP_SIZE), 0, $in, 8, 4)
    $out = Invoke-Ioctl $Handle $IOCTL_MAP $in 8
    $va = [BitConverter]::ToUInt64($out, 0)
    if ($va -eq 0) { throw 'MCHBAR 映射返回空地址' }

    $mchPl1Raw = [Runtime.InteropServices.Marshal]::ReadInt32([IntPtr]([int64]$va + $OFF_PL1))
    $mchPl2Raw = [Runtime.InteropServices.Marshal]::ReadInt32([IntPtr]([int64]$va + $OFF_PL2))
    # 基址/布局不对时这两个位置会是 0、全 1，或量程明显不合理 —— 明确报错而不是盲写。
    $pl1W = ($mchPl1Raw -band 0x7FFF) * $POWER_UNIT
    $pl2W = ($mchPl2Raw -band 0x7FFF) * $POWER_UNIT
    if (($mchPl1Raw -eq 0 -and $mchPl2Raw -eq 0) -or
        ($mchPl1Raw -eq -1 -and $mchPl2Raw -eq -1) -or
        $pl1W -lt 3 -or $pl1W -gt 150 -or $pl2W -lt 3 -or $pl2W -gt 200) {
        throw ('MCHBAR 布局与预期不符，已放弃写入: 0x59A0=0x{0:X8}({1:N1}W) 0x59A4=0x{2:X8}({3:N1}W)' -f `
            $mchPl1Raw, $pl1W, $mchPl2Raw, $pl2W)
    }
    $msr610 = Read-Msr $Handle $MSR_PKG_POWER_LIMIT
    return [pscustomobject]@{
        Va        = $va
        MchPl1Raw = $mchPl1Raw
        MchPl2Raw = $mchPl2Raw
        MchPl1W   = ($mchPl1Raw -band 0x7FFF) * $POWER_UNIT
        MchPl2W   = ($mchPl2Raw -band 0x7FFF) * $POWER_UNIT
        Msr610    = $msr610
        MsrPl1W   = ($msr610 -band 0x7FFF) * $POWER_UNIT
        MsrPl2W   = (($msr610 -shr 32) -band 0x7FFF) * $POWER_UNIT
    }
}

# ── 主流程 ────────────────────────────────────────────────────────────
$handle = $null
try {
    if (-not (Test-IntelPlatform)) {
        Write-Log '非 Intel 平台，跳过 MCHBAR 天花板（本功能只服务 Intel RAPL 布局）'
        Send-Result ([ordered]@{ ok = $false; status = 'unsupported'; reason = 'not-intel-platform'; ceilingW = 0 })
        exit 0
    }

    $handle = Open-MmioDevice
    if ($null -eq $handle) {
        Write-Log 'MMIO 通道不可用（驱动未加载），天花板未改动'
        Send-Result ([ordered]@{ ok = $false; status = 'unavailable'; reason = 'mmio-driver-unavailable'; ceilingW = 0 })
        exit 0
    }

    $state = Get-CeilingState $handle
    Write-Log ("当前: MCHBAR PL1={0:N1}W PL2={1:N1}W | MSR0x610 PL1={2:N1}W PL2={3:N1}W" -f `
        $state.MchPl1W, $state.MchPl2W, $state.MsrPl1W, $state.MsrPl2W)

    if ($Status) {
        Send-Result ([ordered]@{
            ok = $true; status = 'ok'
            ceilingW = [double]$state.MchPl1W
            ceilingPl2W = [double]$state.MchPl2W
            msrPl1W = [double]$state.MsrPl1W
            msrPl2W = [double]$state.MsrPl2W
            enabled = $ceilingOn
        })
        exit 0
    }

    if ($Revert) {
        $targetPl1 = $FACTORY_PL1
        $targetPl2 = $FACTORY_PL2
        $writeMsr = $false
        Write-Log '还原出厂 MCHBAR 天花板 15W / 55W'
    } else {
        if (-not $ceilingOn -and -not $explicitWatts) {
            Write-Log '面板里未启用 TDP 天花板（tdp.intelCeiling.enabled=false），不改动硬件'
            Send-Result ([ordered]@{ ok = $true; status = 'disabled'; ceilingW = [double]$state.MchPl1W })
            exit 0
        }
        $targetPl1 = $Watts
        # PL2 留 5W 爆发余量（游戏加载/场景切换），但绝不越过 hardMax——
        # 用户要求"游戏时最高不超过 25W"，所以爆发也只能到 25。
        $burst = [int](Get-Setting $ceilingCfg @('burstWatts') ([Math]::Min($hardMax, $targetPl1 + 5)))
        $targetPl2 = [Math]::Max($Watts, $burst)
        if ($targetPl2 -gt $hardMax) { $targetPl2 = $hardMax }
        $writeMsr = [bool]$WithMsr
        # 只升不降：当前天花板已经不低于目标时直接确认返回（MCHBAR 是上界，
        # 低于它的 TDP 天然生效），避免浮动调度每次调整都真写一次寄存器。
        if ($onlyRaise -and $state.MchPl1W -ge ($targetPl1 - 0.01)) {
            Write-Log ("天花板已是 {0:N1}W，不低于目标 {1}W，跳过写入" -f $state.MchPl1W, $targetPl1)
            Send-Result ([ordered]@{ ok = $true; status = 'ok'; ceilingW = [double]$state.MchPl1W
                ceilingPl2W = [double]$state.MchPl2W; targetW = $targetPl1
                msrPl1W = [double]$state.MsrPl1W; msrPl2W = [double]$state.MsrPl2W })
            exit 0
        }
    }

    $pl1Field = [int]($targetPl1 / $POWER_UNIT)
    $pl2Field = [int]($targetPl2 / $POWER_UNIT)

    # MCHBAR：只改低 15 位功耗码，保留使能位与其它字段
    $newPl1 = ($state.MchPl1Raw -band (-bnot 0x7FFF)) -bor ($pl1Field -band 0x7FFF)
    $newPl2 = ($state.MchPl2Raw -band (-bnot 0x7FFF)) -bor ($pl2Field -band 0x7FFF)
    [Runtime.InteropServices.Marshal]::WriteInt32([IntPtr]([int64]$state.Va + $OFF_PL1), $newPl1)
    [Runtime.InteropServices.Marshal]::WriteInt32([IntPtr]([int64]$state.Va + $OFF_PL2), $newPl2)

    if ($writeMsr) {
        # MSR 0x610: PL1[14:0] / PL2[46:32]，其余位（时间窗、clamp、lock）保持原样
        $msr = $state.Msr610
        $msr = ($msr -band (-bnot [uint64]0x7FFF)) -bor [uint64]($pl1Field -band 0x7FFF)
        $msr = ($msr -band (-bnot ([uint64]0x7FFF -shl 32))) -bor ([uint64]($pl2Field -band 0x7FFF) -shl 32)
        Write-Msr $handle $MSR_PKG_POWER_LIMIT $msr
    }

    Start-Sleep -Milliseconds 120
    $after = Get-CeilingState $handle
    $pl1Ok = [Math]::Abs($after.MchPl1W - $targetPl1) -lt 0.01
    $pl2Ok = [Math]::Abs($after.MchPl2W - $targetPl2) -lt 0.01
    Write-Log ("写入后: MCHBAR PL1={0:N1}W PL2={1:N1}W | MSR0x610 PL1={2:N1}W PL2={3:N1}W | 校验 {4}" -f `
        $after.MchPl1W, $after.MchPl2W, $after.MsrPl1W, $after.MsrPl2W, $(if ($pl1Ok -and $pl2Ok) { 'OK' } else { 'MISMATCH' }))

    Send-Result ([ordered]@{
        ok = ($pl1Ok -and $pl2Ok)
        status = if ($pl1Ok -and $pl2Ok) { 'applied' } else { 'mismatch' }
        ceilingW = [double]$after.MchPl1W
        ceilingPl2W = [double]$after.MchPl2W
        targetW = $targetPl1
        msrPl1W = [double]$after.MsrPl1W
        msrPl2W = [double]$after.MsrPl2W
    })
    if (-not ($pl1Ok -and $pl2Ok)) { exit 3 }
    exit 0
} catch {
    Write-Log ('异常: ' + $_.Exception.Message)
    Send-Result ([ordered]@{ ok = $false; status = 'error'; reason = $_.Exception.Message; ceilingW = 0 })
    exit 4
} finally {
    if ($null -ne $handle -and $handle -ne [IntPtr]::Zero) { [YmccMmio]::CloseHandle($handle) | Out-Null }
}
