// B 键返回的层级仲裁。
//
// 快捷动作编辑器里可能同时叠着两层浮层：输出动作选择面板（内嵌，或 library
// 变体下的浮层）和键盘输出对话框（Teleport 到 body）。手柄引擎只派发一次
// ipc:gamepad-back，而 window 上的监听器在 target 阶段按注册顺序执行，先挂载
// 的组件会抢在更上层之前处理。
//
// 真实的故障链：按 B 时编辑器先判断「有没有浮层」，它用的是
// dialogEl.querySelector('[data-gp-modal="keyboard-output"]')，而键盘弹窗被
// Teleport 到 body、不在 dialogEl 内 —— 于是判定为「没有浮层」，直接走了关闭
// 编辑器的分支。用户看到的就是「在键盘输出界面按 B，一下退回最初的界面」。
//
// 这个栈只回答一个问题：现在最上层是谁。栈顶消费 B；栈不空时编辑器不自行关闭。
export interface BackLayer {
  /** 返回 true 表示这次 B 已消费；false 表示交回下一层。 */
  handleBack(): boolean;
}

const layers: BackLayer[] = [];

export function pushBackLayer(layer: BackLayer): void {
  removeBackLayer(layer);
  layers.push(layer);
}

export function removeBackLayer(layer: BackLayer): void {
  const index = layers.lastIndexOf(layer);
  if (index >= 0) layers.splice(index, 1);
}

export function topBackLayer(): BackLayer | undefined {
  return layers[layers.length - 1];
}

export function hasBackLayer(): boolean {
  return layers.length > 0;
}
