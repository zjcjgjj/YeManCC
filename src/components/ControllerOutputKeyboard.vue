<script setup lang="ts">
import { computed, nextTick, onMounted, onBeforeUnmount, ref } from 'vue';
import ControllerKeyboardPreview from '@/components/ControllerKeyboardPreview.vue';
import KeyboardKeyGlyph from '@/components/KeyboardKeyGlyph.vue';
import { focusGamepadElement } from '@/gamepad/focus';
import { nearestSpatialTarget } from '@/gamepad/spatial';
import { pushBackLayer, removeBackLayer, topBackLayer, type BackLayer } from '@/gamepad/backLayers';
import { KEYBOARD_KEY_LAYOUT } from '@/bridge/keyboardKeyLayout';
import { type KeyboardOutputKey, keyboardOutputKeyLabel } from '@/bridge/controllerKeyboardSequence';
import { CONTROLLER_SHORTCUT_ACTIONS } from '@/bridge/controllerShortcutRules';
const props=defineProps<{actionId:string;code:string;fixedAction?:boolean;initialKeys?:KeyboardOutputKey[];chordOnly?:boolean}>();
const emit=defineEmits<{(event:'select',actionId:string,code:string):void;(event:'combo',keys:KeyboardOutputKey[]):void;(event:'close',discarded?:boolean):void}>();
const root=ref<HTMLElement|null>(null),focused=ref(props.code||'KeyA'),symbols=ref(props.actionId==='keyboard.symbol');
const comboMode=ref(!!props.chordOnly||(props.initialKeys?.length||0)>1), selectedKeys=ref<KeyboardOutputKey[]>(props.initialKeys?.map(k=>({...k}))||[]);
const previewInputs=computed(()=>comboMode.value?selectedKeys.value.map(k=>({source:'keyboard' as const,code:k.code})):props.code?[{source:'keyboard' as const,code:props.code}]:[]);
const canConfirm=computed(()=>{const keys=new Set(selectedKeys.value.map(k=>k.code));if(selectedKeys.value.some(k=>k.actionId==='keyboard.symbol'))keys.add('ShiftLeft');return keys.size>0&&keys.size<=4});
function confirmCombo(){if(canConfirm.value)emit('combo',selectedKeys.value.map(k=>({...k})))}
const previous=document.activeElement instanceof HTMLElement?document.activeElement:null;
function outputFor(code:string){const ids=symbols.value?['keyboard.symbol','keyboard.letter','keyboard.numeric','keyboard.leftFunction','keyboard.rightFunction']:['keyboard.letter','keyboard.numeric','keyboard.leftFunction','keyboard.rightFunction'];for(const id of ids){const a=CONTROLLER_SHORTCUT_ACTIONS.find(a=>a.id===id),o=a?.parameters?.find(p=>p.id==='key')?.options?.find(o=>o.value===code);if(o)return{actionId:id,code,label:o.label}}return undefined}
const enabledCodes=computed<string[]>(()=>KEYBOARD_KEY_LAYOUT.filter(k=>outputFor(k.code)).map(k=>k.code));
const label=computed(()=>outputFor(focused.value)?.label||focused.value);
function focusCode(code:string){focused.value=code;void nextTick(()=>focusGamepadElement(root.value?.querySelector<HTMLElement>('[data-key-code="'+code+'"]:is(button)')||null))}
function move(dx:number,dy:number){const keys=Array.from(root.value?.querySelectorAll<HTMLElement>('.keyboard-hit-layer button:not(:disabled)')||[]);const from=keys.find(key=>key.dataset.keyCode===focused.value);if(!from)return false;const target=nearestSpatialTarget(keys,from,{dx:Math.sign(dx) as -1|0|1,dy:Math.sign(dy) as -1|0|1});if(!target?.dataset.keyCode)return false;focusCode(target.dataset.keyCode);return true}
function navigate(dx:number,dy:number){if(move(dx,dy))return;if(dy>0&&comboMode.value)focusGamepadElement(root.value?.querySelector<HTMLElement>('.keyboard-combo-confirm:not(:disabled)')||null);else if(dy<0)focusGamepadElement(root.value?.querySelector<HTMLElement>('header button')||null)}
function spatial(event:Event){
  const e=event as CustomEvent<{dx:number;dy:number}>;
  // The shared engine dispatches on the inline-edit container; direct keyboard
  // events originate from a key. Resolve both within this keyboard's own root.
  const active=(root.value?.getRootNode() as Document|ShadowRoot|undefined)?.activeElement;
  const source=(event.target as Element)?.closest?.('.keyboard-hit-layer')?event.target:active;
  if(!(source instanceof Element)||!root.value?.contains(source)||!source.closest('.keyboard-hit-layer'))return;
  event.preventDefault();navigate(e.detail.dx,e.detail.dy);
}
function keydown(event:KeyboardEvent){const direction:Record<string,[number,number]>={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]};if(event.key==='Escape'){event.preventDefault();event.stopPropagation();cancel()}else if(direction[event.key]&&(event.target as Element)?.closest('.keyboard-hit-layer')){event.preventDefault();navigate(...direction[event.key])}}
// 实体键盘直录：本弹窗原先只认十字键/摇杆与鼠标点击，接上键盘后按什么键都
// 没有反应（看起来像"无法继续映射"）。这里在捕获阶段接一份，抢在按钮的
// 默认行为之前：单键模式按下即选中并返回，组合键模式按下即入列、Enter 确认。
const KEY_DIRECTIONS:Record<string,[number,number]>={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]};
const CONFIRM_CODES=new Set(['Enter','NumpadEnter']);
const MODIFIER_CODES=new Set(['ShiftLeft','ShiftRight','ControlLeft','ControlRight','AltLeft','AltRight','MetaLeft','MetaRight']);
const keyHint=ref('');
function physicalKey(event:KeyboardEvent){
  if(event.isComposing||event.repeat)return;
  const code=event.code;
  if(code==='Tab')return;                       // 保留原生焦点遍历
  if(event.key==='Escape'){event.preventDefault();event.stopPropagation();cancel();return}
  if(KEY_DIRECTIONS[code]){event.preventDefault();event.stopPropagation();navigate(...KEY_DIRECTIONS[code]);return}
  if(CONFIRM_CODES.has(code)&&comboMode.value){event.preventDefault();event.stopPropagation();confirmCombo();return}
  const output=outputFor(code);
  if(!output){keyHint.value=`「${event.key||code}」不在可映射按键表里`;return}
  event.preventDefault();event.stopPropagation();
  keyHint.value='';
  // 单键模式下按修饰键通常是想做组合（Ctrl+S 之类），自动切到组合模式继续收集，
  // 否则第一次按 Ctrl 就被当成单键输出并直接关闭弹窗。
  if(!comboMode.value&&MODIFIER_CODES.has(code))comboMode.value=true;
  if(enabledCodes.value.includes(code))focused.value=code;   // 视觉跟随实体按键
  select(code);
}
// 取消（B / Esc / 点遮罩 / 返回按钮都走这里）：把"有没有未确认的组合键"
// 一起告诉父级。以前是静默关闭，用户按 B 之后刚选的 Ctrl+S 直接消失，
// 看起来像"选了却不生效"。
function cancel(){emit('close',comboMode.value&&selectedKeys.value.length>0)}
// 本弹窗是 B 的最后一层：关闭它只回到「输出动作」区，不该继续往上退。
// 是否轮到本层由 backLayers 栈决定——弹窗把 B 全部吃掉，编辑器就不会再把
// 它当成"没有浮层"而关掉整个编辑器。
const backLayer:BackLayer={handleBack:()=>{if(!root.value?.getClientRects().length)return false;cancel();return true}};
function back(event:Event){
  if(topBackLayer()!==backLayer)return;
  if(!root.value?.getClientRects().length)return;
  event.preventDefault();event.stopImmediatePropagation();backLayer.handleBack();
}
function select(code:string){const output=outputFor(code);if(!output)return;if(!comboMode.value){emit('select',output.actionId,output.code);return}const index=selectedKeys.value.findIndex(k=>k.code===code);if(index>=0)selectedKeys.value.splice(index,1);else if(selectedKeys.value.length<4)selectedKeys.value.push({actionId:output.actionId,code:output.code})}
onMounted(()=>{pushBackLayer(backLayer);window.addEventListener('ipc:gamepad-back',back,true);window.addEventListener('keydown',physicalKey,true);const code=enabledCodes.value.includes(focused.value)?focused.value:enabledCodes.value[0];if(code)focusCode(code)});
onBeforeUnmount(()=>{removeBackLayer(backLayer);window.removeEventListener('ipc:gamepad-back',back,true);window.removeEventListener('keydown',physicalKey,true);if(previous?.isConnected)focusGamepadElement(previous)});
</script>
<template><Teleport to="body"><div class="keyboard-output-overlay" @pointerdown.self="cancel()"><section ref="root" class="keyboard-output-dialog" role="dialog" aria-modal="true" aria-label="选择键盘输出" data-gp-modal="keyboard-output" data-gp-inline-edit @gp:spatial-edit="spatial" @keydown="keydown"><header><div><h3>键盘输出</h3><p>{{fixedAction?'默认快捷：更换键盘输出':'选择要发送的按键（可直接按实体键盘）'}}</p><p v-if="keyHint" class="keyboard-warn">{{keyHint}}</p></div><button v-if="!chordOnly" type="button" :aria-pressed="comboMode" @click="comboMode=!comboMode">{{comboMode?'组合键':'单个按键'}}</button><button type="button" :aria-pressed="symbols" @click="symbols=!symbols">符号层 {{symbols?'开':'关'}}</button><button type="button" class="keyboard-close" @click="cancel()"><span class="controller-key" data-controller-key="B">B</span> 返回</button></header><ControllerKeyboardPreview :inputs="previewInputs" selectable :focused-code="focused" :enabled-codes="enabledCodes" @select="select" @focus-key="focused=$event"/><div v-if="comboMode" class="keyboard-chord"><span>{{selectedKeys.length?selectedKeys.map(keyboardOutputKeyLabel).join(' + '):'选择 1 至 4 个键，例如 Ctrl + S（可直接按实体键盘）'}}</span><button class="keyboard-combo-confirm" type="button" :disabled="!canConfirm" @click="confirmCombo">{{selectedKeys.length?`使用组合按键（${selectedKeys.length}）`:'使用组合按键'}}<span class="keyboard-enter-hint">Enter</span></button></div><footer><span class="keyboard-current"><KeyboardKeyGlyph :code="focused" :label="label"/><b>{{label}}</b></span><span v-if="comboMode">十字键 / 摇杆选键 <span class="controller-key" data-controller-key="A">A</span> 加入或移除 · <b class="keyboard-step">下方向键</b> 移到「使用组合按键」再按 <span class="controller-key" data-controller-key="A">A</span> 确认（实体键盘可按 Enter）· <span class="controller-key" data-controller-key="B">B</span> 返回</span><span v-else>十字键 / 摇杆移动 <span class="controller-key" data-controller-key="A">A</span> 选择（也可直接按实体键盘）· <span class="controller-key" data-controller-key="B">B</span> 返回</span></footer></section></div></Teleport></template>
<style scoped>
/* 层级必须高于快捷动作编辑器（.editor-overlay 1100 / .editor-close-shade 1500）：
   本对话框 Teleport 到 body，写 50 时会被编辑器整屏遮罩压在下面，表现为
   "点键盘按键没反应"——其实弹窗已经打开，只是看不见。 */
