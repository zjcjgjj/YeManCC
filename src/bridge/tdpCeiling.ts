// Intel TDP 天花板（MCHBAR PL1/PL2）
//
// 为什么需要这一层：
//   本机（AYANEO AIR PLUS Intel / i3-1215U）固件把 MCHBAR +0x59A0 的 PL1 钉在
//   15W，PCODE 对「MSR 0x610」与「MCHBAR」两个来源取 min()。面板现有的 TDP 下发
//   只写 MSR，所以滑块拉到 25W 实际仍是 15W —— 这也是原厂 AYASpace 在 Intel 机上
//   同样调不上去的原因（反汇编确认它只写 MSR）。
//
//   这里补上 MCHBAR 那一侧：把天花板抬到设定值（默认 25W）。之后面板里任何 TDP
//   设置都按 min(MSR, 天花板) 生效，因此：
//     · 面板设 12W → 12W
//     · 面板设 30W → 25W（这就是"游戏时最高不超过 25"）
//
// 接入方式（不是独立开关，而是跟随面板 TDP 设置）：
//   · 每次 TDP 下发成功后调用 ensureTdpCeilingAtLeast(acceptedW)：下发值高于固件
//     15W 时把天花板抬到该值，只升不降（MCHBAR 是上界，低于它的值天然生效）。
//   · 「最大 TDP」下拉（ContinuousPerformancePanel 的 tdpCeiling）决定滑块上限，
//     滑块值再经 ensureTdpCeilingAtLeast 落到 MCHBAR，两者因此自然对齐。
//   · 启动时按设置文件里的 intelCeiling 重新落一次。
//
// 边界：
//   · 天花板只是上限，不替用户决定实际功耗；实际值仍由面板 TDP 滑块/性能调度决定。
//   · 驱动（签名有效的 MMIO 副本）不可用、Secure Boot 开启、或脚本失败时，
//     一律降级为「不干预硬件」——绝不因为这里失败而拦住界面或改动其它设置。
//   · 硬件状态是易失的（重启后固件还原 15W），所以每次启动都要重新落一次。
import { shell } from './api';
import { getPowerControlDir } from './yeman';
import { getSettingsGeneration, readSettingsSection, saveSettingsSection } from './settingsRepository';

/** 可选的档位。上限锁在 25W：用户明确要求"最高不要超过 25"。 */
export const TDP_CEILING_STEPS = [15, 18, 20, 22, 25] as const;
export const TDP_CEILING_DEFAULT = 25;
/** 硬上限。脚本侧还有一层 MAX_ALLOWED=30，这里更严，防止 UI 之外传入更大值。 */
export const TDP_CEILING_HARD_MAX = 25;
/**
 * 固件写在 MCHBAR 的出厂 PL1。低于等于它时不需要干预：MSR 下限天然生效。
 * 本机实测（AYANEO AIR PLUS Intel / i3-1215U）为 15W。
 */
export const TDP_CEILING_FIRMWARE_W = 15;

export interface TdpCeilingConfig {
  /** 是否启用天花板。关闭时脚本不改动硬件（保持固件 15W）。 */
  enabled: boolean;
  /** 天花板瓦数，必须落在 TDP_CEILING_STEPS 内。 */
  watts: number;
  /** PL2（短时爆发）上限，默认与 watts 相同，受 hardMax 钳制。 */
  burstWatts: number;
  /** 本机允许的最大值，默认 25。 */
  hardMax: number;
  /** 兼容标记：用户是否在面板里动过这一项。 */
  configured?: boolean;
}

export interface TdpCeilingState {
  ok: boolean;
  /** applied=刚写入；ok=读取；disabled=未启用；unavailable=驱动不可用；unsupported=非 Intel。 */
  status: 'ok' | 'applied' | 'disabled' | 'unavailable' | 'unsupported' | 'mismatch' | 'error';
  /** MCHBAR +0x59A0 的当前 PL1，0 表示未知。 */
  ceilingW: number;
  ceilingPl2W?: number;
  msrPl1W?: number;
  msrPl2W?: number;
  reason?: string;
}

