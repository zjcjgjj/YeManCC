using System.IO;

// AYANEO CEii 原生灯光写入器的离线自检：只断言要写入的字节，不接触任何硬件。
// 期望值来源：寄存器布局取 ShadowBlip/ayaneo-platform 的 air_plus 分支
// （zones {3,6,9,12}、pattern 0x0f / fade 0x10 / anim 0x11-0x14 / watchdog 0x15），
// 量程取 AYASpace 3.2.0.4 官方实现（0..255 线性映射到 0..192，左右同值，不做
// 厂商驱动的 64/32 二次收窄）。因此这里的断言等价于把该协议钉在仓库里。
internal static class AyaneoCeiiLightingTests
{
    private static int checks;
    private static void Check(bool yes, string why) { ++checks; if (!yes) throw new Exception(why); }

    // 颜色块每通道一条数据 + 一次提交，共 6 条。
    private static readonly byte[] LeftZoneStarts = [0xb3, 0xb6, 0xb9, 0xbc];
    private static readonly byte[] RightZoneStarts = [0x73, 0x76, 0x79, 0x7c];

    internal static int Run()
    {
        checks = 0;

        // 1) 量程：对齐 AYASpace 官方的 192 体系，左右同值。
        Check(AyaneoCeiiLighting.ScaleChannel(255, AyaneoCeiiLighting.LeftChannelCeiling, 100) == 192, "left full-scale");
        Check(AyaneoCeiiLighting.ScaleChannel(255, AyaneoCeiiLighting.RightChannelCeiling, 100) == 192, "right full-scale matches left");
        Check(AyaneoCeiiLighting.ScaleChannel(255, AyaneoCeiiLighting.LeftChannelCeiling, 50) == 96, "half brightness halves the byte");
        Check(AyaneoCeiiLighting.ScaleChannel(0, AyaneoCeiiLighting.LeftChannelCeiling, 100) == 0, "black stays black");
        Check(AyaneoCeiiLighting.ScaleChannel(1, AyaneoCeiiLighting.RightChannelCeiling, 100) == 1, "dim input never collapses to 0");

        // 2) 亮度单调：滑块调高不能让任何通道变暗。
        for (var channel = 0; channel <= 255; channel += 5)
            for (var low = 1; low < 91; low += 9)
                Check(AyaneoCeiiLighting.ScaleChannel(channel, AyaneoCeiiLighting.LeftChannelCeiling, low) <=
                      AyaneoCeiiLighting.ScaleChannel(channel, AyaneoCeiiLighting.LeftChannelCeiling, low + 9),
                      $"monotonic {channel}@{low}");

        // 3) 左右同色同亮度时，两侧字节必须完全一致——旧实现按 64/32 分别收窄，
        //    同一颜色右灯只有左灯一半亮度，正是"副灯颜色不一致"的根因。
        var same = new AyaneoCeiiColor(200, 120, 60);
        var leftScaled = AyaneoCeiiLighting.Scale(same, AyaneoCeiiLighting.LeftChannelCeiling, 100);
        var rightScaled = AyaneoCeiiLighting.Scale(same, AyaneoCeiiLighting.RightChannelCeiling, 100);
        Check(leftScaled == rightScaled, "left and right scale identically");

        // 4) 常亮序列：模式取用 + 两侧动效程序（含输出使能）+ 两侧各 4 分区 × 3 通道。
        var red = new AyaneoCeiiColor(255, 0, 0);
        var green = new AyaneoCeiiColor(0, 255, 0);
        var solid = AyaneoCeiiLighting.BuildStaticSequence(red, green, 100);
        Check(solid.Count == 80, "solid sequence length");
        Check(solid[0].Address == 0xb2 && solid[0].Data == 0x80, "first payload byte is the left enable");

        var leftProgram = solid.Take(16).ToArray();
        Check(leftProgram.SequenceEqual(new AyaneoCeiiWrite[]
        {
            new(0xb2, 0x80), new(0xc6, 0x01),   // 输出使能（缺它灯不亮）
            new(0xbf, 0x00), new(0xc6, 0x01),   // pattern off
            new(0xc0, 0x00), new(0xc6, 0x01),   // fade off
            new(0xc1, 0x05), new(0xc6, 0x01),   // anim 1 static
            new(0xc2, 0x05), new(0xc6, 0x01),
            new(0xc3, 0x05), new(0xc6, 0x01),
            new(0xc4, 0x05), new(0xc6, 0x01),
            new(0xc5, 0x07), new(0xc6, 0x01),   // watchdog on
        }), "left effect program");

        var rightProgram = solid.Skip(16).Take(16).ToArray();
        Check(rightProgram.SequenceEqual(new AyaneoCeiiWrite[]
        {
            new(0x72, 0x80), new(0x86, 0x01),
            new(0x7f, 0x00), new(0x86, 0x01),
            new(0x80, 0x00), new(0x86, 0x01),
            new(0x81, 0x05), new(0x86, 0x01),
            new(0x82, 0x05), new(0x86, 0x01),
            new(0x83, 0x05), new(0x86, 0x01),
            new(0x84, 0x05), new(0x86, 0x01),
            new(0x85, 0x07), new(0x86, 0x01),
        }), "right effect program");

        // 5) 颜色块：左侧 24 条之后是右侧 24 条，逐个分区核对 R/G/B 与提交。
        //    左环吃第一个颜色参数（主色），右环吃第二个（副色），两组各 4 个分区。
        var colors = solid.Skip(32).ToArray();
        Check(colors.Length == 48, "color block length");
        var redLeft = AyaneoCeiiLighting.Scale(red, AyaneoCeiiLighting.LeftChannelCeiling, 100);
        var greenRight = AyaneoCeiiLighting.Scale(green, AyaneoCeiiLighting.RightChannelCeiling, 100);
        for (var zone = 0; zone < 4; zone++)
        {
            var leftBase = zone * 6;
            CheckChannel(colors, leftBase, LeftZoneStarts[zone], redLeft, 0xc6, $"left zone {zone}");
        }
        for (var zone = 0; zone < 4; zone++)
        {
            var rightBase = 24 + zone * 6;
            CheckChannel(colors, rightBase, RightZoneStarts[zone], greenRight, 0x86, $"right zone {zone}");
        }
        // 左右两环走同一量程：左红 192、右绿 192。
        Check(colors[0].Data == 192 && colors[26].Data == 192, "left and right share one ceiling");

        // 6) 右环第 4 区（0x7C）同样是摇杆环的一部分，通道顺序与其余分区一致，都是 R,G,B。
        //    2026-11-22 实机照片推翻了"0x7C = AYA 标志灯、按 B,R,G 下发"的旧结论：
        //    整机只有左右两个环会亮，没有独立标志灯；按 B,R,G 写会把天蓝 #2ea6ff 拆成
        //    洋红 #FF2EA6，用户拍到的"摇杆环上半圈发粉白光"就是它。
        var zone4Base = 24 + 3 * 6;
        Check(colors[zone4Base].Address == 0x7c, "right zone 4 base");
        var probe = new AyaneoCeiiColor(255, 128, 64);
        var probeSeq = AyaneoCeiiLighting.BuildStaticSequence(probe, probe, 100);
        var probeRight = AyaneoCeiiLighting.Scale(probe, AyaneoCeiiLighting.RightChannelCeiling, 100);
        var probeWrites = probeSeq.Skip(32 + zone4Base).Take(6).ToArray();
        Check(probeWrites[0].Address == 0x7c && probeWrites[2].Address == 0x7d && probeWrites[4].Address == 0x7e,
            "zone 4 channels land on 0x7c/0x7d/0x7e");
        Check(probeWrites[0].Data == probeRight.R, "0x7c carries red (R,G,B, not B,R,G)");
        Check(probeWrites[2].Data == probeRight.G, "0x7d carries green");
        Check(probeWrites[4].Data == probeRight.B, "0x7e carries blue");
        // 三个通道值必须互不相同，否则上面的顺序断言等于没测。
        Check(probeRight.R != probeRight.G && probeRight.G != probeRight.B && probeRight.R != probeRight.B,
            "probe color resolves to three distinct channel bytes");

        // 6b) 主副色分环：主色只落左环、副色只落右环，四个分区全都要改。
        //     旧实现两个环都刷主色、只把 0x7C 一区刷副色——副色既只占八分之一，
        //     又被通道互换染色，用户体感就是"主副不能单独调整"。
        var ringMain = new AyaneoCeiiColor(255, 0, 0);
        var ringSecond = new AyaneoCeiiColor(0, 0, 255);
        var zoned = AyaneoCeiiLighting.BuildStaticSequence(ringMain, ringSecond, 100);
        var zonedColors = zoned.Skip(32).ToArray();
        var expectMain = AyaneoCeiiLighting.Scale(ringMain, AyaneoCeiiLighting.LeftChannelCeiling, 100);
        var expectSecond = AyaneoCeiiLighting.Scale(ringSecond, AyaneoCeiiLighting.RightChannelCeiling, 100);
        for (var zone = 0; zone < 4; zone++)
            CheckChannel(zonedColors, zone * 6, LeftZoneStarts[zone], expectMain, 0xc6, $"split left {zone}");
        for (var zone = 0; zone < 4; zone++)
            CheckChannel(zonedColors, 24 + zone * 6, RightZoneStarts[zone], expectSecond, 0x86, $"split right {zone}");
        // 副色（蓝）在右环必须写成 R=0,G=0,B=192：0x7e 蓝满、0x7c 红为 0。
        Check(zonedColors[24 + 3 * 6 + 4].Data == expectSecond.B, "right zone 4 keeps secondary blue on 0x7e");
        Check(zonedColors[24 + 3 * 6].Data == expectSecond.R, "right zone 4 keeps secondary red on 0x7c");

        // 7) 呼吸：只有 pattern 位变化，其余字节与常亮完全一致。
        var breathing = AyaneoCeiiLighting.BuildSequence(red, green, 100, 0x02, AyaneoCeiiLighting.FadeOff);
        Check(breathing.Count == solid.Count, "breathing keeps the same write count");
        var diffs = breathing.Zip(solid).Where(pair => pair.First != pair.Second).ToArray();
        Check(diffs.Length == 2, "breathing touches only the two pattern registers");
        Check(diffs[0].First == new AyaneoCeiiWrite(0xbf, 0x02), "left pattern register carries the value");
        Check(diffs[1].First == new AyaneoCeiiWrite(0x7f, 0x02), "right pattern register carries the value");

        // 8) 寄存器白名单：只能落在灯光区，风扇/电源区一律拒绝。
        foreach (var allowed in new byte[] { 0x87, 0xc6, 0x86, 0xb2, 0x72, 0xb3, 0xbe, 0xbf, 0xc5, 0x73, 0x7e, 0x7f, 0x85 })
            Check(AyaneoCeiiLighting.IsKnownRegister(allowed), $"allowed 0x{allowed:x2}");
        foreach (var rejected in new byte[] { 0x00, 0x02, 0x04, 0x18, 0xb0, 0xb1, 0x70, 0x71, 0xc8, 0xd1, 0xff })
            Check(!AyaneoCeiiLighting.IsKnownRegister(rejected), $"rejected 0x{rejected:x2}");

        // 9) 下发：每字节前补取用握手，提交后停顿；失败必须如实返回 false。
        var trace = new List<AyaneoCeiiWrite>();
        var okWriter = new AyaneoCeiiLighting((address, data) => { trace.Add(new(address, data)); return true; });
        Check(okWriter.Apply(solid), "apply succeeds");
        var dataBytes = solid.Count(item => item.Address is not (0xc6 or 0x86));
        Check(trace.Count == solid.Count + dataBytes, "one handshake per data byte");
        Check(trace.Where(item => item.Address != 0x87).SequenceEqual(solid), "apply preserves payload order");
        Check(trace[0].Address == 0x87, "stream starts with handshake");
        Check(trace.Count(item => item.Address == 0x87) == dataBytes, "exactly one handshake per data byte");
        // 提交字节紧跟数据字节，不允许被握手插在中间（与冻结实现的逐字节节奏一致）。
        for (var i = 1; i < trace.Count; i++)
            if (trace[i].Address is 0xc6 or 0x86)
                Check(trace[i - 1].Address != 0x87, $"commit never preceded by handshake at {i}");
        var failAt = 0;
        var failing = new AyaneoCeiiLighting((_, _) => ++failAt < 5);
        Check(!failing.Apply(solid), "apply reports failure");
        Check(failAt == 5, "apply stops at the first failure");
        Check(!new AyaneoCeiiLighting((_, _) => false).Release(), "release reports failure");

        // 10) Trace 只用于诊断，不改变顺序。
        var lines = new List<string>();
        AyaneoCeiiLighting.Trace(solid.Take(2).ToArray(), lines.Add);
        Check(lines.Count == 2 && lines[0] == "0xb2=0x80" && lines[1] == "0xc6=0x01", "trace formatting");

        // 11) 呼吸：AYASpace 3.2.0.4 逆向得到的动效取值。
        Check(AyaneoCeiiLighting.PatternBreathing == 0xe7, "breathing pattern is 0xe7");
        Check(AyaneoCeiiLighting.AnimBreathing == 0x07, "breathing per-zone step is 7");
        Check(AyaneoCeiiLighting.WatchdogBreathing == 0x04, "breathing watchdog is 4");
        Check(AyaneoCeiiLighting.DefaultGlobalColorCeiling == 192, "default global ceiling is 192 (AYASpace scale)");
        Check(AyaneoCeiiLighting.GlobalColorCeiling == 192, "global ceiling starts at the default");
        AyaneoCeiiLighting.GlobalColorCeiling = 4096;
        Check(AyaneoCeiiLighting.GlobalColorCeiling == 255, "global ceiling clamps high");
        AyaneoCeiiLighting.GlobalColorCeiling = 0;
        Check(AyaneoCeiiLighting.GlobalColorCeiling == 32, "global ceiling clamps low");
        AyaneoCeiiLighting.GlobalColorCeiling = AyaneoCeiiLighting.DefaultGlobalColorCeiling;

        // 12) 呼吸快帧只碰颜色区，绝不重复下发灯效程序——重复下发会让单帧从 ~90ms
        //     涨到 ~400ms，12fps 直接掉到 2.5fps。
        var fast = AyaneoCeiiLighting.BuildColorSequence(same, same, 100).ToArray();
        Check(fast.Length == 48, "fast frame carries 24 data bytes plus 24 commits");
        Check(fast.All(w => w.Address != 0x87), "fast frame never handshakes");
        Check(fast.All(w => w.Address is not (0xb2 or 0x72)), "fast frame never touches enable");
        Check(fast.All(w => w.Address is not (0xbf or 0x7f)), "fast frame never touches pattern");
        Check(fast.All(w => w.Address is not (0xc5 or 0x85)), "fast frame never touches watchdog");
        Check(fast.Count(w => w.Address is 0xc6 or 0x86) == 24, "fast frame commits per colour byte");
        Check(fast[0].Address == 0xb3 && fast[1].Address == 0xc6, "fast frame keeps byte-then-commit pairs");

        // 13) 批量下发：一次握手、末尾两组各提交一次，数据字节顺序不变。
        var burstTrace = new List<(byte Address, byte Data)>();
        var burstWriter = new AyaneoCeiiLighting((a, d) => { burstTrace.Add((a, d)); return true; });
        Check(burstWriter.ApplyBurst(fast, 0), "burst apply succeeds");
        Check(burstTrace.Count(step => step.Address == 0x87) == 3, "burst handshakes three times");
        Check(burstTrace.Last().Address == 0x86 && burstTrace.Last().Data == 1, "burst ends on the right commit");
        var burstData = burstTrace.Where(step => step.Address is not (0x87 or 0xc6 or 0x86)).ToArray();
        var pairData = fast.Where(step => step.Address is not (0xc6 or 0x86)).ToArray();
        Check(burstData.Length == pairData.Length, "burst carries every data byte");
        for (var i = 0; i < burstData.Length; i++)
            Check(burstData[i] == (pairData[i].Address, pairData[i].Data), $"burst keeps data order at {i}");
        var burstFailAt = 0;
        Check(!new AyaneoCeiiLighting((_, _) => ++burstFailAt < 3).ApplyBurst(fast, 0),
            "burst reports failure");

        return checks;
    }

    private static void CheckChannel(AyaneoCeiiWrite[] writes, int offset, byte start, AyaneoCeiiColor expect,
        byte commit, string label)
    {
        Check(writes[offset].Address == start && writes[offset].Data == expect.R, label + " red");
        Check(writes[offset + 1].Address == commit && writes[offset + 1].Data == 1, label + " red commit");
        Check(writes[offset + 2].Address == (byte)(start + 1) && writes[offset + 2].Data == expect.G, label + " green");
        Check(writes[offset + 3].Address == commit && writes[offset + 3].Data == 1, label + " green commit");
        Check(writes[offset + 4].Address == (byte)(start + 2) && writes[offset + 4].Data == expect.B, label + " blue");
        Check(writes[offset + 5].Address == commit && writes[offset + 5].Data == 1, label + " blue commit");
    }
}
