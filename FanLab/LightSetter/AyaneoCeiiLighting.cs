// AYANEO CEii（现代 MCU，AIR Plus 家族）原生灯光写入器。
//
// 为什么需要它：
// 冻结的 HandheldCompanion.dll 里 AYANEODeviceCEc.SetLedColor 的 switch 只处理
// LEDLevel.SolidColor，其余效果（Breathing/Rainbow/...）直接落空但返回 true。
// 同时它用同一个数值驱动左右两组灯（右侧实际量程只有左侧一半），而冻结实现
// 只把 SolidColor 落到一个颜色上——副色（右环）因此从未被独立驱动过。
//
// 分级语义（2026-11-22 起）：左摇杆环 = 主色，右摇杆环 = 副色，两组各 4 个
// 分区、一律 R,G,B 顺序。整机没有独立的 AYA 标志灯，0x7C 就是右环第 4 区，
// 旧代码把它当标志灯并按 B,R,G 写，导致副色被拆成洋红（见 ZoneOffsets 注释）。
//
// 寄存器表不是猜的，与 ShadowBlip/ayaneo-platform 的 air_plus 分支逐项对应
// （ayaneo-platform.c：ayaneo_led_mc_set / ayaneo_led_mc_on /
//  ayaneo_led_mc_intensity，zones[4] = {3,6,9,12}，
//  scale_color(color_l,64) / scale_color(color_r,32)）：
//   左右基址 0xb0 / 0x70，提交位 0xc6 / 0x86，模式 0x87 = 0xa5 取用 / 0x00 释放；
//   灯效寄存器相对基址偏移：0x0f pattern、0x10 fade、0x11-0x14 动画步进、
//   0x15 watchdog；动画步进 0x05 = 静态（常亮）。
//
// 本类型只产出"要写哪些字节"，真正落地由传入的委托完成，因此可以在没有硬件
// 的情况下被离线自检逐字节断言。
internal readonly record struct AyaneoCeiiColor(byte R, byte G, byte B);
internal readonly record struct AyaneoCeiiWrite(byte Address, byte Data);

internal sealed class AyaneoCeiiLighting
{
    // 控制与定位
    internal const byte ModeAddress = 0x87;
    internal const byte ModeHold = 0xa5;
    internal const byte ModeRelease = 0x00;
    internal const byte LeftBase = 0xb0;
    internal const byte RightBase = 0x70;
    internal const byte LeftCommit = 0xc6;
    internal const byte RightCommit = 0x86;

    // 输出使能：分区颜色从 base+3 起，base+2 是空闲的使能位。
    // 冻结实现 SetLedStatus(true) 写 0x80；驱动 AYANEO_LED_CMD_ENABLE_ON 写 0xb1。
    // 两者都置了 bit7，0xb1 还带 bit4/5/0（疑为"带动效"），故保留为可配置值。
    internal const byte EnableOffset = 0x02;
    internal const byte EnableStatic = 0x80;
    internal const byte EnableEffects = 0xb1;

    // 灯效寄存器（相对各自 group 基址）
    internal const byte PatternOffset = 0x0f;
    internal const byte FadeOffset = 0x10;
    internal const byte Anim1Offset = 0x11;
    internal const byte Anim2Offset = 0x12;
    internal const byte Anim3Offset = 0x13;
    internal const byte Anim4Offset = 0x14;
    internal const byte WatchdogOffset = 0x15;

    // 写入值
    internal const byte PatternOff = 0x00;
    internal const byte FadeOff = 0x00;
    internal const byte AnimStatic = 0x05;
    internal const byte WatchdogOn = 0x07;

