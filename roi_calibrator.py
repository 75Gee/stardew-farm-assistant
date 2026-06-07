# -*- coding: utf-8 -*-
"""
ROI 屏幕区域校准工具。

功能:
    在整屏预览上拖动滑块，实时调整并预览 ``config.json`` 中的 ROI 矩形，
    使黄框精确覆盖钓鱼小游戏左侧竖条（鱼图标 + 绿色有效区域）。

窗口说明:
    1. ROI overview   整屏缩小预览 + 黄色 ROI 框
    2. ROI crop       ROI 内实际截图（可叠加绿条/鱼识别线）
    3. Adjust         滑块面板（left / top / width / height）

操作（焦点在任一 OpenCV 窗口上）:
    拖动滑块    调整 ROI
    S           保存到 config.json
    R           从 config.json 重新载入
    Q / ESC     退出

用法:
    python roi_calibrator.py
    或双击 run_roi_calibrator.bat
"""

from __future__ import annotations

import json
from pathlib import Path

import cv2
import mss
import numpy as np

from fishing_bot import (
    CONFIG_PATH,
    detect_fish_y,
    detect_green_bar,
    load_config,
    load_fish_template_bundle,
    vision_params,
)


def _bgr_from_capture(arr: np.ndarray) -> np.ndarray:
    """将 mss 截屏的 BGRA 数组转为 BGR；已是 BGR 则复制返回。"""
    if arr.shape[2] == 4:
        return cv2.cvtColor(arr, cv2.COLOR_BGRA2BGR)
    return arr.copy()


def _save_roi_to_config(left: int, top: int, width: int, height: int) -> None:
    """
    将当前 ROI 写入 config.json，保留其余配置项不变。

    参数:
        left, top:     屏幕左上角坐标（像素）。
        width, height: 截取区域宽高（像素）。
    """
    cfg = load_config()
    cfg["roi"] = {"left": left, "top": top, "width": width, "height": height}
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    print(f"已保存 ROI 到 {CONFIG_PATH}: left={left}, top={top}, width={width}, height={height}")


def _draw_detection_preview(
    bgr: np.ndarray,
    gl: np.ndarray,
    gh: np.ndarray,
    fl: np.ndarray,
    fh: np.ndarray,
    vision: dict[str, float],
    fish_mode: str,
    fish_template: np.ndarray | None,
    template_cfg: dict,
) -> np.ndarray:
    """
    在 ROI 裁剪图上叠加绿条矩形与鱼 y 水平线，用于验证 HSV/模板参数。

    返回:
        带标注的 BGR 图像副本。
    """
    vis = bgr.copy()
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    bar = detect_green_bar(hsv, gl, gh, vision=vision)
    fish_y = detect_fish_y(
        hsv,
        fl,
        fh,
        gl,
        gh,
        vision=vision,
        fish_mode=fish_mode,
        bgr=bgr,
        fish_template=fish_template,
        template_cfg=template_cfg,
    )
    h, w = vis.shape[:2]
    if bar is not None:
        y0, y1 = bar
        cv2.rectangle(vis, (0, y0), (w - 1, y1), (0, 255, 0), 2)
        cy = int((y0 + y1) / 2)
        cv2.line(vis, (0, cy), (w - 1, cy), (0, 200, 0), 1)
        cv2.putText(vis, "green bar", (4, max(20, y0 - 6)), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 255, 0), 1)
    else:
        cv2.putText(vis, "green: not found", (4, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 165, 255), 1)
    if fish_y is not None:
        fy = int(fish_y)
        cv2.line(vis, (0, fy), (w - 1, fy), (0, 128, 255), 2)
        cv2.circle(vis, (w // 2, fy), 6, (0, 128, 255), 2)
        cv2.putText(vis, "fish y", (4, min(h - 8, fy + 18)), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 128, 255), 1)
    else:
        cv2.putText(vis, "fish: not found", (4, 44), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 100, 255), 1)
    return vis