function clampWatts(value: unknown): number {
  const n = Number(value);
  if (!Number.isFinite(n)) return TDP_CEILING_DEFAULT;
  const stepped = TDP_CEILING_STEPS.find(w => w >= n) ?? TDP_CEILING_DEFAULT;
  return Math.max(TDP_CEILING_STEPS[0], Math.min(TDP_CEILING_HARD_MAX, stepped));
}

export function normalizeTdpCeiling(raw: unknown): TdpCeilingConfig {
  const src = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const watts = clampWatts(src.watts);
  const burst = Number(src.burstWatts);
  return {
    // 默认开启：用户诉求是"打开面板就能用 25W"，未配置时也应当跟随 TDP 下发生效。
    // 非 Intel 平台脚本自身会直接退出，因此开启不会带来跨平台风险。
    enabled: src.enabled !== false,
    watts,
    // PL2 默认比 PL1 高 5W 作为爆发余量，但连同 hardMax 一起受 25W 约束。
    burstWatts: Number.isFinite(burst) ? Math.max(watts, Math.min(TDP_CEILING_HARD_MAX, Math.round(burst)))
      : Math.min(TDP_CEILING_HARD_MAX, watts + 5),
    hardMax: TDP_CEILING_HARD_MAX,
    configured: src.configured === true,
  };
}

export async function readTdpCeilingConfig(): Promise<TdpCeilingConfig> {
  try {
    const tdp = await readSettingsSection<Record<string, unknown>>('tdp');
    return normalizeTdpCeiling(tdp?.intelCeiling);
  } catch {
    return normalizeTdpCeiling(null);
  }
}

export async function saveTdpCeilingConfig(next: TdpCeilingConfig): Promise<TdpCeilingConfig> {
  const normalized = normalizeTdpCeiling({ ...next, configured: true });
  const generation = getSettingsGeneration();
  const current = await readSettingsSection<Record<string, unknown>>('tdp');
  await saveSettingsSection('tdp', { ...current, intelCeiling: { ...normalized } } as Record<string, unknown>, generation);
  return normalized;
}

// ── 脚本调用 ──────────────────────────────────────────────────────────
function ceilingScriptPath(): string {
  return `${getPowerControlDir()}\\mmio\\YeManTdpCeiling.ps1`;
}