    // AYASpace 3.2.0.4（AYASpaceCef.exe）逆向得到的动效取值，见 EcRgb.cpp 的
    // always_bright / breathe_custom_color 两条路径：
    //   always_bright       → set_effect 序列编码 pattern=(2*0x40+0x10*8+0)=0x00、
    //                         分区动画步进 0x05、watchdog 0x07
    //   breathe_custom_color→ 同一序列编码 pattern=(3*0x40+4*8+7)=0xE7、
    //                         分区动画步进 0x07、watchdog 0x04
    // 静态那一列与 Linux 驱动完全吻合，说明这套编码可信；呼吸那一列此前我们
    // 从未试过 pattern=0xE7，所以一直扫不到动效。
    internal const byte PatternBreathing = 0xe7;
    internal const byte AnimBreathing = 0x07;
    internal const byte WatchdogBreathing = 0x04;

    // 分区动画步进寄存器相对分区基偏移的量：AYASpace 写的是 zoneOffset + 0x10
    // （即 0x13/0x16/0x19/0x1c），而不是 groupBase + 0x11..0x14。
    internal const byte ZoneAnimDelta = 0x10;

    // 量程：AYASpace 官方（AYASpaceCef 0x140348b70）把 0..255 线性映射到 0..192，
    // 三个通道同一个常数，左右两组同值——它才是本机用户熟悉的亮度基准。
    // 厂商 Linux 驱动额外按 64/32 收窄（ayaneo_led_mc_intensity），那是保守限幅，
    // 不是 MCU 硬上限：照它落地只剩官方三成亮度，单通道色（正红）尤其暗，
    // 而且左右按 64/32 分别收窄会让同一颜色的两个摇杆环亮度差一倍。
    // 现在完全对齐官方：全局 192，左右不再二次收窄。
    internal const int DefaultGlobalColorCeiling = 192;
    private static int globalColorCeiling = DefaultGlobalColorCeiling;
    internal static int GlobalColorCeiling
    {
        get => globalColorCeiling;
        set => globalColorCeiling = Math.Clamp(value, 32, 255);
    }

    // 两侧摇杆环走同一颗 MCU 的同类通道，物理量程一致；255 表示"不再收窄"。
    internal const int LeftChannelCeiling = 255;
    internal const int RightChannelCeiling = 255;

    // 冻结实现每写一字节前都重新握一次 0x87=0xA5，且每字节后等 10ms；实测少了
    // 这一步 MCU 会中途脱手，灯停在固件默认色。此处按同样的节奏复现。
    private const int CommitSettleMs = 10;

    // 每组的四个分区：zone0..zone3 = base + 3/6/9/12。
    //
    // 2026-11-22 实机照片推翻了两条旧结论，改任何一条前请先看这段：
    //   1) base+12（右组 0x7C）不是"AYA 标志灯"，它就是右摇杆环的第 4 个分区。
    //      旧依据是 CEcControl_RgbI2cWrite 里 StickRight/StickBoth/AYA 共用 0x70 基址，
    //      于是推断 AYA 分区落在这里。但整机只有左右两个环会亮，没有任何独立的标志灯；
    //      按"标志灯"给它单独喂副色，结果只是把右环的四分之一染成另一种颜色。
    //   2) 通道顺序在四个分区上完全一致，都是 R,G,B。旧代码只对 0x7C 用 B,R,G，
    //      理由是 CEcRgb_SetColorAya 按 (B,R,G) 写——那套实现是给 AYA 标志写的，
    //      用在这里就是把颜色拆散：副色 #2ea6ff（天蓝）被写成 #FF2EA6（洋红），
    //      照片里摇杆环上半圈那圈莫名其妙的粉白光就是它。
    // 因此：不再有 logo 特例，四个分区同一顺序；副色整组落到右环。
    private static readonly byte[] ZoneOffsets = [3, 6, 9, 12];
    // 四段动画步进的默认值：全静态。动效取证时可整体替换（见 BuildSequence 的 anims）。
    private static readonly byte[] StaticAnims = [AnimStatic, AnimStatic, AnimStatic, AnimStatic];

    private readonly Func<byte, byte, bool> write;

    internal AyaneoCeiiLighting(Func<byte, byte, bool> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        this.write = write;
    }

