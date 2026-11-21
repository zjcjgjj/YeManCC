<script setup lang="ts">
// YMCC-AIRPLUS-FIX: read-only live monitor for the four AYANEO dedicated keys
// (AYA / = / LC / RC). It NEVER records, dispatches or rewrites any mapping —
// it only visualises native receipts so a small-screen user can confirm that a
// physical press reached YeManCC and see which standard key chord the firmware
// emits. The real key identity stays the native keyId; labels/chords are display
// only. Configuration is done in the existing shortcut editor (open via the
// "配置" button). Safe-zone component: src/ only, no native/frozen changes.
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { on as onIpc } from '@/bridge/ipc';
import {
  formatControllerShortcutInput,
  type OemKeysSnapshot,
  type OemSpecialKey,
} from '@/bridge/controllerShortcutRules';
import { controllerOemDisplayCatalog } from '@/bridge/controllerOemPresentation';

const props = defineProps<{
  oemKeys: OemKeysSnapshot;
  persona?: string;
}>();
const emit = defineEmits<{ configure: [] }>();

const AYA_KEY_IDS = ['ckbig', 'cksmall', 'cktl', 'cktr'] as const;
const pressed = ref<string[]>([]);
let stopState: (() => void) | null = null;

const display = computed(() => controllerOemDisplayCatalog(props.persona, props.oemKeys));
const keys = computed<OemSpecialKey[]>(() => {
  if (!display.value.supported) return [];
  return AYA_KEY_IDS
    .map(id => display.value.keys.find(key => key.keyId === id))
    .filter((key): key is OemSpecialKey => !!key);
});

/** Standard-key chord this firmware emits for the key (read-only profile expression). */
function chordOf(key: OemSpecialKey): string {
  const rule = key.keyboardContributions?.find(r => r.earlierGroupRules.length === 0) || key.keyboardContributions?.[0];
  if (!rule) return '';
  const parts = rule.keyGroups.map(group => group.map(code => formatControllerShortcutInput({ source: 'keyboard', code })).join('+'));
  return [...parts, ...rule.mouseCodes.map(code => formatControllerShortcutInput({ source: 'mouse', code }))].join(' + ');
}
function isDown(id: string): boolean { return pressed.value.includes(id); }

function onState(payload: unknown): void {
  const state = payload as { oemPressedKeys?: unknown } | null;
  pressed.value = Array.isArray(state?.oemPressedKeys)
    ? state.oemPressedKeys.filter((k): k is string => typeof k === 'string')
    : [];
}

onMounted(() => { stopState = onIpc('gamepad.state', onState); });
onBeforeUnmount(() => { stopState?.(); stopState = null; });
</script>

<template>
  <div v-if="keys.length" class="aya-dedicated-monitor" aria-label="AYANEO 专用键实时状态">
    <div class="aya-monitor-head">
      <span class="aya-monitor-title">专用键实时状态</span>
      <button type="button" class="aya-monitor-config" :title="`在快捷键编辑器中配置 ${keys.length} 个专用键`" @click="emit('configure')">配置</button>
    </div>
    <div class="aya-monitor-grid">
      <div v-for="key in keys" :key="key.keyId" class="aya-key-cell" :class="{ down: isDown(key.keyId), unavailable: key.captureImplemented === false }" :data-oem-key="key.keyId">
        <span class="aya-key-led" aria-hidden="true"></span>
        <span class="aya-key-label">{{ key.label }}</span>
        <span class="aya-key-chord">{{ chordOf(key) || '固件未上报标准键码' }}</span>
        <span class="aya-key-state">{{ isDown(key.keyId) ? '已按下' : '未按下' }}</span>
      </div>
    </div>
    <p class="aya-monitor-hint">按下任一键验证是否收到反馈；键码为固件/AYASpace 映射后的标准组合键，非 EC 原始码。自定义动作请在「配置」中设置。</p>
  </div>
</template>

<style scoped>
.aya-dedicated-monitor{margin-top:10px;padding:10px;border:1px solid rgba(139,159,186,.16);border-radius:8px;background:var(--bg-input,rgba(139,159,186,.04))}
.aya-monitor-head{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px}
.aya-monitor-title{font-size:11px;font-weight:700;color:var(--text-dim)}
.aya-monitor-config{padding:3px 9px;border:1px solid color-mix(in srgb,var(--accent) 45%,transparent);border-radius:6px;background:color-mix(in srgb,var(--accent) 10%,var(--bg-input));color:var(--accent);font:inherit;font-size:11px;font-weight:700;cursor:pointer}
.aya-monitor-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px}
.aya-key-cell{display:grid;grid-template-rows:auto auto auto auto;justify-items:center;gap:2px;min-width:0;padding:7px 4px;border:1px solid rgba(139,159,186,.16);border-radius:7px;text-align:center;transition:border-color .1s,background .1s}
.aya-key-cell.down{border-color:var(--accent);background:color-mix(in srgb,var(--accent) 14%,var(--bg-input))}
.aya-key-cell.unavailable{opacity:.5}
.aya-key-led{width:6px;height:6px;border-radius:50%;background:#667282}
.aya-key-cell.down .aya-key-led{background:var(--accent);box-shadow:0 0 6px var(--accent)}
.aya-key-label{min-width:0;max-width:100%;font-size:11px;font-weight:700;color:var(--text);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.aya-key-chord{min-width:0;max-width:100%;font-size:10px;color:var(--text-dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.aya-key-state{font-size:10px;color:var(--text-dim)}
.aya-key-cell.down .aya-key-state{color:var(--accent)}
.aya-monitor-hint{margin:8px 0 0;font-size:10px;line-height:1.5;color:var(--text-dim)}
@media(max-width:520px){.aya-monitor-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
</style>