/** stdout 恒为单行 JSON；容忍 PowerShell 在它之前写入的杂项行。 */
function parseResult(stdout: string): TdpCeilingState | null {
  const lines = String(stdout ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  for (let i = lines.length - 1; i >= 0; i--) {
    if (!lines[i].startsWith('{')) continue;
    try {
      const j = JSON.parse(lines[i]) as Record<string, unknown>;
      const status = String(j.status ?? '') as TdpCeilingState['status'];
      if (!status) continue;
      return {
        ok: j.ok === true,
        status,
        ceilingW: Number(j.ceilingW) || 0,
        ceilingPl2W: Number.isFinite(Number(j.ceilingPl2W)) ? Number(j.ceilingPl2W) : undefined,
        msrPl1W: Number.isFinite(Number(j.msrPl1W)) ? Number(j.msrPl1W) : undefined,
        msrPl2W: Number.isFinite(Number(j.msrPl2W)) ? Number(j.msrPl2W) : undefined,
        reason: typeof j.reason === 'string' ? j.reason : undefined,
      };
    } catch { /* 继续往前找 */ }
  }
  return null;
}

async function runCeilingScript(args: string[], timeoutMs: number): Promise<TdpCeilingState> {
  try {
    const r = await shell.run('powershell.exe', [
      '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', ceilingScriptPath(),
      ...args,
    ], timeoutMs);
    const parsed = parseResult(r.stdout);
    if (parsed) return parsed;
    const detail = `${r.stderr || r.stdout}`.replace(/\s+/g, ' ').trim().slice(0, 200);
    return { ok: false, status: 'error', ceilingW: 0, reason: detail || `exitCode=${r.exitCode}` };
  } catch (error) {
    return { ok: false, status: 'error', ceilingW: 0, reason: (error as Error).message };
  }
}

// 状态读回做了 6s 缓存：页面刷新/多次挂载不应该反复起进程。
let stateCache: { at: number; value: TdpCeilingState } | null = null;
let stateFlight: Promise<TdpCeilingState> | null = null;
const STATE_TTL_MS = 6000;

export function invalidateTdpCeilingState(): void {
  stateCache = null;
}

export function readTdpCeilingState(force = false): Promise<TdpCeilingState> {
  if (!force && stateCache && Date.now() - stateCache.at < STATE_TTL_MS) return Promise.resolve(stateCache.value);
  if (stateFlight) return stateFlight;
  stateFlight = runCeilingScript(['-Status'], 15000).then(result => {
    stateCache = { at: Date.now(), value: result };
    if (result.ok) observeCeiling(result.ceilingW);
    return result;
  }).finally(() => { stateFlight = null; });
  return stateFlight;
}

/**
 * 把天花板写到指定瓦数。传 0 表示按设置文件里的值应用。
 * 会同时写 MCHBAR 与 MSR 0x610 —— 两者是 min() 关系，只写一个会被另一个压住。
 */
export async function applyTdpCeiling(watts?: number, timeoutMs = 25000): Promise<TdpCeilingState> {
  const args = watts && watts > 0
    ? ['-Watts', String(clampWatts(watts))]
    : [];
  const result = await runCeilingScript(args, timeoutMs);
  invalidateTdpCeilingState();
  if (result.status === 'applied' || result.status === 'ok') {
    stateCache = { at: Date.now(), value: { ...result, status: 'ok' } };
    observeCeiling(result.ceilingW);
  }
  return result;
}

// ── 跟随 TDP 下发：只升不降 ─────────────────────────────────────────────
// MCHBAR 的 PL1 是「上界」：只要它不低于当前 MSR 目标，PCODE 的 min() 就不会压住。
// 因此面板把 TDP 从 25 调到 12 时不需要回写（MSR 自己就限制了功耗），只有往高走
// 才必须抬天花板。这样自动浮动在区间内反复调整时不会每次都起一个 PowerShell 进程。
let knownCeilingW = 0;
let ensureQueue: Promise<void> = Promise.resolve();

/** 记录一次已知的硬件天花板（读回/写入成功后调用），避免重复写。 */
function observeCeiling(watts: number): void {
  const w = Math.round(Number(watts));
  if (Number.isFinite(w) && w > knownCeilingW) knownCeilingW = w;
}

/**
 * 保证固件天花板不低于 watts（受 hardMax 钳制）。
 * 由 setTdp 在硬件下发成功后调用；串行执行，失败静默——它只是让下发值真的生效，
 * 自身失败不能变成一次 TDP 下发失败。
 */
export function ensureTdpCeilingAtLeast(watts: number): Promise<void> {
  const target = Math.min(TDP_CEILING_HARD_MAX, Math.round(Number(watts) || 0));
  // 固件默认就是 15W，等于或低于它时完全不需要动硬件。
  if (target <= TDP_CEILING_FIRMWARE_W) return Promise.resolve();
  ensureQueue = ensureQueue.then(async () => {
    if (target <= knownCeilingW) return;
    // -MinWatts：硬件已经够高时脚本直接返回，不会把天花板写低，也不会重复写寄存器。
    const result = await runCeilingScript(['-MinWatts', String(target)], 25000);
    if (result.ok) observeCeiling(result.ceilingW || target);
  }).catch(() => { /* 降级为不干预 */ });
  return ensureQueue;
}

/** 当前进程已知的硬件天花板（0 = 未知/未抬升），供界面显示。 */
export function knownTdpCeilingWatts(): number {
  return knownCeilingW;
}

export async function revertTdpCeiling(): Promise<TdpCeilingState> {
  const result = await runCeilingScript(['-Revert'], 20000);
  invalidateTdpCeilingState();
  if (result.ok) knownCeilingW = TDP_CEILING_FIRMWARE_W;
  return result;
}

/**
 * 启动时按面板设置重新落一次天花板。永不抛异常——启动链路上的失败只能是提示，
 * 不能拦住界面。返回 null 表示无需动作（未启用）。
 */
export async function syncTdpCeilingOnStartup(): Promise<TdpCeilingState | null> {
  const config = await readTdpCeilingConfig();
  if (!config.enabled) return null;
  return await applyTdpCeiling(config.watts).catch(error => ({
    ok: false, status: 'error' as const, ceilingW: 0, reason: (error as Error).message,
  }));
}