    // 单通道缩放：先按亮度百分比收窄，再压过 192，再按本侧上限收窄。
    // 非零输入不允许被压成 0——那会让左右两侧在最暗处出现可见色差。
    internal static byte ScaleChannel(int channel, int ceiling, int brightnessPercent)
    {
        if (channel <= 0) return 0;
        var scaled = (int)Math.Round(channel * Math.Clamp(brightnessPercent, 0, 100) / 100.0);
        scaled = scaled * GlobalColorCeiling / 255;
        scaled = scaled * ceiling / 255;
        if (scaled <= 0) scaled = 1;
        return (byte)Math.Min(scaled, 255);
    }

    internal static AyaneoCeiiColor Scale(AyaneoCeiiColor color, int ceiling, int brightnessPercent) =>
        new(ScaleChannel(color.R, ceiling, brightnessPercent),
            ScaleChannel(color.G, ceiling, brightnessPercent),
            ScaleChannel(color.B, ceiling, brightnessPercent));

    // 常亮：与驱动器 ayaneo_led_mc_on() + ayaneo_led_mc_intensity() 同构。
    // left = 左摇杆环（主色），right = 右摇杆环（副色）。
    internal static IReadOnlyList<AyaneoCeiiWrite> BuildStaticSequence(
        AyaneoCeiiColor left, AyaneoCeiiColor right, int brightnessPercent) =>
        BuildSequence(left, right, brightnessPercent, PatternOff, FadeOff);

    // pattern 非 0 时由 MCU 自己跑动效；颜色仍需先落地，动效才会作用于该颜色。
    // enable 是 base+2 的输出使能字节，缺了它 MCU 输出保持关闭（灯不亮）。
    // anims 是四段动画步进值；默认全静态（0x05），只有取证试写才需要别的取值。
    // watchdog 是 base+0x15；驱动固定写 0x07，取证时可置 0 观察是否解锁自主播放。
    internal static IReadOnlyList<AyaneoCeiiWrite> BuildSequence(
        AyaneoCeiiColor left, AyaneoCeiiColor right, int brightnessPercent,
        byte pattern, byte fade, byte enable = EnableStatic, IReadOnlyList<byte>? anims = null,
        byte watchdog = WatchdogOn, IReadOnlyList<byte>? zoneAnims = null)
    {
        var steps = anims ?? StaticAnims;
        if (steps.Count != 4) throw new ArgumentException("anims must carry exactly four steps", nameof(anims));
        if (zoneAnims is not null && zoneAnims.Count != 4)
            throw new ArgumentException("zoneAnims must carry exactly four steps", nameof(zoneAnims));
        var leftScaled = Scale(left, LeftChannelCeiling, brightnessPercent);
        var rightScaled = Scale(right, RightChannelCeiling, brightnessPercent);
        var writes = new List<AyaneoCeiiWrite>(96);

        AddEffectProgram(writes, LeftBase, LeftCommit, enable, pattern, fade, steps, watchdog, zoneAnims);
        AddEffectProgram(writes, RightBase, RightCommit, enable, pattern, fade, steps, watchdog, zoneAnims);

        // 与驱动器一致：先左组全部分区，再右组全部分区。两组都是 R,G,B，无特例。
        foreach (var offset in ZoneOffsets) AddChannel(writes, LeftBase, LeftCommit, offset, leftScaled);
        foreach (var offset in ZoneOffsets) AddChannel(writes, RightBase, RightCommit, offset, rightScaled);

        return writes;
    }

