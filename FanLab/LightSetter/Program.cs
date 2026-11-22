// YeMan 灯光设置器（YeManLightSetter）——一次性灯光设置工具
// 2026-09-16（HC 机型库适配 L1 方案 B）：
// 直驱 HC 程序集（HandheldCompanion.dll）执行灯光设置，覆盖 HC 支持的全部
// 机型——各机型的 EC/HID/USB 协议由 HC 内部处理（SetLedColor 等实现）。
// 灯光是低频操作（用户改一次），故无需常驻宿主/租约/HTTP/恢复链；本进程
// 按需启动、调用完成后退出。legacy 不调用 HC Close；独立 HID 核心释放自己的句柄。
//
// 用法：
//   YeManLightSetter.exe --hc-assembly <HandheldCompanion.dll>
//     --action color --level solid|breathing|rainbow|wave|wheel|gradient|ambilight|preset
//     [--r 255 --g 0 --b 0] [--r2 .. --g2 .. --b2 ..] [--speed 0-100]
//   YeManLightSetter.exe --hc-assembly <..> --action preset --preset-id <HC NameKey>
//   YeManLightSetter.exe --hc-assembly <..> --action brightness --brightness 0-100
//   YeManLightSetter.exe --hc-assembly <..> --action status --status on|off
// 独立 HID 核心：--device-core-assembly <YeManLightDeviceCore.dll>
//   --expected-device-class <精确机型类> [--device-core-sha256 <内置 pin 的一致性检查>]
//   X1Mini color solid/rainbow、brightness 在 HC 初始化前分派。
//   未覆盖能力可显式携带 --hc-assembly 走旧适配；传输失败绝不隐式重试。
// HC profile：--hc-profile legacy|ymcc-device-core-v1（新 profile 等待主控 pin）
// 可选：--hc-runtime-root <HC 运行时目录>（默认取 dll 所在目录）
//       --timeout-ms <IsReady 等待上限，默认 15000>
//
// 输出：单行 JSON（{ok:true,result:{...}} 或 {ok:false,error:"..."}），
// 退出码 0=成功 / 1=失败。调用方可据此记录 native 日志。
//
// 复用要素（与 YeManFanHost 同构）：
//   - HC 加载 + SHA-256 校验（ExpectedHcSha256，与 FanHost 同一锁定值）
//   - 依赖解析 ResolveDependency / 运行时配置 ConfigureHcRuntime
//   - 设备激活 WaitForHcDeviceReadyBeforeOpen（IsReady 轮询）+ OpenHcDevice
//   - LEDLevel 位值（HandheldCompanion.Utils.DeviceUtils.cs:20-31, [Flags]）
using YeManFanBackend.Capabilities;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    // 与 YeManFanHost 同源的 HC 程序集锁定哈希（candidate 0.32.4.0-06c0b954；
    // 920 v1.15 §26.3 重基线到获准候选 0c5132a9…（FAN-936 单一闭包：ASUS + MSI），
    // native 硬编码的 PowerControl\handheldcompanion-runtime\HC-CANDIDATE-… 目录将携带该 DLL）。
    private const string ExpectedHcSha256 =
        "0c5132a9d13aebfc5add2aa7c9ac54e8daaa8ebc816a0c68d099bb9685dc2e49";

    // Awaiting main-controller derivative identity/pin. Empty means disabled;
    // neither a caller hash nor a runtime manifest may enable this profile.
    private const string ExpectedYmccHcCoreSha256 = "f79edd9dc1c60d5b5e9f3c456174b3c049ea6087ea0c988facd95bc1c174b93c";
    private const string ExpectedYmccHcCoreAssemblyName = "HandheldCompanion";

    private static string HcPinForProfile(string profile) => profile switch
    {
        "legacy" => ExpectedHcSha256,
        "ymcc-device-core-v1" when ExpectedYmccHcCoreSha256.Length == 64 && ExpectedYmccHcCoreAssemblyName.Length > 0 => ExpectedYmccHcCoreSha256,
        "ymcc-device-core-v1" => throw new InvalidOperationException("YMCC HC device-core profile has no approved pin yet"),
        _ => throw new ArgumentException($"unknown HC runtime profile {profile}"),
    };

    // LEDLevel（[Flags]）：None=0, SolidColor=1, Breathing=2, Rainbow=4,
    // Wave=8, Wheel=16, Gradient=32, Ambilight=64, LEDPreset=128。
    private const string Oxp3DeviceClass = "HandheldCompanion.Devices.OneXPlayer3";
    // Source identity only; this predicate never grants catalog availability.
    internal static bool IsSuperJoySourceModel(string? modelClass) =>
        modelClass is "HandheldCompanion.Devices.AYANEO3" or "HandheldCompanion.Devices.AYANEONEXT2";
    private static readonly Dictionary<string, int> LedLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solid"] = 1,
        ["breathing"] = 2,
        ["rainbow"] = 4,
        ["wave"] = 8,
        ["wheel"] = 16,
        ["gradient"] = 32,
        ["ambilight"] = 64,
        ["preset"] = 128,
    };

    private static Assembly? assembly;
    private static string? runtimeRoot;

    // AYANEO CEii 原生动效（呼吸/颜色校准）落地的两个运行期开关。
    // ayaNativeLighting 默认开启；置 false 时完全退回冻结 HC 的 SetLedColor，
    // 便于实机 A/B 对照与一键回滚。ayaPattern 是 MCU pattern 寄存器的取值，
    // 需由 aya-light-probe 在真机确认后固化（0x00 = 关闭动效）。
    private const byte DefaultAyaPattern = 0x01;
    private static byte ayaPattern = DefaultAyaPattern;
    private static bool ayaNativeLighting = true;
    private static int ayaProbeDwellMs = 3000;
    // base+2 输出使能字节。0x80 是冻结实现 SetLedStatus(true) 的取值（该机型实测能亮），
    // 0xb1 是 Linux 驱动的 ENABLE_ON。缺这一字节 MCU 输出保持关闭，灯不亮。
    private static byte ayaEnable = AyaneoCeiiLighting.EnableStatic;
    private static string ayaProbeStage = "pattern";
    // 探针候选覆盖：null = 用内置候选。取证时按批次试，减少目视判读负担。
    private static byte[]? ayaProbeEnables;
    private static byte[]? ayaProbePatterns;
    private static byte[]? ayaProbeFades;
    // watchdog（base+0x15）候选。静态用 0x07，AYASpace 呼吸用 0x04；这里扫它是为了
    // 判断它到底是"锁帧"还是"动效存活时长"——前者恒常亮，后者决定动画能跑多久。
    private static byte[]? ayaProbeWatchdogs;
    // aux 探针：候选 (偏移, 值)。偏移相对左右组基址，用来验证 base+0/base+1
    // 是否为"全局亮度/总开关"——若是，呼吸只改 2 个字节就能做到，不必逐分区重写。
    private static List<(byte Offset, byte Value)>? ayaProbeAux;
    // 动效取证：四段动画步进与 fade。默认静态 + 无 fade（= 常亮），只在探针里改。
    private static byte[] ayaAnims = [AyaneoCeiiLighting.AnimStatic, AyaneoCeiiLighting.AnimStatic,
        AyaneoCeiiLighting.AnimStatic, AyaneoCeiiLighting.AnimStatic];
    private static byte ayaFade = AyaneoCeiiLighting.FadeOff;
    // 软件呼吸：AYASpace 3.2.0.4 的结论是 MCU 没有自主呼吸波形——全场只有 3 处
    // set_effect 调用，pattern 恒为 0xE7（那只是"动效/渐变模式"），真正的明暗循环
    // 由宿主逐帧推颜色。这里按同一思路实现：固件灯效程序下一次，之后只推颜色字节。
    private static int ayaBreatheMs;                     // 总时长，0 = 不做软件呼吸
    private static int ayaBreathePeriodMs = 4500;        // 一个完整明暗周期（实机调参：2s 偏快、3.5s 偏快、4.5s 刚好）
    private static int ayaBreatheFloorPercent = 12;      // 最暗保留百分比，避免"看起来像坏了"
    private static int ayaBreatheFramesPerPeriod = 60;   // 每个明暗周期至少推多少帧
    private static bool ayaBreatheBurst = true;          // 整组一次握手 + 末尾提交（高帧率）
    private static int ayaBreatheSettleMs = 4;           // 批量提交后的收敛等待
    // 父进程 PID。常驻呼吸由面板拉起，面板一旦消失（崩溃/被结束）本进程必须自行
    // 退出，否则会留下一个永远在呼吸的孤儿。0 = 不监视。
    private static int ayaParentPid;
    // 分区动画步进（zone+0x10）。null = 不下发，保持历史字节序列不变。
    private static byte[]? ayaZoneAnims;
    // 把首条配方重复下发 N 次。用来验证"MCU 动效需要定期保活"：AYASpace 每 13 秒
    // 重发一次同一帧，只看单帧往往只截到半个周期。
    private static int ayaProbeRepeat = 1;
    // 每步换颜色做目视标签（默认开）。关掉则全程用 --r/--g/--b 的基色。
    private static bool ayaProbePalette = true;
    // 探针每步下发后是否交出控制权（0x87=0x00）。交出 = 交回固件（实测固件默认是蓝色），
    // 只有需要观察"固件是否接手"时才开。
    private static bool ayaProbeRelease;
    // watchdog（base+0x15）取值。驱动固定 0x07；置 0 用来验证它是否为"锁住最后一帧"。
    private static byte ayaWatchdog = AyaneoCeiiLighting.WatchdogOn;

    // 实机取证输出：探针/回读同时写 stderr 与 %LOCALAPPDATA%\YeManCC\aya-light-probe.log。
    // 生产里本进程由主程序以管理员身份拉起，stderr 被父进程管道收走，落文件才能在
    // 无人值守时复核；日志第一行固定记录提权状态——EC 端口只有管理员能开，
    // 非管理员时所有 EC 读写都会"静默成功但不生效"。
    private static readonly object diagnosticGate = new();
    private static string? diagnosticLog;

    private static void Diagnostic(string line)
    {
        Console.Error.WriteLine(line);
        try
        {
            lock (diagnosticGate)
            {
                diagnosticLog ??= Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "YeManCC", "aya-light-probe.log");
                var directory = Path.GetDirectoryName(diagnosticLog);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(diagnosticLog, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // 提权状态：EC 端口（PawnIO / Super I/O）只对管理员开放，非管理员时
    // HC 的 ECRamDirectWrite 会静默空转并返回 true。诊断必须先把这件事说清楚。
    private static string ElevationNote()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            var admin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            return $"elevated={(admin ? "true" : "false")} user={identity.Name}";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return "elevated=unknown";
        }
    }

    // HC 设备初始化/灯光链路会构造 WPF 亲和对象（System.Windows/Media 等），
    // 非 STA 线程会抛"调用线程必须为 STA，因为许多 UI 组件都需要。"
    // （2026-09-16 实机复现；FanHost 用常驻 STA dispatcher 线程解决，本进程
    //  是一次性短命进程，直接让主线程 STA 即可——同 [STAThread]，零新线程。）
    [STAThread]
    private static int Main(string[] args)
    {
        YeManCC.ProcessEfficiency.ApplyCurrentProcessOnce();
        // 取证动作把最终结果也落到诊断日志：提权子进程的 stdout/stderr 被父进程
        // 管道收走，无人值守时只能靠文件复核（异常路径尤其需要）。
        var diagnosticRun = args.Contains("aya-light-probe") || args.Contains("aya-light-readback") ||
                            args.Contains("--aya-breathe-ms");
        try
        {
            var result = Run(args);
            // 退出码必须反映设备侧真实结果：Run 把 HC 各方法返回 false 收敛为
            // note != "ok"（setLedColor-false / setLedBrightness-false /
            // off-partial 等）而不抛异常。原实现恒返回 0，上层
            // ymccLightSetterApply 以 exitCode==0 判成功时会对失败误报 ok。
            var note = result.TryGetValue("note", out var value) ? value as string : null;
            var exitCode = ExitCodeForNote(note);
            var json = JsonSerializer.Serialize(new { ok = exitCode == 0, result });
            Console.WriteLine(json);
            if (diagnosticRun) Diagnostic("[aya-result] " + json);
            return exitCode;
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.GetBaseException().Message,
                dependencyFailures = YeMan.SharedRuntime.RuntimeDependencyContract.DescribeFailure(ex, "lighting.execute"),
            });
            Console.WriteLine(json);
            if (diagnosticRun) Diagnostic($"[aya-fatal] {ElevationNote()} {json}");
            return 1;
        }
    }

    private static Dictionary<string, object?> Run(string[] args) => RunWithSuperJoyPorts(args,
        (identity, owner, stop) => SuperJoyWindowsHidTransport.Open(identity, () => owner.IsCurrent, owner.Token, stop),
        (root, stop) => AyaneoCeWindowsEcTransport.Open(root, stop));

    // Same parser, sole catalog, owner acquire and dispatch as the shipping entry.
    // Only internal software fixtures may supply terminal factories; no CLI bypass.
    internal static Dictionary<string, object?> RunWithSuperJoyPorts(string[] args,
        Func<SuperJoyIdentity, SuperJoyOwnerLease, CancellationToken, ISuperJoyLightingTransport> openHid,
        Func<string, CancellationToken, IAyaneoCeLightingEc> openEc,
        Func<SuperJoyIdentity, SuperJoyOwnerLease, CancellationToken, IApexLightingTransport>? openApex = null,
        Func<LightingOemOwnershipCapability, string, Assembly, CancellationToken, IAyaneoOemRestoreIo>? openOem = null)
    {
        string? hcPath = null, runtimeRootOpt = null, action = null, level = null, statusArg = null, presetId = null, expectedDeviceClass = null;
        string? corePath = null, coreCallerHash = null, ownerInterfacePath = null, ownerGeneration = null;
        var hcProfile = "legacy";
        int? r = null, g = null, b = null, r2 = null, g2 = null, b2 = null;
        int? brightness = null, speed = null;
        var timeoutMs = 15_000;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {current}");
                return args[++i];
            }
            switch (current)
            {
                case "--hc-assembly": hcPath = Next(); break;
                case "--hc-profile": hcProfile = Next(); break;
                case "--device-core-assembly": corePath = Next(); break;
                case "--device-core-sha256": coreCallerHash = Next(); break;
                case "--hc-runtime-root": runtimeRootOpt = Next(); break;
                case "--action": action = Next(); break;
                case "--level": level = Next(); break;
                case "--preset-id": presetId = Next(); break;
                case "--expected-device-class": expectedDeviceClass = Next(); break;
                case "--owner-interface-path": ownerInterfacePath = Next(); break;
                case "--owner-generation": ownerGeneration = Next(); break;
                case "--r": r = int.Parse(Next()); break;
                case "--g": g = int.Parse(Next()); break;
                case "--b": b = int.Parse(Next()); break;
                case "--r2": r2 = int.Parse(Next()); break;
                case "--g2": g2 = int.Parse(Next()); break;
                case "--b2": b2 = int.Parse(Next()); break;
                case "--brightness": brightness = int.Parse(Next()); break;
                case "--speed": speed = int.Parse(Next()); break;
                case "--status": statusArg = Next(); break;
                case "--timeout-ms": timeoutMs = int.Parse(Next()); break;
                // AYANEO CEii 原生灯光：实机 A/B 与 pattern 取证开关。
                case "--aya-native-lighting":
                    ayaNativeLighting = Next() switch
                    {
                        "1" or "on" or "true" => true,
                        "0" or "off" or "false" => false,
                        var other => throw new ArgumentException($"invalid --aya-native-lighting {other}"),
                    };
                    break;
                case "--aya-pattern":
                {
                    var value = int.Parse(Next());
                    if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(args), "--aya-pattern must be 0-255");
                    ayaPattern = (byte)value;
                    break;
                }
                case "--dwell-ms":
                    ayaProbeDwellMs = Math.Clamp(int.Parse(Next()), 500, 15000);
                    break;
                case "--aya-enable-bit":
                {
                    var value = int.Parse(Next());
                    if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(args), "--aya-enable-bit must be 0-255");
                    ayaEnable = (byte)value;
                    break;
                }
                case "--aya-stage":
                {
                    var stage = Next();
                    if (stage is not ("pattern" or "enable" or "both" or "recipe" or "fade" or "aux" or "breathe" or "softbreathe"))
                        throw new ArgumentException($"invalid --aya-stage {stage}");
                    ayaProbeStage = stage;
                    break;
                }
                case "--aya-enables":
                {
                    // 逗号分隔，十进制或 0x 十六进制；拒绝空列表与越界值。
                    var parsed = ParseByteList(Next(), "--aya-enables");
                    ayaProbeEnables = parsed;
                    break;
                }
                case "--aya-patterns":
                {
                    var parsed = ParseByteList(Next(), "--aya-patterns");
                    ayaProbePatterns = parsed;
                    break;
                }
                case "--aya-fades":
                {
                    // 单独扫 fade（pattern 保持关闭），用来判定 fade 自身是否就是呼吸周期。
                    var parsed = ParseByteList(Next(), "--aya-fades");
                    ayaProbeFades = parsed;
                    break;
                }
                case "--aya-watchdogs":
                {
                    // 扫 watchdog（base+0x15），配合 pattern=0xE7 判定它是否为动效存活时长。
                    ayaProbeWatchdogs = ParseByteList(Next(), "--aya-watchdogs");
                    break;
                }
                case "--aya-aux":
                {
                    // 形如 "1:0xff,1:0x40,0:0x01"：偏移相对基址，值支持十六进制。
                    ayaProbeAux = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(item =>
                        {
                            var pair = item.Split(':', StringSplitOptions.TrimEntries);
                            if (pair.Length != 2) throw new ArgumentException($"invalid --aya-aux item {item}");
                            return (ParseByte(pair[0], "--aya-aux"), ParseByte(pair[1], "--aya-aux"));
                        })
                        .ToList();
                    break;
                }
                case "--aya-anims":
                {
                    // 四段动画步进（base+0x11..0x14）。顺序即播放顺序。
                    var parsed = ParseByteList(Next(), "--aya-anims");
                    if (parsed.Length != 4) throw new ArgumentException("--aya-anims requires exactly four values");
                    ayaAnims = parsed;
                    break;
                }
                case "--aya-zone-anims":
                {
                    // 四段分区动画步进（zone+0x10，即 0x13/0x16/0x19/0x1c）。
                    // AYASpace 实际写的是这一组，而不是 base+0x11..0x14。
                    var parsed = ParseByteList(Next(), "--aya-zone-anims");
                    if (parsed.Length != 4)
                        throw new ArgumentException("--aya-zone-anims requires exactly four values");
                    ayaZoneAnims = parsed;
                    break;
                }
                case "--aya-repeat":
                {
                    if (!int.TryParse(Next(), out var repeat) || repeat < 1 || repeat > 600)
                        throw new ArgumentException("--aya-repeat requires 1-600");
                    ayaProbeRepeat = repeat;
                    break;
                }
                case "--aya-peak-scale":
                {
                    // 全局颜色上限（默认 240）。抬高到 255 即用满 Linux 驱动允许的
                    // 64/32 量程，整体更亮且左右比例不变。
                    if (!int.TryParse(Next(), out var scale) || scale < 32 || scale > 255)
                        throw new ArgumentException("--aya-peak-scale requires 32-255");
                    AyaneoCeiiLighting.GlobalColorCeiling = scale;
                    break;
                }
                case "--aya-parent-pid":
                {
                    if (!int.TryParse(Next(), out var parentPid) || parentPid < 0)
                        throw new ArgumentException("--aya-parent-pid requires a non-negative pid");
                    ayaParentPid = parentPid;
                    break;
                }
                case "--aya-breathe-ms":
                {
                    if (!int.TryParse(Next(), out var ms) || ms < 0 || ms > 24 * 3600 * 1000)
                        throw new ArgumentException("--aya-breathe-ms requires 0-86400000");
                    ayaBreatheMs = ms;
                    break;
                }
                case "--aya-breathe-period-ms":
                {
                    if (!int.TryParse(Next(), out var period) || period < 400 || period > 60000)
                        throw new ArgumentException("--aya-breathe-period-ms requires 400-60000");
                    ayaBreathePeriodMs = period;
                    break;
                }
                case "--aya-breathe-floor":
                {
                    if (!int.TryParse(Next(), out var floor) || floor < 0 || floor > 90)
                        throw new ArgumentException("--aya-breathe-floor requires 0-90");
                    ayaBreatheFloorPercent = floor;
                    break;
                }
                case "--aya-breathe-frames":
                {
                    if (!int.TryParse(Next(), out var frames) || frames < 4 || frames > 240)
                        throw new ArgumentException("--aya-breathe-frames requires 4-240");
                    ayaBreatheFramesPerPeriod = frames;
                    break;
                }
                case "--aya-breathe-burst":
                    ayaBreatheBurst = Next() switch
                    {
                        "1" or "on" or "true" => true,
                        "0" or "off" or "false" => false,
                        var other => throw new ArgumentException($"invalid --aya-breathe-burst {other}"),
                    };
                    break;
                case "--aya-fade":
                    ayaFade = ParseByte(Next(), "--aya-fade");
                    break;
                case "--aya-watchdog":
                    ayaWatchdog = ParseByte(Next(), "--aya-watchdog");
                    break;
                case "--aya-palette":
                    ayaProbePalette = Next() switch
                    {
                        "1" or "on" or "true" => true,
                        "0" or "off" or "false" => false,
                        var other => throw new ArgumentException($"invalid --aya-palette {other}"),
                    };
                    break;
                case "--aya-release":
                    ayaProbeRelease = Next() switch
                    {
                        "1" or "on" or "true" => true,
                        "0" or "off" or "false" => false,
                        var other => throw new ArgumentException($"invalid --aya-release {other}"),
                    };
                    break;
                default: throw new ArgumentException($"unknown arg {current}");
            }
        }

        ValidateRequest(action, level, statusArg, presetId);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        if (action == "restore-oem" && (hcProfile != "ymcc-device-core-v1" || level is not null ||
            statusArg is not null || presetId is not null || brightness is not null || speed is not null ||
            r is not null || g is not null || b is not null || r2 is not null || g2 is not null || b2 is not null))
            throw new ArgumentException("OEM_LIGHTING_REQUIRES_APPROVED_PROFILE_AND_EXCLUSIVE_ACTION");
        // Dispatch before legacy HC. Catalog admission remains closed until the
        // actual native selector/lifecycle is wired; caller arguments cannot grant it.
        if (IsSuperJoySourceModel(expectedDeviceClass))
            return ExecuteSuperJoyLighting(expectedDeviceClass!, action!, level, statusArg,
                r, g, b, r2, g2, b2, brightness, presetId, timeoutMs,
                runtimeRootOpt, ownerInterfacePath, ownerGeneration, openHid, openEc);
        if (string.Equals(expectedDeviceClass, ApexLightingConsumer.Model, StringComparison.Ordinal))
            return ExecuteApexLighting(action!, level, r, g, b, r2, g2, b2, speed, brightness, presetId,
                ownerInterfacePath, ownerGeneration, timeoutMs,
                openApex ?? ((identity, owner, stop) => ApexWindowsHidTransport.Open(identity, () => owner.IsCurrent, stop)));
        if (string.Equals(expectedDeviceClass, Oxp3DeviceClass, StringComparison.Ordinal))
            return ExecuteOxp3Lighting(action!, level, r, g, b, r2, g2, b2, brightness, speed, presetId,
                timeoutMs, ownerInterfacePath, ownerGeneration);
        if (expectedDeviceClass == DeviceCoreConsumer.X2MiniPro && corePath is null)
            throw new NotSupportedException("X2 Mini Pro requires the pinned independent device core and native owner");
        if (corePath is not null)
        {
            var coreResult = DeviceCoreConsumer.TryExecute(corePath, coreCallerHash,
                expectedDeviceClass, action!, level, new
                {
                    Action = action, Level = level, R = r, G = g, B = b,
                    R2 = r2, G2 = g2, B2 = b2, Brightness = brightness, Speed = speed, PresetId = presetId,
                }, ownerInterfacePath, ownerGeneration, timeoutMs);
            if (coreResult is not null) return coreResult;
            if (string.IsNullOrWhiteSpace(hcPath))
                throw new NotSupportedException("device-core capability unavailable; explicit --hc-assembly legacy adapter required");
        }
        else if (coreCallerHash is not null)
            throw new ArgumentException("--device-core-sha256 requires --device-core-assembly");
        if (string.IsNullOrWhiteSpace(hcPath)) throw new ArgumentException("--hc-assembly or --device-core-assembly is required");
        var expectedHcHash = HcPinForProfile(hcProfile);

        // ---- HC 加载（FanHost LoadAssemblyAndFactory 同构）----
        var fullPath = Path.GetFullPath(hcPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("HandheldCompanion.dll not found", fullPath);
        var actualHash = Sha256(fullPath);
        if (!actualHash.Equals(expectedHcHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"HC assembly SHA-256 mismatch: {actualHash}");
        var assemblyDirectory = Path.GetDirectoryName(fullPath)!;
        runtimeRoot = Path.GetFullPath(runtimeRootOpt ?? assemblyDirectory);
        AssemblyLoadContext.Default.Resolving += ResolveDependency;
        assembly = YeMan.SharedRuntime.RuntimeDependencyContract.LoadPinned(
            AssemblyLoadContext.Default, new AssemblyName(ExpectedYmccHcCoreAssemblyName), fullPath, expectedHcHash);
        if (hcProfile != "legacy" && assembly.GetName().Name != ExpectedYmccHcCoreAssemblyName)
            throw new InvalidOperationException("unexpected YMCC HC device-core assembly identity");
        ConfigureHcRuntime();

        // ---- 设备获取 + 激活（FanHost OpenHcDevice/WaitForHcDeviceReadyBeforeOpen 同构）----
        var deviceType = assembly.GetType("HandheldCompanion.Devices.IDevice", throwOnError: true)!;
        var device = deviceType
            .GetMethod("GetCurrent", BindingFlags.Public | BindingFlags.Static)?
            .Invoke(null, null)
            ?? throw new InvalidOperationException("HC IDevice.GetCurrent returned null");
        // aya-light-probe 是实机取证用的诊断动作，不是产品操作：它只是沿常亮通道
        // 试写 pattern 寄存器，因此按 color/solid 的能力门准入，不新增目录条目。
        var diagnostic = action is "aya-light-probe" or "aya-light-readback" or "aya-light-breathe";
        var admissionAction = diagnostic ? "color" : action!;
        var admissionLevel = diagnostic ? "solid" : level;
        var capability = RequireCatalogLightingOperation(device.GetType(), expectedDeviceClass,
            admissionAction, admissionLevel, brightness is not null, presetId);
        if (action == "restore-oem")
        {
            var restored = ExecuteAyaneoOemRestore(device.GetType(), expectedDeviceClass, runtimeRoot,
                assembly, timeoutMs, openOem ?? AyaneoOemRestoreWindowsPorts.Open);
            restored["hcSha256"] = actualHash;
            restored["backend"] = "ymcc-hc-device-core";
            return restored;
        }
        ValidateAyaneoCeRequest(device.GetType(), action!, level, brightness, r, g, b);
        Invoke(device, "Initialize", false, false);
        var ready = WaitReady(device, timeoutMs);
        using var oneXLighting = OneXLightingSession.Prepare(device);
        if (!OpenLightingDevice(device))
            throw new InvalidOperationException("HC device Open returned false");

        // ---- 灯光调用（IDevice.cs:1036-1041）----
        // action 本体已提为私有纯调用函数 ExecuteLedAction，使离线 fake harness
        // 能直接执行真实业务代码（而非复刻一份）。Run 只负责加载/激活与结果封装。
        var deviceRuntimeType = device.GetType();
        // 诊断动作不走产品事务：它直接按常亮通道试写 pattern 寄存器并回读灯效，
        // 因此不经过 OneX 队列、也不产生可被当成"已生效"的写入回执。
        string note;
        bool managed = false, completed = false;
        var writeTasks = 0;
        var failedTasks = 0;
        if (action == "aya-light-readback")
        {
            note = ExecuteAyaneoCeiiReadback(device, deviceRuntimeType);
        }
        else if (action == "aya-light-breathe")
        {
            // 常驻呼吸：本进程会一直跑到被父进程杀掉（或父进程消失）为止，
            // 因此不产生"写入回执"，也不进入 OneX 队列。
            note = ExecuteAyaneoCeiiBreathing(device, deviceRuntimeType, r, g, b, r2, g2, b2,
                Clamp(brightness ?? 100));
        }
        else if (action == "aya-light-probe")
        {
            note = ExecuteAyaneoCeiiProbe(device, deviceRuntimeType, r, g, b, Clamp(brightness ?? 80));
        }
        else
        {
            var transaction = OneXQueuedLighting.Execute(device, () => ExecuteResolvedCatalogLedAction(device, deviceRuntimeType,
                capability, statusArg, r, g, b, r2, g2, b2, brightness, speed, presetId), preset: action == "preset");
            note = transaction.Note;
            managed = transaction.Managed;
            completed = transaction.Completed;
            writeTasks = transaction.WriteTasks;
            failedTasks = transaction.FailedTasks;
        }

        return new Dictionary<string, object?>
        {
            ["deviceClass"] = device.GetType().FullName,
            ["capabilityId"] = capability.Device.DeviceId,
            ["capabilityOperation"] = capability.OperationId,
            ["capabilityCatalogSha256"] = HcLightingCapabilityCatalog.CatalogSha256,
            ["hcSha256"] = actualHash,
            ["sharedDependencyBindings"] = YeMan.SharedRuntime.DeviceCoreSharedDependencies.Snapshot(),
            ["backend"] = hcProfile == "legacy" ? "legacy-hc" : "ymcc-hc-device-core",
            ["hcProfile"] = hcProfile,
            ["ready"] = ready,
            ["opened"] = true,
            ["action"] = action,
            ["note"] = note,
            ["presetId"] = presetId,
            ["queuedWriteManaged"] = managed,
            ["queuedWritesCompleted"] = completed,
            ["queuedWriteTasks"] = writeTasks,
            ["queuedWriteFailures"] = failedTasks,
            ["colorSequencePolicy"] = UsesAokzoeBrightnessFirst(deviceRuntimeType, action, brightness)
                ? "brightness-settle-color" : "existing",
            ["colorSequenceDelayMs"] = UsesAokzoeBrightnessFirst(deviceRuntimeType, action, brightness)
                ? AokzoeColorSettleMs : 0,
            ["physicalEffectVerified"] = false,
        };
    }

    private static Dictionary<string, object?> ExecuteApexLighting(string action, string? level,
        int? r, int? g, int? b, int? r2, int? g2, int? b2, int? speed, int? brightness, string? presetId,
        string? ownerPath, string? ownerToken, int timeoutMs,
        Func<SuperJoyIdentity, SuperJoyOwnerLease, CancellationToken, IApexLightingTransport> open)
    {
        // Require the same generated capability policy as native admission,
        // before acquiring the lease or opening any device.
        var operation = HcLightingCapabilityCatalog.Require(ApexLightingConsumer.Model, ApexLightingConsumer.Model,
            action, level, brightness is not null, presetId);
        if (operation.Capabilities.SourceAdapter != LightingSourceAdapter.ApexHidV2 ||
            operation.PreferredProvider != "onex-apex-hid-v2")
            throw new InvalidOperationException("APEX_LIGHTING_ROUTE_NOT_CONNECTED");
        // Existing callers may repeat the primary color and the default speed.
        // A different secondary color or a custom speed has no V2 consumer.
        ValidateApexOptionalArguments(action, r, g, b, r2, g2, b2, speed);
        var request = ApexLightingRequest.Create(action, level, r, g, b, brightness);
        if (string.IsNullOrWhiteSpace(ownerPath) || string.IsNullOrWhiteSpace(ownerToken))
            throw new ArgumentException("APEX_NATIVE_OWNER_REQUIRED");
        var parts = SuperJoyOwnerLease.ParseToken(ownerToken);
        var identity = ApexLightingConsumer.OwnerIdentity(ownerPath, checked((long)parts[2]));
        using var deadline = new CancellationTokenSource(timeoutMs);
        using var owner = SuperJoyOwnerLease.Acquire(identity, ownerToken, deadline.Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, deadline.Token);
        var result = ApexLightingConsumer.Apply(identity, request, () => owner.IsCurrent, stop.Token,
            () => open(identity, owner, stop.Token));
        return new()
        {
            ["deviceClass"] = ApexLightingConsumer.Model, ["capabilityId"] = operation.Device.DeviceId,
            ["capabilityOperation"] = operation.OperationId,
            ["capabilityCatalogSha256"] = HcLightingCapabilityCatalog.CatalogSha256,
            ["backend"] = "onex-apex-hid-v2", ["action"] = action,
            ["note"] = result.Completed ? "ok" : result.Reason,
            ["protocolResult"] = result.Reason, ["queuedWritesCompleted"] = result.Completed,
            ["hidWrites"] = result.Writes, ["released"] = result.Released,
            ["ownerAdmission"] = "native-owner-handoff-v2", ["ownerGeneration"] = identity.Generation,
            ["hcSha256"] = "", ["physicalEffectVerified"] = false,
        };
    }

    internal static void ValidateApexOptionalArguments(string action, int? r, int? g, int? b,
        int? r2, int? g2, int? b2, int? speed)
    {
        if (speed is not null && speed != 50) throw new NotSupportedException("APEX_FIXED_SPEED");
        if (r2 is not null || g2 is not null || b2 is not null)
        {
            if (action != "color" || r2 != r || g2 != g || b2 != b || r is null || g is null || b is null)
                throw new NotSupportedException("APEX_SECOND_COLOR_UNAVAILABLE");
        }
    }

    private static Dictionary<string, object?> ExecuteSuperJoyLighting(string model, string action,
        string? level, string? status, int? r, int? g, int? b, int? r2, int? g2, int? b2,
        int? brightness, string? presetId, int timeoutMs, string? suppliedRuntime,
        string? ownerPath, string? ownerToken,
        Func<SuperJoyIdentity, SuperJoyOwnerLease, CancellationToken, ISuperJoyLightingTransport> openHid,
        Func<string, CancellationToken, IAyaneoCeLightingEc> openEc)
    {
        // A source model or valid token is not runtime catalog availability.
        if (!HcLightingCapabilityCatalog.IsReviewedExtension(model))
            throw new NotSupportedException("SUPERJOY_LIGHTING_ROUTE_NOT_CONNECTED: " + model);
        var operation = HcLightingCapabilityCatalog.Require(model, model, action, level, brightness is not null, presetId);
        if (operation.Capabilities.SourceAdapter != LightingSourceAdapter.SuperJoyCec || operation.PreferredProvider != "superjoy-cec-v1")
            throw new InvalidOperationException("SUPERJOY_PROVIDER_IDENTITY_MISMATCH");
        var request = AyaneoSuperJoyRequest.Create(model, action, level, status, r, g, b, r2, g2, b2, brightness);
        if (string.IsNullOrWhiteSpace(ownerPath) || string.IsNullOrWhiteSpace(ownerToken) || string.IsNullOrWhiteSpace(suppliedRuntime))
            throw new ArgumentException("SUPERJOY_NATIVE_OWNER_AND_RUNTIME_REQUIRED");
        var parts = SuperJoyOwnerLease.ParseToken(ownerToken);
        var identity = new SuperJoyIdentity(model, ownerPath, checked((long)parts[2]));
        using var deadline = new CancellationTokenSource(timeoutMs);
        using var owner = SuperJoyOwnerLease.Acquire(identity, ownerToken, deadline.Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, deadline.Token);
        runtimeRoot = Path.GetFullPath(suppliedRuntime);
        AssemblyLoadContext.Default.Resolving += ResolveDependency;
        var result = AyaneoSuperJoyConsumer.Apply(identity, request, () => owner.IsCurrent, stop.Token,
            () => openHid(identity, owner, stop.Token),
            () => openEc(runtimeRoot, stop.Token));
        return new()
        {
            ["deviceClass"] = model, ["capabilityId"] = operation.Device.DeviceId,
            ["capabilityOperation"] = operation.OperationId,
            ["capabilityCatalogSha256"] = HcLightingCapabilityCatalog.CatalogSha256,
            ["backend"] = "superjoy-cec-v1", ["action"] = action,
            ["note"] = result.Completed ? "ok" : result.Reason,
            ["protocolResult"] = result.Reason,
            ["queuedWritesCompleted"] = result.Completed,
            ["hidWrites"] = result.HidWrites, ["hidReads"] = result.HidReads,
            ["ecWrites"] = result.EcWrites, ["handbackAttempted"] = result.HandbackAttempted,
            ["handbackCompleted"] = result.HandbackCompleted,
            ["ownerAdmission"] = "native-owner-handoff-v2", ["ownerGeneration"] = identity.Generation,
            ["hcSha256"] = "", ["physicalEffectVerified"] = false,
        };
    }

    // The pinned HC assembly predates OXP3 lighting. This path performs only the
    // reviewed source-derived HID v1 writes and never constructs or replaces an
    // HC device, WMI EC, serial port or controller reader.
    private static Dictionary<string, object?> ExecuteOxp3Lighting(string action, string? level,
        int? r, int? g, int? b, int? r2, int? g2, int? b2, int? brightness, int? speed, string? presetId,
        int timeoutMs, string? ownerInterfacePath, string? ownerGeneration) =>
        ExecuteOxp3LightingWith(action, level, r, g, b, r2, g2, b2, brightness, speed, presetId,
            timeoutMs, ownerInterfacePath, ownerGeneration,
            (path, token) => Oxp3WindowsHidTransport.Open(path, token));

    internal static Dictionary<string, object?> ExecuteOxp3LightingWith(string action, string? level,
        int? r, int? g, int? b, int? r2, int? g2, int? b2, int? brightness, int? speed, string? presetId,
        int timeoutMs, string? ownerInterfacePath, string? ownerGeneration,
        Func<string?, CancellationToken, IOxp3HidTransport> open)
    {
        if (string.IsNullOrWhiteSpace(ownerInterfacePath) || string.IsNullOrWhiteSpace(ownerGeneration))
            throw new ArgumentException("OXP3 requires --owner-interface-path and --owner-generation from the native owner");
        using var deadline = new CancellationTokenSource(timeoutMs);
        // Admission and catalog validation happen before any device open.
        var operation = HcLightingCapabilityCatalog.Require(Oxp3DeviceClass, Oxp3DeviceClass, action,
            level, brightness is not null, presetId);
        using var lease = Oxp3OwnerLease.Acquire(ownerInterfacePath, ownerGeneration, deadline.Token);
        // The effective request deadline and owning-lease withdrawal both flow
        // into admission, device open, the sequence and its in-flight IO.
        using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lease.Token);
        var transport = open(ownerInterfacePath, operationToken.Token);
        try
        {
            lease.ValidateInterfacePath(transport.DevicePath);
            lease.EnsureCurrent();
            return ExecuteOxp3Core(operation, action, level, r, g, b, r2, g2, b2, brightness, speed, presetId,
                transport, operationToken.Token, lease);
        }
        finally
        {
            transport.Dispose();
        }
    }

    internal static Dictionary<string, object?> ExecuteOxp3Admitted(string action, string? level,
        int? r, int? g, int? b, int? r2, int? g2, int? b2, int? brightness, int? speed, string? presetId,
        IOxp3HidTransport transport, CancellationToken cancellationToken = default)
    {
        var operation = HcLightingCapabilityCatalog.Require(Oxp3DeviceClass, Oxp3DeviceClass, action,
            level, brightness is not null, presetId);
        return ExecuteOxp3Core(operation, action, level, r, g, b, r2, g2, b2, brightness, speed, presetId,
            transport, cancellationToken, null);
    }

    private static Dictionary<string, object?> ExecuteOxp3Core(LightingCapabilityOperation operation,
        string action, string? level, int? r, int? g, int? b, int? r2, int? g2, int? b2,
        int? brightness, int? speed, string? presetId, IOxp3HidTransport transport,
        CancellationToken cancellationToken, Oxp3OwnerLease? lease)
    {
        if (operation.Capabilities.SourceAdapter != LightingSourceAdapter.Oxp3Hid ||
            !HcLightingCapabilityCatalog.IsReviewedExtension(operation.Device.DeviceId))
            throw new InvalidOperationException("OXP3 lighting extension route is not registered");
        var main = new Oxp3Rgb((byte)Clamp(r ?? 255), (byte)Clamp(g ?? 0), (byte)Clamp(b ?? 0));
        var secondary = new Oxp3Rgb((byte)Clamp(r2 ?? r ?? 255), (byte)Clamp(g2 ?? g ?? 0), (byte)Clamp(b2 ?? b ?? 0));
        var presetValue = 0;
        if (action == "preset" && !HcLightingCapabilityCatalog.TryResolveExtensionPreset(operation.Device.DeviceId, presetId!, out presetValue))
            throw new ArgumentException("preset-id is absent for this HC device");
        Action? ownerCheck = lease is null ? null : lease.EnsureCurrent;
        var writer = new Oxp3LightingWriter(transport, cancellationToken: cancellationToken, ownerCheck: ownerCheck);
        var canceled = false;
        var ok = false;
        try
        {
            ok = action switch
            {
                "color" => writer.ApplyColor(main, secondary, string.Equals(operation.Level, "breathing", StringComparison.Ordinal), brightness ?? 100),
                "brightness" => writer.ApplyBrightness(brightness ?? 50, main, secondary),
                "off" => writer.ApplyOff(),
                "preset" => writer.ApplyPreset(presetValue, secondary, brightness ?? 100),
                _ => throw new ArgumentException($"unknown action {action}")
            };
        }
        catch (OperationCanceledException) { canceled = true; }
        var note = ok ? "ok" : lease?.WasRevoked == true ? "lighting-owner-revoked"
            : lease?.WasSuperseded == true ? "lighting-owner-generation-changed"
            : canceled ? "lighting-sequence-timeout" : action switch
        {
            "color" => "setLedColor-false",
            "brightness" => "setLedBrightness-false",
            "preset" => "setLEDPreset-false",
            _ => "off-partial"
        };
        return new Dictionary<string, object?>
        {
            ["deviceClass"] = Oxp3DeviceClass,
            ["capabilityId"] = operation.Device.DeviceId,
            ["capabilityOperation"] = operation.OperationId,
            ["capabilityCatalogSha256"] = HcLightingCapabilityCatalog.CatalogSha256,
            ["backend"] = "oxp3-pure-hid-v1",
            ["hcSha256"] = "",
            ["ready"] = true,
            ["opened"] = true,
            ["action"] = action,
            ["note"] = note,
            ["presetId"] = presetId,
            ["queuedWriteManaged"] = false,
            ["queuedWritesCompleted"] = ok,
            ["queuedWriteTasks"] = writer.Writes,
            ["queuedWriteFailures"] = writer.FailedWrites,
            ["physicalEffectVerified"] = false,
            ["fixedSpeed"] = true,
            ["speed"] = speed,
            ["speedApplied"] = false,
            ["ownerAdmission"] = lease is null ? "test-admitted" : "native-owner-handoff",
            ["ownerGeneration"] = lease?.Generation,
            ["ownerRevoked"] = lease?.WasRevoked ?? false,
            ["ownerGenerationChanged"] = lease?.WasSuperseded ?? false
        };
    }

    // Resolve once before initialization. The immutable admitted operation flows directly to
    // the actual action; runtime overrides are checked before any device IO.
    private static LightingCapabilityOperation RequireCatalogLightingOperation(Type actualType, string? expectedClass,
        string action, string? level, bool includesBrightness, string? presetId)
    {
        var operation = HcLightingCapabilityCatalog.Require(actualType.FullName, expectedClass, action, level, includesBrightness, presetId);
        ValidateOperationImplementation(actualType, action, includesBrightness);
        if (action == "off" && (HasRealLightingMethod(actualType, "SetLedBrightness") != operation.Capabilities.Brightness ||
            HasRealLightingMethod(actualType, "SetLedStatus") != (operation.Capabilities.StatusAvailable || operation.Capabilities.StatusTransportDisabled)))
            throw new NotSupportedException("LIGHTING_OFF_RUNTIME_CONTRACT_MISMATCH");
        return operation;
    }

    private static string ExecuteCatalogLedAction(object device, Type actualType, string? expectedClass,
        string action, string? level, string? statusArg, int? r, int? g, int? b, int? r2, int? g2, int? b2,
        int? brightness, int? speed, string? presetId)
    {
        var operation = RequireCatalogLightingOperation(actualType, expectedClass, action, level, brightness is not null, presetId);
        return ExecuteResolvedCatalogLedAction(device, actualType, operation, statusArg, r, g, b, r2, g2, b2, brightness, speed, presetId);
    }

    private static string ExecuteResolvedCatalogLedAction(object device, Type actualType, LightingCapabilityOperation operation,
        string? statusArg, int? r, int? g, int? b, int? r2, int? g2, int? b2, int? brightness, int? speed, string? presetId)
    {
        var action = operation.Action;
        if (action == "off")
        {
            OneXQueuedLighting.PrepareRequestCache(device, action, brightness, statusArg);
            return ExecuteLightingOff(device, actualType, operation.Capabilities.StatusAvailable, operation.Capabilities.Brightness);
        }
        return action == "preset" ? ExecuteLedPreset(device, actualType, presetId, brightness)
            : ExecuteLedAction(device, actualType, action, operation.Level, statusArg, r, g, b, r2, g2, b2, brightness, speed);
    }

    private static string ExecuteLightingOff(object device, Type type, bool statusWriter, bool brightnessWriter)
    {
        var contract = ResolveSetLedColorContract(type);
        var statusOk = !statusWriter || type.GetMethod("SetLedStatus")!.Invoke(device, [false]) is true;
        var brightnessOk = !brightnessWriter || type.GetMethod("SetLedBrightness")!.Invoke(device, [0]) is true;
        var black = CreateLedColor(contract.ColorType, 0, 0, 0);
        var colorOk = contract.SetColor.Invoke(device, [black, black, Enum.ToObject(contract.LedLevelType, LedLevels["solid"]), 0]) is true;
        return statusOk && brightnessOk && colorOk ? "ok" : "off-partial";
    }

    private static bool HasRealLightingMethod(Type actualType, string method) =>
        actualType.GetMethod(method, BindingFlags.Public | BindingFlags.Instance) is { } candidate &&
        candidate.DeclaringType?.FullName != "HandheldCompanion.Devices.IDevice";

    // These four catalog identities inherit A1's HID-v2 brightness/color setters.
    // This predicate selects sequencing only; catalog/runtime admission still runs first.
    private const int AokzoeColorSettleMs = 300;
    private static bool UsesAokzoeBrightnessFirst(Type type, string? action, int? brightness) =>
        action == "color" && brightness is > 0 && type.FullName is
            ("HandheldCompanion.Devices.AOKZOEA1" or "HandheldCompanion.Devices.AOKZOEA1Pro" or
             "HandheldCompanion.Devices.AOKZOEA1X" or "HandheldCompanion.Devices.AOKZOEA2");

    // 灯光 action 真实业务体（从 Run 的 switch 原样提取，逐行不变）。传入已
    // 准备的 device/deviceType 与现有 action/数值参数，返回现有 note；由 Run 在
    // 完成加载、hash/config/init/WaitReady/Open 后调用。提取目的是让离线 fake
    // harness 直接编译/链接同一份生产代码，而不是复刻 action 顺序。
    private static string ExecuteLedAction(
        object device, Type deviceType, string? action,
        string? level, string? statusArg,
        int? r, int? g, int? b, int? r2, int? g2, int? b2,
        int? brightness, int? speed)
    {
        ValidateRequest(action, level, statusArg, null);
        ValidateOperationImplementation(deviceType, action!, brightness is not null);
        var ayaneoCe = ValidateAyaneoCeRequest(deviceType, action!, level, brightness, r, g, b);
        var aokzoeBrightnessFirst = UsesAokzoeBrightnessFirst(deviceType, action, brightness);
        OneXQueuedLighting.PrepareRequestCache(device, action!, brightness, statusArg);
        var note = "ok";
        switch (action)
        {
            case "color":
            {
                var levelName = string.IsNullOrWhiteSpace(level) ? "solid" : level;
                if (string.Equals(levelName, "preset", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("preset requires --action preset and --preset-id");
                if (!LedLevels.TryGetValue(levelName, out var levelValue))
                    throw new ArgumentException($"unknown level {levelName}");
                // 签名驱动：level enum 与颜色类型都取自实际 SetLedColor 参数，
                // 不再用 "HandheldCompanion.Utils.LEDLevel" 全名字符串查找（该
                // 名称在当前 HC 中无定义，实际为 DeviceUtils+LEDLevel）。
                var contract = ResolveSetLedColorContract(deviceType);
                var enumValue = Enum.ToObject(contract.LedLevelType, levelValue);
                var main = CreateLedColor(contract.ColorType, Clamp(r ?? 255), Clamp(g ?? 0), Clamp(b ?? 0));
                var secondary = CreateLedColor(
                    contract.ColorType,
                    Clamp(r2 ?? r ?? 255), Clamp(g2 ?? g ?? 0), Clamp(b2 ?? b ?? 0));
                // AYANEO CEii：冻结实现的 switch 只处理 SolidColor，且用同一数值驱动
                // 左右两组灯（右侧真实量程只有左侧一半），logo 分区还按 B/R/G 下发。
                // 契约成立时改由本进程按 MCU 真实协议重新落地；常亮写入失败仍退回
                // 下面的 HC 调用，动效写入失败则如实报错而不是假成功。
                if (ayaneoCe)
                {
                    var nativeNote = TryApplyAyaneoCeiiColor(device, deviceType, levelValue,
                        r, g, b, r2, g2, b2, Clamp(brightness!.Value));
                    if (nativeNote is not null) return nativeNote;
                }
                // CEc/CEii apply brightness by scaling the RGB bytes. Their fresh
                // color setter returns true without retaining either color until
                // brightness is initialized, so prepare it before the first color.
                if (ayaneoCe && deviceType.GetMethod("SetLedBrightness")!.Invoke(device, [Clamp(brightness!.Value)]) is not true)
                    return "setLedBrightness-false";
                // A fresh helper cannot know whether the previous request disabled the lamp.
                // HID-v2 re-enable must settle before color/mode: HueSync7176803c
                // onex_led_device_hid.py:414,634-643 (also used by the exact APEX route).
                if (aokzoeBrightnessFirst)
                {
                    if (deviceType.GetMethod("SetLedBrightness")!.Invoke(device, [Clamp(brightness!.Value)]) is not true)
                        return "setLedBrightness-false";
                    Thread.Sleep(AokzoeColorSettleMs);
                }
                note = contract.SetColor.Invoke(device, [main, secondary, enumValue, speed ?? 100]) is bool ok && ok
                    ? "ok"
                    : "setLedColor-false";
                // 亮度随 color 一并设置（HC DevicePage 的亮度滑杆与效果同属一次
                // UpdateLED；0..100 映射由 HC 内部处理）。仅显式传入时写。
                if (brightness is not null && !ayaneoCe && !aokzoeBrightnessFirst)
                {
                    var setBrightness = deviceType.GetMethod("SetLedBrightness")
                        ?? throw new MissingMethodException(deviceType.FullName, "SetLedBrightness");
                    if (setBrightness.Invoke(device, [Clamp(brightness.Value)]) is not bool bOk || !bOk)
                        note = note == "ok" ? "setLedBrightness-false" : note;
                }
                if (ayaneoCe && note == "ok" &&
                    deviceType.GetMethod("SetLedStatus")!.Invoke(device, [brightness!.Value > 0]) is not true)
                    note = "setLedStatus-false";
                break;
            }
            case "brightness":
            {
                // A fresh CEc helper has no current colors to rescale. Only the
                // caller's explicit color context may supply those values.
                if (ayaneoCe)
                    return ExecuteCatalogLedAction(device, deviceType, deviceType.FullName, "color", level, statusArg,
                        r, g, b, r2, g2, b2, brightness ?? 50, speed, null);
                var setBrightness = deviceType.GetMethod("SetLedBrightness")
                    ?? throw new MissingMethodException(deviceType.FullName, "SetLedBrightness");
                note = setBrightness.Invoke(device, [Clamp(brightness ?? 50)]) is bool ok && ok
                    ? "ok"
                    : "setLedBrightness-false";
                break;
            }
            case "status":
            {
                var setStatus = deviceType.GetMethod("SetLedStatus")
                    ?? throw new MissingMethodException(deviceType.FullName, "SetLedStatus");
                note = setStatus.Invoke(device, [!string.Equals(statusArg, "off", StringComparison.OrdinalIgnoreCase)]) is bool ok && ok
                    ? "ok"
                    : "setLedStatus-false";
                break;
            }
            case "off":
                // Only real optional methods are called. The catalog route further suppresses
                // inherited writers whose transport is disabled for the exact model.
                note = ExecuteLightingOff(device, deviceType, HasRealLightingMethod(deviceType, "SetLedStatus"),
                    HasRealLightingMethod(deviceType, "SetLedBrightness"));
                break;
            case "aya-light-probe":
                note = ExecuteAyaneoCeiiProbe(device, deviceType, r, g, b, Clamp(brightness ?? 100));
                break;
            default:
                throw new ArgumentException($"unknown action {action}");
        }

        return note;
    }

    // ── AYANEO CEii 原生灯光（呼吸/颜色校准）────────────────────────────
    // 只在机型确实是 CEii（AIR Plus 家族）且 ECRamDirectWrite 契约成立时接管；
    // 其余一切情况返回 null，让调用方原样走冻结 HC 的既有路径。

    private static AyaneoCeiiLighting? TryCreateAyaneoCeiiWriter(object device, Type deviceType,
        bool allowProbeRegisters = false)
    {
        const string ownerName = "HandheldCompanion.Devices.AYANEO.AYANEODeviceCEii";
        Type? owner = deviceType;
        while (owner is not null && owner.FullName != ownerName) owner = owner.BaseType;
        if (owner is null) return null;
        var method = deviceType.GetMethod("ECRamDirectWrite", BindingFlags.Public | BindingFlags.Instance,
            null, [typeof(byte), typeof(byte), typeof(byte)], null);
        if (method is null || method.ReturnType != typeof(bool) || method.DeclaringType != owner) return null;
        var write = method.CreateDelegate<Func<byte, byte, byte, bool>>(device);
        // 白名单只放行灯光寄存器，反射拿到的方法不会把字节写到风扇/电源区。
        // 取证路径额外放行 base+0/base+1（IsProbeRegister），出货路径不放。
        var allowed = allowProbeRegisters
            ? (Func<byte, bool>)AyaneoCeiiLighting.IsProbeRegister
            : AyaneoCeiiLighting.IsKnownRegister;
        return new AyaneoCeiiLighting((address, data) => allowed(address) && write(address, data, 0xd1));
    }

    // 回读通道：用于确认写入是否真的落到 EC RAM，而不是"返回 true 但没落地"。
    private static Func<byte, byte, byte>? TryCreateAyaneoCeiiReader(object device, Type deviceType)
    {
        const string ownerName = "HandheldCompanion.Devices.AYANEO.AYANEODeviceCEii";
        Type? owner = deviceType;
        while (owner is not null && owner.FullName != ownerName) owner = owner.BaseType;
        if (owner is null) return null;
        var method = deviceType.GetMethod("ECRamDirectRead", BindingFlags.Public | BindingFlags.Instance,
            null, [typeof(byte), typeof(byte)], null);
        if (method is null || method.ReturnType != typeof(byte) || method.DeclaringType != owner) return null;
        return method.CreateDelegate<Func<byte, byte, byte>>(device);
    }

    // 逐寄存器回读灯光区，确认字节是否真的落地。诊断专用，不做任何写入。
    private static string ExecuteAyaneoCeiiReadback(object device, Type deviceType)
    {
        var read = TryCreateAyaneoCeiiReader(device, deviceType)
            ?? throw new NotSupportedException("AYANEO_CEII_NATIVE_READBACK_UNAVAILABLE");
        var lines = new List<string>();
        void Dump(string label, byte start, byte end)
        {
            var parts = new List<string>();
            for (var address = (int)start; address <= end; address++)
                parts.Add($"0x{address:x2}=0x{read((byte)address, 0xd1):x2}");
            lines.Add($"{label}: " + string.Join(' ', parts));
        }
        lines.Add($"mode: 0x87=0x{read(AyaneoCeiiLighting.ModeAddress, 0xd1):x2}");
        Dump("left ", 0xb2, 0xc5);
        Dump("right", 0x72, 0x85);
        // 已知一定非 0 的寄存器：EC 版本 0x00-0x04、风扇控制/占空比、充电旁路 0xd1。
        // 这些全 0 说明"回读"本身不成立，不能据此判断写入是否落地。
        Dump("misc ", 0x00, 0x08);
        lines.Add($"misc2: 0xc8=0x{read(0xc8, 0xd1):x2} 0xd1=0x{read(0xd1, 0xd1):x2}");
        lines.Add(ElevationNote());
        lines.Add(DescribeEcTransport(device, deviceType));
        lines.Add(DescribeAcpiEc(device, deviceType));
        foreach (var line in lines) Diagnostic("[aya-readback] " + line);
        return "ok";
    }

    // 逗号分隔的字节列表，十进制或 0x 十六进制；空列表与越界值直接拒绝。
    private static byte[] ParseByteList(string value, string option) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => ParseByte(item, option))
            .ToArray() is { Length: > 0 } parsed
            ? parsed
            : throw new ArgumentException($"{option} requires at least one value");

    private static byte ParseByte(string value, string option) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToByte(value[2..], 16)
            : byte.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    // 反射读一个实例字段（公有/私有都试），拿不到返回 null，绝不抛。
    private static object? ReadField(object? target, string name)
        => target?.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(target);

    // 判定"HC 的 EC 通道到底通不通"。EC 端口只对管理员开放：非管理员时
    // OpenLibSys 内部设备句柄为空，ReadIoPortByte 恒返回 0、WriteIoPortByte 空转，
    // 而 ECRamDirectWriteByte 仍然 return true —— 正是"报 ok 但灯不变"的机制。
    private static string DescribeEcTransport(object device, Type deviceType)
    {
        var useOpenLib = ReadField(device, "UseOpenLib");
        var isOpen = deviceType.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public)?.GetValue(device);
        var ols = deviceType.GetField("openLibSys", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (ols is null) return $"ec: UseOpenLib={useOpenLib} IsOpen={isOpen} openLibSys=null";
        var pio = ReadField(ols, "_pio");
        var ec = ReadField(ols, "_ec");
        var pawn = ReadField(pio, "_pawnIO");
        var handle = ReadField(pawn, "_handle");
        var loaded = pawn?.GetType().GetProperty("IsLoaded")?.GetValue(pawn);
        var entered = ReadField(ols, "_entered");
        return $"ec: UseOpenLib={useOpenLib} IsOpen={isOpen} pio={(pio is null ? "null" : "ok")} " +
               $"ec={(ec is null ? "null" : "ok")} pawnHandle={(handle is null ? "null" : "ok")} " +
               $"pawnLoaded={loaded} entered={entered}";
    }

    // 标准 ACPI EC（0x62/0x66，LHM 的 WindowsEmbeddedControllerIO）是否可读。
    // 这条通道与 0x4E/0x4F Super I/O 是两套机制：只要能读到非 0 值，就说明
    // 提权与端口访问都成立，问题只可能出在寄存器协议而不是权限。
    private static string DescribeAcpiEc(object device, Type deviceType)
    {
        var ols = deviceType.GetField("openLibSys", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        var ec = ReadField(ols, "_ec");
        if (ec is null) return "acpi: unavailable";
        var readByte = ec.GetType().GetMethod("ReadByte", BindingFlags.Instance | BindingFlags.Public,
            null, [typeof(byte)], null);
        if (readByte is null) return "acpi: ReadByte missing";
        var parts = new List<string>();
        foreach (var register in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x70, 0x71, 0x72, 0x7f, 0x86, 0x87, 0xb0, 0xb1, 0xb2, 0xbf, 0xc6 })
        {
            try { parts.Add($"0x{register:x2}=0x{(byte)readByte.Invoke(ec, [register])!:x2}"); }
            catch (TargetInvocationException ex) { parts.Add($"0x{register:x2}=err:{ex.InnerException?.GetType().Name}"); }
        }
        return "acpi: " + string.Join(' ', parts);
    }

    // 返回 null = 未接管（调用方继续走 HC）；"ok" = 已下发；其余 = 失败原因。
    private static string? TryApplyAyaneoCeiiColor(object device, Type deviceType, int levelValue,
        int? r, int? g, int? b, int? r2, int? g2, int? b2, int brightness)
    {
        if (!ayaNativeLighting) return null;
        var pattern = PatternForLevel(levelValue);
        if (pattern is null) return null;
        var writer = TryCreateAyaneoCeiiWriter(device, deviceType);
        if (writer is null) return null;
        var left = new AyaneoCeiiColor((byte)Clamp(r ?? 255), (byte)Clamp(g ?? 0), (byte)Clamp(b ?? 0));
        // 本机没有任何独立的标志灯，只有左右两个摇杆环（各 4 个分区）：
        // 主色 → 左环，副色 → 右环。旧实现把左右都刷主色、只把 0x7C 一区刷副色
        // 并且按 B,R,G 下发，于是副色既只占四分之一个环、又被拆成洋红——用户看到
        // 的"上半部分颜色不对"和"主副不能单独调整"都是这两条造成的。
        var right = new AyaneoCeiiColor(
            (byte)Clamp(r2 ?? r ?? 255), (byte)Clamp(g2 ?? g ?? 0), (byte)Clamp(b2 ?? b ?? 0));
        var writes = AyaneoCeiiLighting.BuildSequence(
            left, right, brightness, pattern.Value, AyaneoCeiiLighting.FadeOff, ayaEnable);
        if (levelValue == 2 && ayaBreatheMs > 0)
        {
            // 呼吸：固件程序下一次，之后只推颜色字节，由主机按余弦曲线驱动明暗。
            return RunAyaneoCeiiBreathing(writer, left, right, brightness)
                ? "ok" : "aya-native-write-false";
        }
        if (writer.Apply(writes)) return "ok";
        // 常亮退回 HC 仍然能亮；动效退回 HC 只会假成功，必须如实报错。
        return levelValue == 1 ? null : "aya-native-write-false";
    }

    // 软件呼吸：先落一帧完整灯效程序（带 pattern=0xE7，让 MCU 在两次颜色更新之间
    // 自己做渐变，这样帧率不高也不会看出跳变），之后每帧只发 24 个颜色字节。
    // left = 左环（主色），right = 右环（副色），两环各 4 分区、顺序都是 R,G,B。
    // 帧预算按"每周期至少 24 帧"给，实测单帧约 250ms，因此 3s 周期基本能跑满。
    private static bool RunAyaneoCeiiBreathing(AyaneoCeiiLighting writer,
        AyaneoCeiiColor left, AyaneoCeiiColor right, int brightness, bool forever = false)
    {
        if (!writer.Apply(AyaneoCeiiLighting.BuildSequence(
                left, right, brightness, AyaneoCeiiLighting.PatternBreathing,
                AyaneoCeiiLighting.FadeOff, ayaEnable, ayaAnims,
                AyaneoCeiiLighting.WatchdogBreathing, ayaZoneAnims)))
            return false;

        var start = Environment.TickCount64;
        var floor = Math.Clamp(ayaBreatheFloorPercent, 0, 90);
        var frameBudget = Math.Max(1, ayaBreathePeriodMs / ayaBreatheFramesPerPeriod);
        Diagnostic($"[aya-breathe] start forever={forever} ms={ayaBreatheMs} period={ayaBreathePeriodMs} " +
                   $"floor={floor}% budget={frameBudget}ms frames/period={ayaBreatheFramesPerPeriod} " +
                   $"burst={ayaBreatheBurst} parentPid={ayaParentPid} brightness={brightness}");
        var frames = 0;
        while (forever || Environment.TickCount64 - start < ayaBreatheMs)
        {
            if (ParentGone())
            {
                Diagnostic($"[aya-breathe] parent gone, stopping after {frames} frames");
                break;
            }
            var elapsed = Environment.TickCount64 - start;
            var phase = elapsed % (long)ayaBreathePeriodMs / (double)ayaBreathePeriodMs;
            var wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase);
            var percent = (int)Math.Round(floor + (100 - floor) * wave);
            var applied = Math.Clamp(brightness * percent / 100, 1, 100);
            var frame = AyaneoCeiiLighting.BuildColorSequence(left, right, applied);
            var ok = ayaBreatheBurst
                ? writer.ApplyBurst(frame, ayaBreatheSettleMs)
                : writer.Apply(frame);
            if (!ok)
            {
                Diagnostic($"[aya-breathe] color-write-failed after {frames} frames");
                return false;
            }
            frames++;
            var spent = Environment.TickCount64 - start - elapsed;
            if (spent < frameBudget) Thread.Sleep((int)(frameBudget - spent));
        }
        Diagnostic($"[aya-breathe] done frames={frames} elapsed={Environment.TickCount64 - start}ms " +
                   $"fps={frames * 1000.0 / Math.Max(1, Environment.TickCount64 - start):f1}");
        return true;
    }

    // 面板崩溃或被杀时本进程要能自己收手。PID 复用不是问题：面板进程在呼吸
    // 运行期间不可能被回收再分配。
    private static bool ParentGone()
    {
        if (ayaParentPid <= 0) return false;
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(ayaParentPid);
            return parent.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    // 常驻呼吸入口：跑满调用方给的时长，或被父进程杀掉/父进程消失为止。
    private static string ExecuteAyaneoCeiiBreathing(object device, Type deviceType,
        int? r, int? g, int? b, int? r2, int? g2, int? b2, int brightness)
    {
        var writer = TryCreateAyaneoCeiiWriter(device, deviceType)
            ?? throw new NotSupportedException("AYANEO_CEII_NATIVE_BREATHE_UNAVAILABLE");
        var ring = new AyaneoCeiiColor((byte)Clamp(r ?? 255), (byte)Clamp(g ?? 0), (byte)Clamp(b ?? 0));
        // 主色 = 左环，副色 = 右环；两颗环各 4 分区，通道顺序同为 R,G,B。
        var second = new AyaneoCeiiColor(
            (byte)Clamp(r2 ?? r ?? 255), (byte)Clamp(g2 ?? g ?? 0), (byte)Clamp(b2 ?? b ?? 0));
        var ok = RunAyaneoCeiiBreathing(writer, ring, second, brightness, forever: ayaBreatheMs <= 0);
        // 退出前落回常亮同色，避免停在某一帧的暗态上。
        writer.Apply(AyaneoCeiiLighting.BuildStaticSequence(ring, second, brightness));
        return ok ? "ok" : "aya-native-write-false";
    }

    // LEDLevel：1=SolidColor 2=Breathing。其余效果目前没有可验证的 MCU 取值，
    // 一律交回 HC（保持既有行为，不制造假成功）。
    private static byte? PatternForLevel(int levelValue)
    {
        if (levelValue == 1) return AyaneoCeiiLighting.PatternOff;
        if (levelValue == 2 && ayaPattern != AyaneoCeiiLighting.PatternOff) return ayaPattern;
        return null;
    }

    // 实机取证：逐个试写候选字节，人工确认哪一个让灯呼吸，随后固化到 DefaultAyaPattern /
    // ayaEnable。结束一律复位成常亮，不留下未知动效状态。
    //   stage=enable  只扫输出使能位（pattern 关闭），先确认"能点亮"的使能值
    //   stage=pattern 固定当前使能，扫 pattern
    //   stage=both    使能 × pattern 全组合（耗时长，确认使能后再用）
    // 每一步换一种颜色，颜色即标签：目视确认时只需回答"哪个颜色在呼吸"，
    // 不必数序号（序号只对日志有意义）。
    private static readonly (string Name, byte R, byte G, byte B)[] ProbePalette =
        [("红", 255, 0, 0), ("绿", 0, 255, 0), ("蓝", 0, 64, 255), ("白", 255, 255, 255)];
    // 使能位候选：0x80+speed 是冻结实现 SetLedStatus(true) 的取值，speed 恒为 0；
    // 0x81..0x88 试的是"档位/速度"假设，末两个是 Linux 驱动与当前实现取值。
    private static readonly byte[] ProbeEnables =
        [0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88,
         AyaneoCeiiLighting.EnableEffects, AyaneoCeiiLighting.EnableStatic];

    // 动效配方：Pattern + 四段关键帧 + Fade + Watchdog。实测"释放控制权"等于交回固件
    // （固件默认是蓝色），所以静态必须保持占有；这几条用来试 watchdog 是否是
    // "锁住最后一帧"的开关——关掉它 MCU 才有可能自己播放动画程序。
    private static readonly (string Label, byte Pattern, byte Fade, byte[] Anims, byte Watchdog)[] ProbeRecipes =
    [
        ("静态对照", AyaneoCeiiLighting.PatternOff, AyaneoCeiiLighting.FadeOff,
            [AyaneoCeiiLighting.AnimStatic, AyaneoCeiiLighting.AnimStatic,
             AyaneoCeiiLighting.AnimStatic, AyaneoCeiiLighting.AnimStatic], AyaneoCeiiLighting.WatchdogOn),
        ("关键帧 wd=0", 0x01, 0x40, [0x00, 0x40, 0x80, 0xc0], 0x00),
        ("关键帧 wd=1", 0x01, 0x40, [0x00, 0x40, 0x80, 0xc0], 0x01),
        ("关键帧 wd=7(对照)", 0x01, 0x40, [0x00, 0x40, 0x80, 0xc0], AyaneoCeiiLighting.WatchdogOn),
    ];

    // aux 探针：先落一组固定颜色，再单独改一个偏移，看整体亮度/开关是否随之变化。
    // 若 base+0/base+1 是全局亮度，呼吸只需每帧改这两个字节，帧率可以做到很高。
    private static string ExecuteAyaneoCeiiAuxProbe(object device, Type deviceType,
        int? r, int? g, int? b, int brightness, IReadOnlyList<(byte Offset, byte Value)> candidates)
    {
        var writer = TryCreateAyaneoCeiiWriter(device, deviceType, allowProbeRegisters: true)
            ?? throw new NotSupportedException("AYANEO_CEII_NATIVE_PROBE_UNAVAILABLE");
        var baseColor = new AyaneoCeiiColor((byte)Clamp(r ?? 255), (byte)Clamp(g ?? 0), (byte)Clamp(b ?? 0));
        Diagnostic($"[aya-probe] aux {ElevationNote()} brightness={brightness} cases={candidates.Count}");
        var failed = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var (offset, value) = candidates[index];
            var tag = ProbePalette[index % ProbePalette.Length];
            var color = ayaProbePalette ? new AyaneoCeiiColor(tag.R, tag.G, tag.B) : baseColor;
            var leftAddress = (byte)(AyaneoCeiiLighting.LeftBase + offset);
            var rightAddress = (byte)(AyaneoCeiiLighting.RightBase + offset);
            Diagnostic($"[aya-probe] {index + 1}/{candidates.Count} offset=0x{offset:x2} value=0x{value:x2} " +
                       $"addr=0x{leftAddress:x2}/0x{rightAddress:x2} labelColor={tag.Name}");
            if (!writer.Apply(AyaneoCeiiLighting.BuildSequence(
                    color, color, brightness, AyaneoCeiiLighting.PatternOff, AyaneoCeiiLighting.FadeOff,
                    ayaEnable, ayaAnims, ayaWatchdog)))
            {
                failed++;
                Diagnostic($"[aya-probe] {index + 1}/{candidates.Count} color-write-failed");
            }
            if (!writer.ApplySingle(leftAddress, value, AyaneoCeiiLighting.LeftCommit) ||
                !writer.ApplySingle(rightAddress, value, AyaneoCeiiLighting.RightCommit))
            {
                failed++;
                Diagnostic($"[aya-probe] {index + 1}/{candidates.Count} aux-write-failed");
            }
            Thread.Sleep(ayaProbeDwellMs);
        }
        writer.Apply(AyaneoCeiiLighting.BuildStaticSequence(baseColor, baseColor, brightness));
        Diagnostic("[aya-probe] restored static");
        return failed == candidates.Count * 2 ? "aya-native-write-false" : "ok";
    }

    private static string ExecuteAyaneoCeiiProbe(object device, Type deviceType,
        int? r, int? g, int? b, int brightness)
    {
        if (ayaProbeStage == "aux" && ayaProbeAux is { Count: > 0 })
            return ExecuteAyaneoCeiiAuxProbe(device, deviceType, r, g, b, brightness, ayaProbeAux);
        // 软件呼吸走诊断通道验证：产品侧 color:breathing 目前被能力目录拒绝，
        // 诊断动作沿常亮通道准入，因此可以先把实现跑通再决定目录改动。
        if (ayaProbeStage == "softbreathe")
        {
            var handleWriter = TryCreateAyaneoCeiiWriter(device, deviceType);
            if (handleWriter is null) throw new NotSupportedException("AYANEO_CEII_NATIVE_PROBE_UNAVAILABLE");
            var handleLeft = new AyaneoCeiiColor((byte)Clamp(r ?? 255), (byte)Clamp(g ?? 0), (byte)Clamp(b ?? 0));
            if (ayaBreatheMs <= 0) ayaBreatheMs = 20000;
            var softOk = RunAyaneoCeiiBreathing(handleWriter, handleLeft, handleLeft, brightness);
            handleWriter.Apply(AyaneoCeiiLighting.BuildStaticSequence(handleLeft, handleLeft, brightness));
            Diagnostic("[aya-probe] restored static");
            return softOk ? "ok" : "aya-native-write-false";
        }
        // breathe 阶段要写 zone+0x10（0x16/0x19/0x1c 超出常规白名单），单独放行。
        var writer = TryCreateAyaneoCeiiWriter(device, deviceType,
                allowProbeRegisters: ayaProbeStage == "breathe" || ayaZoneAnims is not null)
            ?? throw new NotSupportedException("AYANEO_CEII_NATIVE_PROBE_UNAVAILABLE");
        var baseColor = new AyaneoCeiiColor((byte)Clamp(r ?? 255), (byte)Clamp(g ?? 0), (byte)Clamp(b ?? 0));
        byte[] patterns = ayaProbePatterns ??
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0f, 0x10, 0x20, 0x40, 0x80];
        byte[] enables = ayaProbeEnables ?? ProbeEnables;

        var steps = new List<(byte Enable, byte Pattern, byte Fade, byte[] Anims, byte Watchdog,
            byte[]? ZoneAnims, string Label)>();
        if (ayaProbeStage == "recipe")
        {
            // recipe 里每条自带 watchdog，用来分辨"锁帧"与"自主播放"。
            foreach (var recipe in ProbeRecipes)
                steps.Add((ayaEnable, recipe.Pattern, recipe.Fade, recipe.Anims, recipe.Watchdog, null,
                    $"{recipe.Label} pattern=0x{recipe.Pattern:x2} fade=0x{recipe.Fade:x2} " +
                    $"wd=0x{recipe.Watchdog:x2} anims={string.Join(',', recipe.Anims.Select(x => $"0x{x:x2}"))}"));
        }
        else if (ayaProbeStage == "fade")
            foreach (var fade in ayaProbeFades ?? [0x10, 0x20, 0x40, 0x80])
                steps.Add((ayaEnable, AyaneoCeiiLighting.PatternOff, fade, ayaAnims, ayaWatchdog, null,
                    $"pattern=off fade=0x{fade:x2}"));
        else if (ayaProbeStage == "breathe")
        {
            // 直接照 AYASpace 3.2.0.4 的呼吸帧下发：pattern=0xE7 / 分区步进 0x07 /
            // watchdog=0x04 / fade=0。使能候选：
            //   0x80 = 当前实现（冻结 SetLedStatus(true)）
            //   0xab = AYASpace set_effect 的末步 [0x10]+4 反推值
            //   0xb1 = Linux 驱动 ENABLE_ON
            byte[] zoneAnims = ayaZoneAnims ??
                [AyaneoCeiiLighting.AnimBreathing, AyaneoCeiiLighting.AnimBreathing,
                 AyaneoCeiiLighting.AnimBreathing, AyaneoCeiiLighting.AnimBreathing];
            foreach (var enable in ayaProbeEnables ??
                [AyaneoCeiiLighting.EnableStatic, 0xab, AyaneoCeiiLighting.EnableEffects])
                foreach (var watchdog in ayaProbeWatchdogs ?? [AyaneoCeiiLighting.WatchdogBreathing])
                    steps.Add((enable, AyaneoCeiiLighting.PatternBreathing, AyaneoCeiiLighting.FadeOff,
                        ayaAnims, watchdog, zoneAnims,
                        $"breathe enable=0x{enable:x2} pattern=0x{AyaneoCeiiLighting.PatternBreathing:x2} " +
                        $"wd=0x{watchdog:x2}"));
        }
        else if (ayaProbeStage is "enable" or "both")
            foreach (var enable in enables)
            {
                if (ayaProbeStage == "enable")
                    steps.Add((enable, AyaneoCeiiLighting.PatternOff, ayaFade, ayaAnims, ayaWatchdog, null,
                        $"enable=0x{enable:x2}"));
                else
                    foreach (var pattern in patterns)
                        steps.Add((enable, pattern, ayaFade, ayaAnims, ayaWatchdog, null,
                            $"enable=0x{enable:x2} pattern=0x{pattern:x2}"));
            }
        else
            foreach (var pattern in patterns)
                steps.Add((ayaEnable, pattern, ayaFade, ayaAnims, ayaWatchdog, ayaZoneAnims,
                    $"pattern=0x{pattern:x2} (enable=0x{ayaEnable:x2})"));

        // 保活验证：把首条配方重复下发 N 次，每次间隔 dwell-ms，模拟 AYASpace
        // 的周期性重发，用来观察长周期动效是否被截断。
        if (ayaProbeRepeat > 1 && steps.Count > 0)
            steps = Enumerable.Repeat(steps[0], ayaProbeRepeat).ToList();

        var failed = 0;
        Diagnostic($"[aya-probe] start {ElevationNote()} stage={ayaProbeStage} brightness={brightness} " +
                   $"base=({baseColor.R},{baseColor.G},{baseColor.B}) release={ayaProbeRelease} steps={steps.Count}");
        Diagnostic($"[aya-probe] {DescribeEcTransport(device, deviceType)}");
        Diagnostic($"[aya-probe] {DescribeAcpiEc(device, deviceType)}");
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var tag = ProbePalette[index % ProbePalette.Length];
            var color = ayaProbePalette
                ? new AyaneoCeiiColor(tag.R, tag.G, tag.B)
                : baseColor;
            Diagnostic($"[aya-probe] {index + 1}/{steps.Count} {step.Label} " +
                       $"labelColor={tag.Name}({tag.R},{tag.G},{tag.B})");
            if (!writer.Apply(AyaneoCeiiLighting.BuildSequence(
                    color, color, brightness, step.Pattern, step.Fade, step.Enable, step.Anims,
                    step.Watchdog, step.ZoneAnims)))
            {
                failed++;
                Diagnostic($"[aya-probe] {index + 1}/{steps.Count} write-failed");
            }
            if (ayaProbeRelease)
            {
                var released = writer.Release();
                Diagnostic($"[aya-probe] {index + 1}/{steps.Count} release={(released ? "ok" : "failed")}");
            }
            Thread.Sleep(ayaProbeDwellMs);
        }
        writer.Apply(AyaneoCeiiLighting.BuildStaticSequence(baseColor, baseColor, brightness));
        Diagnostic("[aya-probe] restored static");
        return failed == steps.Count ? "aya-native-write-false" : "ok";
    }

    // Actual DMI is resolved by Run before this operation. Software consumers
    // link this same admission/owner validation and replace only the I/O leaf.
    internal static Dictionary<string, object?> ExecuteAyaneoOemRestore(Type actualType,
        string? expectedClass, string root, Assembly approvedHc, int timeoutMs,
        Func<LightingOemOwnershipCapability, string, Assembly, CancellationToken, IAyaneoOemRestoreIo> open)
    {
        var operation = RequireCatalogLightingOperation(actualType, expectedClass, "restore-oem", null, false, null);
        var ownership = HcLightingCapabilityCatalog.ResolveOemOwnership(actualType.FullName)
            ?? throw new NotSupportedException("OEM_LIGHTING_OWNERSHIP_UNAVAILABLE");
        var release = actualType.GetMethod("CEcControl_RgbReleaseControl", BindingFlags.Instance | BindingFlags.NonPublic);
        if (release?.DeclaringType?.FullName != ownership.MethodOwner || release.ReturnType != typeof(void) ||
            release.GetParameters().Length != 0 || !release.IsFamily || !release.IsVirtual)
            throw new InvalidOperationException("OEM_LIGHTING_METHOD_OWNER_CHANGED");
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var io = open(ownership, root, approvedHc, timeout.Token);
        var result = AyaneoOemLightingRestore.Apply(ownership, io, timeout.Token);
        return new Dictionary<string, object?> {
            ["device"] = actualType.FullName, ["deviceClass"] = actualType.FullName,
            ["action"] = "restore-oem", ["expectedDeviceClass"] = expectedClass,
            ["capabilityId"] = operation.Device.DeviceId, ["capabilityOperation"] = operation.OperationId,
            ["capabilityCatalogSha256"] = HcLightingCapabilityCatalog.CatalogSha256,
            ["note"] = result.Reason, ["oemRestoreRequested"] = true,
            ["ownershipRequestCompleted"] = result.RequestCompleted,
            ["ownershipProtocol"] = ownership.Protocol == LightingOemOwnershipProtocol.LegacyCec ? "legacy-cec" : "ceii-ram",
            ["ownershipReadbackAvailable"] = false, ["physicalEffectVerified"] = false,
            ["writeCallCount"] = result.Writes,
        };
    }

    // No field seeding or guessed current color/brightness: the original CEc
    // public setters prepare and consume the explicit request in one process.
    private static bool ValidateAyaneoCeRequest(Type deviceType, string action, string? level, int? brightness, int? r, int? g, int? b)
    {
        const string ownerName = "HandheldCompanion.Devices.AYANEO.AYANEODeviceCEc";
        Type? owner = deviceType;
        while (owner is not null && owner.FullName != ownerName) owner = owner.BaseType;
        if (owner is null) return false;
        foreach (var name in new[] { "SetLedColor", "SetLedBrightness", "SetLedStatus" })
            if (deviceType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)?.DeclaringType != owner)
                throw new InvalidOperationException("AYANEO_CE_LIGHTING_METHOD_OWNER_CHANGED");
        if (action == "color" && brightness is null)
            throw new InvalidOperationException("AYANEO_CE_COLOR_REQUIRES_EXPLICIT_BRIGHTNESS");
        if (action == "brightness" && (r is null || g is null || b is null))
            throw new InvalidOperationException("AYANEO_CE_BRIGHTNESS_REQUIRES_CURRENT_COLOR_CONTEXT");
        if (action == "brightness")
            _ = RequireCatalogLightingOperation(deviceType, deviceType.FullName, "color", level, true, null);
        return true;
    }

    // MSI's general Open also changes TDP, controller mode and ROM settings.
    // Lighting needs the HID bound by IsReady, its firmware, and open-state flags.
    // Keep that narrow preparation separate from the general device lifecycle.
    private static bool OpenLightingDevice(object device)
    {
        var type = device.GetType();
        if (OneXLightingSession.IsFamily(type)) return OneXLightingSession.IsReady(device);
        Type? msi = type;
        while (msi is not null && msi.FullName != "HandheldCompanion.Devices.ClawA1M") msi = msi.BaseType;
        if (msi is null) return Invoke(device, "Open") is true;
        var open = type.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, "Open");
        if (open.DeclaringType?.FullName is not ("HandheldCompanion.Devices.ClawA1M" or
            "HandheldCompanion.Devices.ClawA2VM" or "HandheldCompanion.Devices.MSI.ClawCG3EM"))
            throw new InvalidOperationException("HC MSI lighting Open owner changed");
        var baseOpen = open.GetBaseDefinition();
        var owner = baseOpen.DeclaringType;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var openLib = owner?.GetField("UseOpenLib", fields);
        var devices = owner?.GetField("hidDevices", fields);
        var firmware = msi.GetField("Firmware", fields);
        var clawOpen = msi.GetField("ClawOpen", fields);
        var isOpen = type.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance)?.GetMethod;
        if (owner?.FullName != "HandheldCompanion.Devices.IDevice" || owner.Assembly != type.Assembly ||
            baseOpen.ReturnType != typeof(bool) || baseOpen.GetParameters().Length != 0 ||
            openLib?.FieldType != typeof(bool) || openLib.GetValue(device) is not false ||
            firmware?.FieldType != typeof(int) || clawOpen?.FieldType != typeof(bool) ||
            isOpen?.DeclaringType != msi || devices?.GetValue(device) is not System.Collections.IDictionary bindings)
            throw new InvalidOperationException("HC MSI lighting base Open contract changed");
        // Frozen HC Open and LED methods both select INPUT_HID_ID == 1.
        if (!bindings.Contains(1) || bindings[1] is not { } hid) return false;
        if (hid.GetType().GetProperty("IsConnected")?.GetValue(hid) is not true) return false;
        var attributes = hid.GetType().GetProperty("Attributes")?.GetValue(hid)
            ?? throw new InvalidOperationException("MSI lighting HID attributes missing");
        if (attributes.GetType().GetProperty("Version")?.GetValue(attributes) is not int version)
            throw new InvalidOperationException("MSI lighting firmware descriptor changed");
        var call = new System.Reflection.Emit.DynamicMethod("YmccMsiLightingOpen", typeof(bool),
            [typeof(object)], typeof(Program).Module, skipVisibility: false);
        var il = call.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Castclass, owner);
        il.Emit(System.Reflection.Emit.OpCodes.Call, baseOpen);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        if (!((Func<object, bool>)call.CreateDelegate(typeof(Func<object, bool>)))(device)) return false;
        firmware.SetValue(device, version);
        clawOpen.SetValue(device, true);
        return true;
    }

    // Validate before assembly load/Initialize/Open, and before direct action dispatch.
    // Keep the existing numeric clamping contract; reject ambiguous commands.
    private static void ValidateRequest(string? action, string? level, string? status, string? presetId)
    {
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("--action is required");
        if (action is not ("color" or "brightness" or "status" or "off" or "preset" or "restore-oem" or "aya-light-probe" or "aya-light-readback" or "aya-light-breathe"))
            throw new ArgumentException($"unknown action {action}");
        if (action == "status" && !string.Equals(status, "on", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "off", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--status must be on or off");
        if (action == "color" && !string.IsNullOrWhiteSpace(level) &&
            (string.Equals(level, "preset", StringComparison.OrdinalIgnoreCase) || !LedLevels.ContainsKey(level)))
            throw new ArgumentException($"unsupported color level {level}; preset requires --action preset and --preset-id");
        if (action == "preset" && string.IsNullOrWhiteSpace(presetId))
            throw new ArgumentException("preset-id is required");
    }

    private static void ValidateOperationImplementation(Type deviceType, string action, bool includesBrightness)
    {
        // HC IDevice's defaults return true without issuing any operation.
        // Do not promote these inherited no-ops to successful lighting writes.
        void RequireOverride(string method)
        {
            if (!HasRealLightingMethod(deviceType, method))
                throw new NotSupportedException($"device lighting operation unavailable: {method}");
        }
        if (action is "color" or "off") RequireOverride("SetLedColor");
        if (action == "brightness" || (action is "color" or "preset" && includesBrightness)) RequireOverride("SetLedBrightness");
        if (action == "status") RequireOverride("SetLedStatus");
        if (action == "preset") RequireOverride("SetLEDPreset");
    }

    // note→退出码映射（从 Main 提取，使离线 harness 能直接测试真实映射）：
    // 仅 "ok" 记成功，其余（setLedColor-false/setLedBrightness-false/
    // setLedStatus-false/off-partial 及 null）均非零。
    internal static int ExitCodeForNote(string? note) => note == "ok" ? 0 : 1;

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static void ValidatePresetDeviceIdentity(Type actualType, string? expectedClass, string? action)
    {
        // This new guard belongs to the new preset route. Existing color paths
        // retain their model aliases (e.g. AynLoki -> LokiMax6600U/6800U).
        if (action == "preset" && expectedClass is not null &&
            !string.Equals(actualType.Name, expectedClass.Split('.').Last(), StringComparison.Ordinal))
            throw new InvalidOperationException("HC factory device differs from the requested preset class");
    }

    // Use the actual device list and object. A level enum (128), array index
    // or caller-supplied numeric value is not a named HC preset.
    private static string ExecuteLedPreset(object device, Type deviceType, string? presetId, int? brightness)
    {
        if (string.IsNullOrWhiteSpace(presetId)) throw new ArgumentException("preset-id is required");
        ValidateOperationImplementation(deviceType, "preset", brightness is not null);
        var presets = deviceType.GetProperty("LEDPresets", BindingFlags.Public | BindingFlags.Instance)?.GetValue(device)
            as System.Collections.IEnumerable ?? throw new MissingMemberException(deviceType.FullName, "LEDPresets");
        var matches = new List<object>();
        foreach (var preset in presets)
        {
            if (preset is not null && preset.GetType().GetProperty("NameKey")?.GetValue(preset) is string key &&
                string.Equals(key, presetId, StringComparison.Ordinal)) matches.Add(preset);
        }
        if (matches.Count != 1) throw new ArgumentException("preset-id is absent or ambiguous for this HC device");
        var setPreset = deviceType.GetMethod("SetLEDPreset", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(deviceType.FullName, "SetLEDPreset");
        var parameters = setPreset.GetParameters();
        if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(matches[0]) ||
            setPreset.ReturnType != typeof(bool)) throw new InvalidOperationException("Invalid SetLEDPreset contract");
        // Match HC DynamicLightingManager: brightness precedes the preset.
        OneXQueuedLighting.PrepareRequestCache(device, "preset", brightness, null);
        var note = "ok";
        if (brightness is not null)
        {
            var method = deviceType.GetMethod("SetLedBrightness", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(deviceType.FullName, "SetLedBrightness");
            if (method.Invoke(device, [Clamp(brightness.Value)]) is not true) note = "setLedBrightness-false";
        }
        if (OneXQueuedLighting.UsesQueue(deviceType, preset: true))
            OneXQueuedLighting.InvalidatePresetCache(device);
        if (setPreset.Invoke(device, [matches[0]]) is not true) return "setLEDPreset-false";
        return note;
    }

    // 目标方法签名驱动：仅从 deviceType 的明确 public instance 方法中收集
    // 名为 SetLedColor 的候选；0 个 = 缺失，>1 个 = 歧义，二者都拒绝（不默认
    // 任选、不把类型错误转成 true）；恰好 1 个时验证四参、两色同型、第三 enum、
    // 第四 int、返回 bool。显式 BindingFlags.Public | Instance 排除 static 与
    // 非公开方法，也避免 GetMethod 在重载下抛 AmbiguousMatchException 的隐式行为。
    // 现网 HC（0C5132A9…）签名为
    //   (System.Windows.Media.Color, System.Windows.Media.Color,
    //    HandheldCompanion.Utils.DeviceUtils+LEDLevel, System.Int32) -> bool
    // 不再依赖错误的全名字符串，也不按名称模糊匹配多个重载后任选。
    private static (MethodInfo SetColor, Type ColorType, Type LedLevelType)
        ResolveSetLedColorContract(Type deviceType)
    {
        var candidates = EnumerateSetLedColorCandidates(deviceType);
        if (candidates.Count == 0)
            throw new MissingMethodException(deviceType.FullName, "SetLedColor");
        if (candidates.Count > 1)
            throw new AmbiguousMatchException(
                $"{deviceType.FullName}.SetLedColor has {candidates.Count} public instance overloads");
        var setColor = candidates[0];
        var parameters = setColor.GetParameters();
        if (parameters.Length != 4)
            throw new InvalidOperationException($"SetLedColor arity {parameters.Length} != 4");
        var colorType = parameters[0].ParameterType;
        if (parameters[1].ParameterType != colorType)
            throw new InvalidOperationException(
                $"SetLedColor color parameter mismatch: {parameters[0].ParameterType} vs {parameters[1].ParameterType}");
        if (!parameters[2].ParameterType.IsEnum)
            throw new InvalidOperationException(
                $"SetLedColor level parameter is not an enum: {parameters[2].ParameterType}");
        if (parameters[3].ParameterType != typeof(int))
            throw new InvalidOperationException(
                $"SetLedColor speed parameter is not int: {parameters[3].ParameterType}");
        if (setColor.ReturnType != typeof(bool))
            throw new InvalidOperationException(
                $"SetLedColor return type is not bool: {setColor.ReturnType}");
        return (setColor, colorType, parameters[2].ParameterType);
    }

    // 收集 public instance 的 SetLedColor 候选：含类型自身的方法；若为接口，还须
    // 收集其全部继承接口上的同名方法（.NET 的 Type.GetMethods 对接口不返回继承
    // 成员），使继承方法可解析。按参数签名去重，避免菱形继承下同一方法重复计入而
    // 误判为歧义。static 与非公开方法一律不计入（BindingFlags.Public | Instance）。
    private static List<MethodInfo> EnumerateSetLedColorCandidates(Type deviceType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MethodInfo>();
        void Collect(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "SetLedColor") continue;
                var key = string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName));
                if (seen.Add(key)) result.Add(method);
            }
        }
        Collect(deviceType);
        if (deviceType.IsInterface)
            foreach (var inherited in deviceType.GetInterfaces())
                Collect(inherited);
        return result;
    }

    // 颜色对象显式按目标类型构造：当前 HC 为 System.Windows.Media.Color
    // （WPF，非 WinRT），用 ARGB 工厂 + byte 参数并显式给 alpha=0xFF。
    // 未知色类型不做强制转换、不伪成功。
    private static object CreateLedColor(Type colorType, int r, int g, int b)
    {
        if (colorType.FullName == "System.Windows.Media.Color")
        {
            var factory = colorType.GetMethod("FromArgb", BindingFlags.Public | BindingFlags.Static,
                null, [typeof(byte), typeof(byte), typeof(byte), typeof(byte)], null);
            if (factory is not null && factory.ReturnType == colorType)
                return factory.Invoke(null, [(byte)0xFF, (byte)r, (byte)g, (byte)b])!;
        }
        throw new NotSupportedException(
            $"unsupported SetLedColor color parameter type: {colorType.FullName}");
    }

    // FanHost WaitForHcDeviceReadyBeforeOpen 同构：IsReady 轮询（250ms）。
    private static bool WaitReady(object device, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Invoke(device, "IsReady") is bool ready && ready) return true;
            Thread.Sleep(250);
        }
        return false; // 与 HC 一致：超时不拒绝，继续 Open
    }

    // FanHost ResolveDependency 同构。
    private static Assembly? ResolveDependency(AssemblyLoadContext _, AssemblyName name)
    {
        if (string.IsNullOrWhiteSpace(name.Name)) return null;
        var path = Path.Combine(runtimeRoot ?? AppContext.BaseDirectory, name.Name + ".dll");
        return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
            : YeMan.SharedRuntime.DeviceCoreSharedDependencies.Resolve(_, name, runtimeRoot ?? AppContext.BaseDirectory);
    }

    // FanHost ConfigureHcRuntime 精简版（HC 需要 SettingsPath/LogsPath 与
    // LogManager 初始化；缺失则 fail-fast，避免 HC 内部写入默认路径）。
    private static void ConfigureHcRuntime()
    {
        if (assembly is null) return;
        var root = Path.Combine(Path.GetTempPath(), "YeManLightSetter-HC");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        Environment.SetEnvironmentVariable("LOG_PATH", logs);
        var appType = assembly.GetType("HandheldCompanion.App");
        SetStaticMember(appType, "SettingsPath", root);
        SetStaticMember(appType, "LogsPath", logs);
        SetStaticMember(appType, "InstallPath", runtimeRoot ?? AppContext.BaseDirectory);
        SetStaticMember(appType, "GameControllerDbPath",
            Path.Combine(runtimeRoot ?? AppContext.BaseDirectory, "gamecontrollerdb.txt"));
        var shared = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(item => item.GetName().Name == "Shared")
            ?? (runtimeRoot is not null && File.Exists(Path.Combine(runtimeRoot, "Shared.dll"))
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtimeRoot, "Shared.dll"))
                : null);
        var logType = shared?.GetType("HandheldCompanion.Shared.LogManager");
        var init = logType?.GetMethod("Initialize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)]);
        if (init is null)
            throw new MissingMethodException("HandheldCompanion.Shared.LogManager.Initialize not found");
        init.Invoke(null, ["YeManLightSetter"]);
    }

    private static void SetStaticMember(Type? type, string name, string value)
    {
        if (type is null) return;
        var property = type.GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property is not null && property.CanWrite)
        {
            property.SetValue(null, value);
            return;
        }
        var field = type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field is not null && field.FieldType == typeof(string)) field.SetValue(null, value);
    }

    // 反射调用（精确参数类型 + fallback 简单查找）。
    private static object? Invoke(object target, string method, params object?[] args)
    {
        var type = target.GetType();
        var exact = type.GetMethod(method,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            args.Select(arg => arg?.GetType() ?? typeof(object)).ToArray(),
            null);
        var resolved = exact
            ?? type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (resolved is null) throw new MissingMethodException(type.FullName, method);
        return resolved.Invoke(target, args);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