.keyboard-output-overlay{position:fixed;inset:0;z-index:1600;display:grid;place-items:center;background:rgba(0,0,0,.65)}.keyboard-output-dialog{width:calc(100% - 72px);max-width:1180px;box-sizing:border-box;padding:22px 24px;border:1px solid var(--border);border-radius:14px;background:var(--bg-solid);color:var(--text);display:grid;gap:22px}.keyboard-output-dialog header{display:flex;align-items:center;gap:12px}.keyboard-output-dialog header>div{flex:1}.keyboard-output-dialog h3{font-size:20px;margin:0 0 6px}.keyboard-output-dialog p{font-size:12px;margin:0;color:var(--text-dim)}.keyboard-output-dialog button:not([data-key-code]){min-height:40px;padding:8px 14px;border:1px solid var(--border);border-radius:7px;background:var(--bg-input);color:var(--text);font:inherit;font-size:12px;cursor:pointer}.keyboard-close b{color:#f04e54;margin-right:5px}.keyboard-output-dialog footer{display:flex;align-items:center;justify-content:space-between;font-size:12px;color:var(--text-dim)}.keyboard-current{display:flex;align-items:center;gap:10px;color:var(--text);font-size:16px}.keyboard-output-dialog footer>span:last-child b{padding:3px 7px;margin:0 5px;background:var(--bg-input);border-radius:50%;color:var(--text)}
.keyboard-chord{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px 14px;border:1px solid var(--accent);border-radius:8px;color:var(--accent);font-size:15px}.keyboard-output-dialog button[aria-pressed=true]{border-color:var(--accent);color:var(--accent)}.keyboard-output-dialog{max-height:calc(100% - 32px);overflow:auto;gap:14px}
.keyboard-warn{color:#f5b93d!important;margin-top:4px!important}
.keyboard-enter-hint{margin-left:8px;padding:2px 7px;border:1px solid currentColor;border-radius:5px;font-size:10px;font-weight:700;opacity:.85}
.keyboard-output-dialog>*{min-width:0}.keyboard-output-dialog header{flex-wrap:wrap}.keyboard-output-dialog header>div{flex:1 0 100%}.keyboard-output-dialog footer{flex-wrap:wrap;gap:8px}
.keyboard-step{padding:2px 7px;border:1px solid var(--border);border-radius:5px;background:var(--bg-input);color:var(--text);font-size:11px;font-weight:700;white-space:nowrap}
</style>