    // 动效程序：使能 / pattern / fade / 四段动画步进 / 分区动画步进 / watchdog。
    // zoneAnims 为 null 时不下发分区动画步进，保持历史字节序列不变。
    private static void AddEffectProgram(List<AyaneoCeiiWrite> writes, byte baseAddress, byte commit,
        byte enable, byte pattern, byte fade, IReadOnlyList<byte> steps, byte watchdog,
        IReadOnlyList<byte>? zoneAnims)
    {
        AddByte(writes, baseAddress, commit, EnableOffset, enable);
        AddByte(writes, baseAddress, commit, PatternOffset, pattern);
        AddByte(writes, baseAddress, commit, FadeOffset, fade);
        AddByte(writes, baseAddress, commit, Anim1Offset, steps[0]);
        AddByte(writes, baseAddress, commit, Anim2Offset, steps[1]);
        AddByte(writes, baseAddress, commit, Anim3Offset, steps[2]);
        AddByte(writes, baseAddress, commit, Anim4Offset, steps[3]);
        if (zoneAnims is not null)
            for (var i = 0; i < ZoneOffsets.Length; i++)
                AddByte(writes, baseAddress, commit, (byte)(ZoneOffsets[i] + ZoneAnimDelta), zoneAnims[i]);
        AddByte(writes, baseAddress, commit, WatchdogOffset, watchdog);
    }

    private static void AddChannel(List<AyaneoCeiiWrite> writes, byte baseAddress, byte commit,
        byte offset, AyaneoCeiiColor color)
    {
        // 四个分区一律 R,G,B。曾经按 CEcRgb_SetColorAya 的 B,R,G 写过右组第 4 区，
        // 实机照片证明那是错的：天蓝会被拆成洋红，粉白光就是通道互换的产物。
        AddByte(writes, baseAddress, commit, offset, color.R);
        AddByte(writes, baseAddress, commit, (byte)(offset + 1), color.G);
        AddByte(writes, baseAddress, commit, (byte)(offset + 2), color.B);
    }

    private static void AddByte(List<AyaneoCeiiWrite> writes, byte baseAddress, byte commit,
        byte offset, byte value)
    {
        writes.Add(new((byte)(baseAddress + offset), value));
        writes.Add(new(commit, 0x01));
    }

    // 逐字节下发。每个数据字节前补一次 0x87=0xA5 取用握手（与冻结实现逐字节同构），
    // 提交字节不再补握手；任一写入失败立即停止，不做隐式重试。
    internal bool Apply(IReadOnlyList<AyaneoCeiiWrite> writes)
    {
        foreach (var item in writes)
        {
            if (item.Address is LeftCommit or RightCommit)
            {
                if (!write(item.Address, item.Data)) return false;
                Thread.Sleep(CommitSettleMs);
                continue;
            }
            if (!write(ModeAddress, ModeHold)) return false;
            if (!write(item.Address, item.Data)) return false;
        }
        return true;
    }

    // 交还控制权给 MCU 自己的固件灯效（重启/离开本工具时用）。
    internal bool Release() => write(ModeAddress, ModeRelease);

    // 单字节写入 + 提交：取证时用来试"某个偏移是不是全局亮度/总开关"。
    // 与整段下发同样的节奏：先握 0x87=0xA5，再写数据，再提交并等待收敛。
    internal bool ApplySingle(byte address, byte data, byte commit)
    {
        if (!write(ModeAddress, ModeHold)) return false;
        if (!write(address, data)) return false;
        if (!write(commit, 0x01)) return false;
        Thread.Sleep(CommitSettleMs);
        return true;
    }

    // 批量下发：整组数据只做一次取用握手，末尾每组提交一次。
    // 呼吸要 10fps 以上，而逐字节"握手 + 10ms 收敛"在 24 个颜色字节上就是 240ms，
    // 一帧 400ms，一个 3 秒周期只有 7 帧——这就是卡顿的来源。
    // 该路径只用于已确认稳定的颜色区（不含使能/pattern/watchdog）。
    internal bool ApplyBurst(IReadOnlyList<AyaneoCeiiWrite> writes, int settleMs)
    {
        if (!write(ModeAddress, ModeHold)) return false;
        foreach (var item in writes)
        {
            if (item.Address is LeftCommit or RightCommit) continue;
            if (!write(item.Address, item.Data)) return false;
        }
        if (!write(ModeAddress, ModeHold)) return false;
        if (!write(LeftCommit, 0x01)) return false;
        if (!write(ModeAddress, ModeHold)) return false;
        if (!write(RightCommit, 0x01)) return false;
        if (settleMs > 0) Thread.Sleep(settleMs);
        return true;
    }

