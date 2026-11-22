#pragma once
#include <atomic>
#include <windows.h>

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x00000011
#endif

// 窗口对截屏/捕获 API 隐身。
//
// 动机：小黄鸭（Lossless Scaling）在 Auto / WindowedMode=false / DXGI 捕获下会按
// 桌面层取帧再整屏缩放输出，浮在上面的 YMCC 浮层会被一起拉大、发糊，缩放后的画面
// 里还会出现重影。设成 WDA_EXCLUDEFROMCAPTURE 后，DXGI 桌面复制与 WGC 都看不到该
// 窗口，缩放工具拿到的帧里没有它，而窗口本身仍由 DWM 正常合成到显示器上——用户看到
// 的是原分辨率的面板，缩放只作用于游戏画面。
//
// 约束（实测）：SetWindowDisplayAffinity 只能由窗口所属进程调用，外部进程一律返回
// FALSE（E_ACCESSDENIED），所以必须在 YMCC 进程内调用。本模块只做这一件事，不参与
// 输入所有权、焦点或可见性判定。
//
// 命名注意：不要叫 apply()——参数里有 std::atomic，ADL 会把 std::apply 拉进重载集，
// MSVC 会选中它并报一堆 tuple 实例化错误。
namespace ymcc::captureexclusion {

// 浮层默认隐身；ui.excludeFromCapture=false 可关闭。
inline std::atomic<bool> overlayEnabled{true};
// 主面板默认不隐身：隐身会让用户自己的截图/录屏里面板变成黑块。
// ui.excludeFromCaptureMain=true 时才启用。
inline std::atomic<bool> mainEnabled{false};

inline bool setExcluded(HWND window, const std::atomic<bool>& allowed) {
    if (!window || !IsWindow(window) || !allowed.load(std::memory_order_acquire)) return false;
    return SetWindowDisplayAffinity(window, WDA_EXCLUDEFROMCAPTURE) != FALSE;
}

inline bool excludeOverlayWindow(HWND window) { return setExcluded(window, overlayEnabled); }
inline bool excludeMainWindow(HWND window) { return setExcluded(window, mainEnabled); }

} // namespace ymcc::captureexclusion
