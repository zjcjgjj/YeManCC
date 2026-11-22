// 启动灯光对账：把「面板里保存的灯光设置」在每次打开软件时重新落一遍。
//
// 为什么需要它：
//   灯光是 EC/MCU 上的易失状态。重启、断电、被 AYASpace 抢过控制权之后，摇杆环会
//   停在固件默认色上，而面板里仍然显示着用户上次选的「正红 / 天蓝 · 呼吸 70%」——
//   用户看到的就是"面板说的和机器实际的不一致"。设置页只在自己挂载时才下发，
//   所以只要不点开那个页面，机器就一直停在错的颜色上。
//
// 这里只做「按已保存设置重新下发」，不改任何设置值，也不做猜测性写硬件：
//   - 能力未声明 / 后端未实现  → 跳过（回到旧行为，不报错）
//   - 灯光来源是原厂(oem)      → 跳过（原厂灯效归原厂管，不能拿缓存值覆盖）
//   - 下发失败                → 如实回报原因，交给调用方提示
//
// 读回验证的边界：CEii 的灯光寄存器读回恒为 0（aya-light-probe.log 里
// 0xb2..0xc5 / 0x72..0x85 全 0x00），本机没有可靠的读数通路，因此这里的"一致"
// 指"已把面板设置重新写成硬件当前状态"，而不是"读回值等于设置值"。
import { gamepadFeedback } from './api';
import { loadSettings } from './settingsRepository';

export interface StartupLightingSyncResult {
  /** applied = 已下发；skipped = 本机无灯光/未接管/读取失败；failed = 下发被拒。 */
  status: 'applied' | 'skipped' | 'failed';
  /** skipped/failed 时的人话原因。 */
  reason?: string;
  /** 下发时使用的灯效与亮度，供提示文案复述。 */
  mode?: string;
  brightness?: number;
  /** 独立主副色的机型（本机为左右环分色）才提示分色语义。 */
  splitRings?: boolean;
}

type LedSettings = {
  ownership?: string;
  enabled?: boolean;
  off?: boolean;
  mode?: string;
  presetId?: string;
  color?: string;
  color2?: string;
  speed?: number;
  brightness?: number;
};

const HEX = /^#[0-9a-fA-F]{6}$/;

function readLedSettings(raw: unknown): LedSettings {
  if (!raw || typeof raw !== 'object') return {};
  const lighting = (raw as { lighting?: unknown }).lighting;
  return lighting && typeof lighting === 'object' ? (lighting as LedSettings) : {};
}

function byte(value: unknown, fallback: number): number {
  const n = Number(value);
  return Number.isFinite(n) ? Math.min(100, Math.max(0, Math.round(n))) : fallback;
}

/**
 * 启动时按面板设置重新下发灯光。永不抛异常——启动链路上的失败只能是提示，
 * 不能拦住界面。
 */
export async function syncLightingFromSettings(): Promise<StartupLightingSyncResult> {
  let led: LedSettings;
  try {
    const all = await loadSettings();
    led = readLedSettings((all as { gamepad?: { feedback?: unknown } }).gamepad?.feedback);
  } catch (error) {
    return { status: 'skipped', reason: `读取设置失败：${(error as Error).message}` };
  }

  // 用户明确选了原厂灯光：原厂态由固件/AYASpace 维持，缓存值不能反向覆盖。
  if (led.ownership === 'oem') return { status: 'skipped', reason: 'oem-ownership' };
  // 从未启用过灯光（新装机默认 enabled=false / off=false）：不去动固件默认灯效。
  // 否则第一次打开软件就会把机器自己的灯效改掉，用户会认为是软件乱写硬件。
  // 反过来，只要用户表达过"要灯"（enabled）或"要关灯"（off），就按面板落地。
  if (led.enabled !== true && led.off !== true) return { status: 'skipped', reason: 'lighting-not-configured' };

  let caps;
  try {
    caps = await gamepadFeedback.capabilities();
  } catch (error) {
    return { status: 'skipped', reason: `能力查询失败：${(error as Error).message}` };
  }
  if (!caps.ledAvailable) return { status: 'skipped', reason: 'led-unavailable' };

  const mode = typeof led.mode === 'string' && led.mode ? led.mode : 'solid';
  const color = HEX.test(String(led.color)) ? String(led.color) : undefined;
  const color2 = HEX.test(String(led.color2)) ? String(led.color2) : undefined;
  const brightness = byte(led.brightness, 70);
  const speed = byte(led.speed, 65);
  const enabled = led.enabled !== false;
  // 与面板 applyLed 完全同构：off = 亮度归零但效果保持；关总开关 = 整灯熄。
  const appliedBrightness = enabled ? (led.off ? 0 : brightness) : 0;

  try {
    const result = await gamepadFeedback.setLed({
      enabled: enabled && !led.off,
      mode,
      presetId: mode === 'preset' ? led.presetId : undefined,
      color,
      color2,
      speed,
      brightness: appliedBrightness,
    });
    if (!result.ok) {
      return { status: 'failed', reason: result.reason ?? '未知原因', mode, brightness: appliedBrightness };
    }
    return {
      status: 'applied',
      mode,
      brightness: appliedBrightness,
      splitRings: caps.supportsSecondColor === true,
    };
  } catch (error) {
    return { status: 'failed', reason: (error as Error).message, mode, brightness: appliedBrightness };
  }
}