    // 只允许落在已知的 CEii 寄存器集合内，避免反射调用把字节写到风扇/电源区。
    internal static bool IsKnownRegister(byte address) =>
        address == ModeAddress ||
        address is LeftCommit or RightCommit ||
        IsGroupRegister(address, LeftBase) || IsGroupRegister(address, RightBase);

    private static bool IsGroupRegister(byte address, byte baseAddress)
    {
        if (address < baseAddress) return false;
        var offset = address - baseAddress;
        // 使能位 0x02、颜色区 0x03..0x0e 与灯效区 0x0f..0x15 才有意义；其余偏移越界。
        return offset is >= EnableOffset and <= WatchdogOffset;
    }

    // 取证专用：额外放行 base+0 / base+1（驱动未使用的两个低偏移）以及
    // zone+0x10 分区动画步进区（0x13..0x1c）。后者与提交位 0xc6/0x86 重叠，
    // 只在探针阶段放行，出货路径永远不碰。
    internal static bool IsProbeRegister(byte address) =>
        IsKnownRegister(address) ||
        (address >= LeftBase && address <= LeftBase + 1) ||
        (address >= RightBase && address <= RightBase + 1) ||
        IsZoneAnimRegister(address, LeftBase) || IsZoneAnimRegister(address, RightBase);

    private static bool IsZoneAnimRegister(byte address, byte baseAddress) =>
        address >= baseAddress && address <= baseAddress + ZoneOffsets[3] + ZoneAnimDelta;

    // 冷启动时把当前颜色重新落地的路径（亮度变化只需要重发同一组字节）。
    internal static IReadOnlyList<AyaneoCeiiWrite> BuildBrightnessSequence(
        AyaneoCeiiColor left, AyaneoCeiiColor right, int brightnessPercent) =>
        BuildStaticSequence(left, right, brightnessPercent);

    // 只推颜色、不碰灯效程序的"快帧"。
    // 软件呼吸每帧都要改颜色，而整段程序（使能/pattern/fade/步进/watchdog）是恒定的，
    // 重复下发只会白白多出 34 次提交 × 10ms 收敛等待——那是单帧从 ~260ms 涨到 ~900ms
    // 的唯一原因。固件侧灯效程序一旦写好就保持不变，因此呼吸帧只发 24 个颜色字节。
    internal static IReadOnlyList<AyaneoCeiiWrite> BuildColorSequence(
        AyaneoCeiiColor left, AyaneoCeiiColor right, int brightnessPercent)
    {
        var leftScaled = Scale(left, LeftChannelCeiling, brightnessPercent);
        var rightScaled = Scale(right, RightChannelCeiling, brightnessPercent);
        var writes = new List<AyaneoCeiiWrite>(48);
        foreach (var offset in ZoneOffsets) AddChannel(writes, LeftBase, LeftCommit, offset, leftScaled);
        foreach (var offset in ZoneOffsets) AddChannel(writes, RightBase, RightCommit, offset, rightScaled);
        return writes;
    }

    internal static void Trace(IReadOnlyList<AyaneoCeiiWrite> writes, Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(sink);
        for (var i = 0; i < writes.Count; i++)
        {
            var item = writes[i];
            sink($"0x{item.Address:x2}=0x{item.Data:x2}");
        }
    }

    // 自检用：亮度升高不应让任一通道变暗。
    internal static bool HasMonotonicBrightness(AyaneoCeiiColor color, int ceiling)
    {
        var low = Scale(color, ceiling, 10);
        var high = Scale(color, ceiling, 100);
        return low.R <= high.R && low.G <= high.G && low.B <= high.B;
    }
}
