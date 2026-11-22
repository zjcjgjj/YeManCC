<script setup lang="ts">
interface Opt {
  value: string | number;
  label: string;
  detail?: string;
}
const props = withDefaults(
  defineProps<{
    modelValue: string | number;
    options: Opt[];
    color?: 'ac' | 'dc' | 'accent';
    full?: boolean;
    disabled?: boolean;
    wrap?: boolean;
    /**
     * 固定列数（>0 时生效）。换成等宽网格而不是 flex 换行：
     * flex 换行是"贪心"的，最后一行剩下的项会被拉满整宽，显得又宽又乱；
     * 网格让每格等宽，行数由列数决定，永远不会溢出或出现畸形宽格。
     * 小屏 6 个档位用 3 列，正好两行。
     */
    columns?: number;
    gpRow?: number | string;
    gpColStart?: number | string;
  }>(),
  { color: 'accent', full: false, disabled: false, wrap: false }
);
const emit = defineEmits<{ (e: 'update:modelValue', v: string | number): void }>();
function pick(v: string | number) {
  if (props.disabled) return;
  emit('update:modelValue', v);
}
const accentVar = props.color === 'dc' ? 'var(--dc-accent)' : 'var(--accent)';
</script>

<template>
  <div
    class="seg"
    :class="{ full: full, disabled: disabled, wrap: wrap, grid: columns > 0 }"
    :style="{ '--seg-accent': accentVar, '--seg-cols': String(columns > 0 ? columns : 1) }"
  >
    <button
      v-for="(o, i) in options"
      :key="o.value"
      type="button"
      class="seg-btn"
      :class="{ active: o.value === modelValue }"
      :disabled="disabled"
      :data-gp-row="gpRow"
      :data-gp-col="gpRow == null ? undefined : Number(gpColStart ?? 0) + i"
      @click="pick(o.value)"
    >
      <span class="seg-main">{{ o.label }}</span>
      <span v-if="o.detail" class="seg-detail">{{ o.detail }}</span>
    </button>
  </div>
</template>

<style scoped>
.seg {
  display: inline-flex;
  align-items: center;
  background: transparent;
  padding: 0;
  gap: 7px;
  flex-wrap: nowrap;
}
.seg.full {
  display: flex;
  width: 100%;
}
.seg.full .seg-btn {
  flex: 1 1 0;
  min-width: 0;
}
/* [YMCC-AIRPLUS-FIX] wrap mode: let options reflow onto multiple rows on narrow
   screens so labels are never squeezed into ellipsis. Used by the font-size card. */
.seg.wrap {
  flex-wrap: wrap;
  row-gap: 8px;
}
/* 等宽网格模式：列数固定、每格等宽，行由数量决定，不会出现被拉满的畸形末格。 */
.seg.grid {
  display: grid;
  grid-template-columns: repeat(var(--seg-cols, 1), minmax(0, 1fr));
  gap: 7px;
  row-gap: 8px;
  width: 100%;
}
.seg.grid .seg-btn {
  flex: 0 0 auto;
  min-width: 0;
  min-height: 40px;
  padding: 0 6px;
  font-size: 14px;
}
.seg.grid .seg-main {
  overflow: visible;
  text-overflow: clip;
}
.seg.full.wrap .seg-btn {
  flex: 1 0 auto;
  min-width: 62px;
}
.seg.wrap .seg-btn {
  font-size: 14px;
  min-height: 42px;
  padding: 0 10px;
}
.seg.wrap .seg-main {
  overflow: visible;
  text-overflow: clip;
}
.seg-btn {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 2px;
  min-height: 38px;
  padding: 0 8px;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 8px;
  background: var(--bg-input);
  color: var(--text);
  font-size: 11px;
  font-weight: 700;
  line-height: var(--btn-line-height);
  cursor: pointer;
  white-space: nowrap;
  transition: background 0.12s, color 0.12s, border-color 0.12s;
}
.seg-main {
  line-height: 1.15;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}
.seg-detail {
  max-width: 100%;
  font-size: 10px;
  line-height: 1.15;
  font-weight: 500;
  opacity: 0.78;
  text-align: center;
  white-space: normal;
  font-variant-numeric: tabular-nums;
}
.seg-btn:hover:not(:disabled) {
  color: var(--text);
}
.seg-btn.active {
  color: var(--accent);
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
  background: color-mix(in srgb, var(--accent) 10%, var(--bg-input));
  font-weight: 700;
}
.seg-btn:focus-visible {
  box-shadow: var(--focus-ring);
}
.seg.disabled {
  opacity: 0.45;
  pointer-events: none;
}
.seg.disabled .seg-btn {
  cursor: default;
}
</style>