def main() -> None:
    """启动 ROI 校准交互循环。"""
    cfg = load_config()
    roi = cfg["roi"]
    gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
    gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
    fl = np.array(cfg["fish_hsv"]["low"], dtype=np.uint8)
    fh = np.array(cfg["fish_hsv"]["high"], dtype=np.uint8)
    vision = vision_params(cfg)
    fish_mode = (cfg.get("fish_detect_mode") or "warm").strip().lower()
    fish_template, template_cfg = load_fish_template_bundle(cfg)
    show_det = bool(cfg.get("debug_visual", {}).get("calibrator_show_detection", True))

    sct = mss.mss()
    mon = sct.monitors[1]  # 主显示器
    mw, mh = mon["width"], mon["height"]

    win_over = "ROI overview (full screen scaled)"
    win_crop = "ROI crop + detection"
    cv2.namedWindow(win_over, cv2.WINDOW_NORMAL)
    cv2.namedWindow(win_crop, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(win_over, min(1280, mw), min(800, mh))

    tb = "Adjust"
    cv2.namedWindow(tb, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(tb, 400, 120)

    # 初始滑块位置：来自 config，并限制在屏幕范围内
    L0, T0, W0, H0 = int(roi["left"]), int(roi["top"]), int(roi["width"]), int(roi["height"])
    L0 = max(0, min(L0, mw - 40))
    T0 = max(0, min(T0, mh - 100))
    W0 = max(40, min(W0, mw - L0))
    H0 = max(120, min(H0, mh - T0))

    cv2.createTrackbar("left", tb, L0, max(1, mw - 1), lambda _: None)
    cv2.createTrackbar("top", tb, T0, max(1, mh - 1), lambda _: None)
    cv2.createTrackbar("width", tb, W0, max(40, mw), lambda _: None)
    cv2.createTrackbar("height", tb, H0, max(120, mh), lambda _: None)

    print("=== ROI 校准工具 ===")
    print("请用鼠标拖动窗口「Adjust」上的滑块，使黄框盖住钓鱼小游戏左侧竖条（鱼和绿色矩形所在列）。")
    print("小窗口「ROI crop」应只看到该竖条；若能看到识别线，说明 HSV 大致有效。")
    print("S 保存到 config.json，R 重新载入配置，Q/ESC 退出。")

    overview_max_w = min(1400, mw)

    while True:
        L = cv2.getTrackbarPos("left", tb)
        T = cv2.getTrackbarPos("top", tb)
        W = cv2.getTrackbarPos("width", tb)
        H = cv2.getTrackbarPos("height", tb)
        # 实时约束：防止 ROI 超出屏幕
        W = max(40, min(W, mw - L))
        H = max(120, min(H, mh - T))
        L = max(0, min(L, mw - W))
        T = max(0, min(T, mh - H))

        raw = np.array(sct.grab(mon))
        full = _bgr_from_capture(raw)

        scale = overview_max_w / full.shape[1]
        small_h = int(full.shape[0] * scale)
        overview = cv2.resize(full, (overview_max_w, small_h))
        Ls, Ts, Ws, Hs = int(L * scale), int(T * scale), max(1, int(W * scale)), max(1, int(H * scale))
        cv2.rectangle(overview, (Ls, Ts), (Ls + Ws, Ts + Hs), (0, 255, 255), 2)
        cv2.putText(
            overview,
            f"L{L} T{T} W{W} H{H} | S save R reload Q quit",
            (8, 24),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            (0, 255, 255),
            1,
        )
        cv2.imshow(win_over, overview)

        crop = full[T : T + H, L : L + W]
        if crop.size == 0:
            crop = np.zeros((120, 80, 3), dtype=np.uint8)
            cv2.putText(crop, "invalid", (4, 60), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
        else:
            if show_det:
                crop = _draw_detection_preview(
                    crop, gl, gh, fl, fh, vision, fish_mode, fish_template, template_cfg
                )
            ch, cw = crop.shape[:2]
            disp_h = min(700, max(200, ch))
            disp_w = int(cw * disp_h / ch)
            disp = cv2.resize(crop, (disp_w, disp_h), interpolation=cv2.INTER_NEAREST)
            crop = disp
        cv2.imshow(win_crop, crop)

        key = cv2.waitKey(30) & 0xFF
        if key in (ord("q"), ord("Q"), 27):
            break
        if key in (ord("s"), ord("S")):
            _save_roi_to_config(L, T, W, H)
        if key in (ord("r"), ord("R")):
            cfg = load_config()
            r = cfg["roi"]
            gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
            gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
            fl = np.array(cfg["fish_hsv"]["low"], dtype=np.uint8)
            fh = np.array(cfg["fish_hsv"]["high"], dtype=np.uint8)
            vision = vision_params(cfg)
            fish_mode = (cfg.get("fish_detect_mode") or "warm").strip().lower()
            fish_template, template_cfg = load_fish_template_bundle(cfg)
            cv2.setTrackbarPos("left", tb, int(r["left"]))
            cv2.setTrackbarPos("top", tb, int(r["top"]))
            cv2.setTrackbarPos("width", tb, int(r["width"]))
            cv2.setTrackbarPos("height", tb, int(r["height"]))
            print("已从 config.json 重新载入 ROI / HSV / vision 参数。")

    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
