# -*- coding: utf-8 -*-
"""
星露谷谷物语 · 钓鱼小游戏自动辅助（主程序）。

===============================================================================
游戏机制（与官方维基一致）
===============================================================================
- 钓鱼小游戏左侧有一条竖向槽，槽内「鱼图标」上下移动。
- 绿色矩形为「有效区域」：鱼必须落在此区域内才算成功。
- 按住「使用工具」键（本脚本映射为键盘 ``c``）时绿条上升并带惯性；松开后下落。

===============================================================================
技术架构
===============================================================================
1. 屏幕采集   ``mss`` 按 ``config.json`` 中的 ROI 截取竖条区域。
2. 视觉识别   OpenCV HSV 分割 / 模板匹配，得到鱼 y 与绿条中心 y。
3. 闭环控制   PD 控制器或 ARX-K（Ackermann 极点配置）计算按住/松开。
4. 输入模拟   ``pynput`` 模拟键盘 ``c``（星露谷默认使用工具键）。
5. 图形界面   Tkinter 实时预览、日志、绿条系统辨识入口。

===============================================================================
配套工具（同目录）
===============================================================================
- ``roi_calibrator.py``       校准 ROI 屏幕坐标
- ``template_mark_tool.py``   制作鱼图标模板
- ``debug_recognition.py``    离线调试图像识别
- ``bar_system_id.py``        绿条 ARX 系统辨识与 K 增益设计

===============================================================================
热键（全局，默认）
===============================================================================
- F8  开始 / 暂停辅助（暂停时不发送按键，识别仍运行）
- F9  退出程序
- F7  采样 ROI 中心 HSV 并写入日志（用于调 ``green_hsv`` / ``fish_hsv``）

===============================================================================
免责声明
===============================================================================
自动化操作可能违反游戏服务条款，本项目仅供计算机视觉与控制算法学习交流。
请自行评估风险，作者不对任何账号封禁或损失负责。
"""

from __future__ import annotations

import json
import math
import queue
import re
import sys
import threading
import time
import traceback
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import cv2
import mss
import numpy as np
from pynput import keyboard
from pynput.keyboard import Controller as KeyboardController

from image_io import imread_unicode

try:
    import tkinter as tk
    from tkinter import messagebox, scrolledtext, ttk

    from PIL import Image, ImageTk
except ImportError as e:
    print("缺少 GUI 依赖，请执行: python -m pip install pillow")
    raise SystemExit(1) from e


# =============================================================================
# 配置与路径
# =============================================================================

CONFIG_PATH = Path(__file__).resolve().parent / "config.json"


def ensure_bgr_matches_roi(bgr: np.ndarray, roi_w: int, roi_h: int) -> np.ndarray:
    """
    将图像缩放到与 ``config.roi`` 的宽高一致。

    说明:
        离线调试图、模板标注图可能与运行时 ``mss`` 截图像素尺寸不同；
        统一缩放后可保证识别算法在相同逻辑分辨率下工作。

    参数:
        bgr:    输入 BGR 图像。
        roi_w:  目标宽度（像素）。
        roi_h:  目标高度（像素）。

    返回:
        缩放后的 BGR 图像；空图原样返回。
    """
    if bgr.size == 0:
        return bgr
    ih, iw = bgr.shape[:2]
    if iw == roi_w and ih == roi_h:
        return bgr
    return cv2.resize(bgr, (roi_w, roi_h), interpolation=cv2.INTER_LINEAR)


def load_fish_template_bundle(cfg: dict) -> tuple[np.ndarray | None, dict[str, Any]]:
    """
    加载鱼图标模板及其匹配参数。

    参数:
        cfg: 完整配置字典，读取 ``template_match`` 节。

    返回:
        (template_bgr, match_cfg)
        - template_bgr: 模板图像；路径无效时为 ``None``。
        - match_cfg:    匹配阈值、多尺度、引导跟踪、颜色复核等参数字典。
                        额外写入 ``anchor_x`` / ``anchor_y``（模板内鱼体几何中心）。
    """
    tm = cfg.get("template_match") or {}
    tcfg: dict[str, Any] = {
        "method": str(tm.get("method", "ccoeff_normed")),
        "min_score": float(tm.get("min_score", 0.38)),
        "multi_scale": bool(tm.get("multi_scale", True)),
        "scale_min": float(tm.get("scale_min", 0.72)),
        "scale_max": float(tm.get("scale_max", 1.28)),
        "scale_steps": int(tm.get("scale_steps", 7)),
        # 模板匹配抗误检/抗抖：允许用上一帧 y 引导，并对候选做颜色复核（针对青蓝鱼图案）
        "guided": bool(tm.get("guided", True)),
        "guided_band_px": int(tm.get("guided_band_px", 0)),
        "guided_penalty_per_px": float(tm.get("guided_penalty_per_px", 0.0016)),
        "topk": int(tm.get("topk", 6)),
        # 顶部/底部水面与边框容易与鱼产生假匹配：默认忽略更大范围
        "ignore_top_px": int(tm.get("ignore_top_px", 40)),
        "ignore_bottom_px": int(tm.get("ignore_bottom_px", 16)),
        # 复核默认更温和；并且会在“全部不通过”时回退到最高分，避免直接无鱼
        "validate_cyan_min_ratio": float(tm.get("validate_cyan_min_ratio", 0.04)),
        "validate_green_min_ratio": float(tm.get("validate_green_min_ratio", 0.0)),
        "validate_green_max_ratio": float(tm.get("validate_green_max_ratio", 0.92)),
        "validate_edge_min_ratio": float(tm.get("validate_edge_min_ratio", 0.02)),
        "fallback_to_raw_best": bool(tm.get("fallback_to_raw_best", True)),
    }
    path = tm.get("fish_image")
    if not path:
        return None, tcfg
    p = (Path(__file__).resolve().parent / str(path)).resolve()
    if not p.is_file():
        return None, tcfg
    img = imread_unicode(p)
    # 模板锚点：用模板内“鱼主体像素”的上下边界中点作为 y（中心）输出。
    # 这样比用质心更稳：即使鱼颜色/高光分布不均，也不会把锚点拉向底部。
    if img is not None and img.size > 0:
        try:
            hsv_t = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
            main_body = cv2.inRange(
                hsv_t,
                np.array([48, 22, 35], np.uint8),   # 绿到青蓝
                np.array([132, 255, 255], np.uint8),
            )
            # 排除透明变黑背景：要求一定饱和或亮度
            sat = cv2.inRange(hsv_t[:, :, 1], 28, 255)
            val = cv2.inRange(hsv_t[:, :, 2], 55, 255)
            main_body = cv2.bitwise_and(main_body, cv2.bitwise_or(sat, val))
            main_body = cv2.morphologyEx(main_body, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
            th0, tw0 = img.shape[:2]
            ys = np.where(np.any(main_body > 0, axis=1))[0]
            xs = np.where(np.any(main_body > 0, axis=0))[0]
            if ys.size > 0:
                tcfg["anchor_y"] = float(ys[0] + ys[-1]) / 2.0
            else:
                tcfg["anchor_y"] = float(th0) / 2.0
            if xs.size > 0:
                tcfg["anchor_x"] = float(xs[0] + xs[-1]) / 2.0
            else:
                tcfg["anchor_x"] = float(tw0) / 2.0
        except Exception:
            th0, tw0 = img.shape[:2]
            tcfg["anchor_y"] = float(th0) / 2.0
            tcfg["anchor_x"] = float(tw0) / 2.0
    # 输出 y 的额外偏移（像素，按缩放同比例缩放）。
    # 默认不做额外偏移；若你想手调，可在 config.json 的 template_match 里加 y_offset_px。
    if img is not None and img.size > 0:
        th0 = img.shape[0]
        tcfg["y_offset_px"] = float(tm.get("y_offset_px", 0.0))
    return img, tcfg


# =============================================================================
# 视觉识别默认参数（可被 config.json 的 vision 节覆盖）
# =============================================================================

DEFAULT_VISION: dict[str, float] = {
    # --- 绿条形状约束：竖长、较窄、实心；草地多为矮胖/破碎/横向连片 ---
    "green_min_aspect_ratio": 2.6,
    "green_max_width_ratio": 0.58,
    "green_min_height_ratio": 0.12,
    "green_min_extent": 0.42,
    "green_min_solidity": 0.72,
    "green_min_area_abs": 900.0,
    "green_min_area_ratio": 0.008,
    # 绿条长度（像素）。星露谷钓鱼小游戏中绿条高度通常在一局/一段时间内固定；
    # 设为 0 表示不强制固定，仅使用形状/面积规则。
    "green_fixed_height_px": 0.0,
    # 固定高度启用时：允许候选高度偏离的比例（过严会漏检，过松会被草拖长）
    "green_height_tolerance_ratio": 0.28,
    # 鱼标：小图标；排除细长条与大块（略放宽，避免漏检浅色鱼）
    "fish_min_area": 18.0,
    "fish_max_area_ratio": 0.07,
    "fish_max_width_ratio": 0.5,
    "fish_max_height_ratio": 0.28,
    "fish_max_aspect_ratio": 4.0,
    # 1=从鱼候选里去掉青蓝水背景像素
    "fish_subtract_blue": 1.0,
    # fish_detect_mode=green_mix 时：宽绿域相对 H 扩展、纯条膨胀、宽松 S/V 下限
    "fish_loose_h_pad": 22.0,
    "fish_loose_s_floor": 8.0,
    "fish_loose_v_floor": 15.0,
    "fish_pure_dilate": 7.0,
    # texture / hybrid：拉普拉斯与梯度分位阈值、绿条核心区内更高纹理门槛
    "texture_sal_percentile": 70.0,
    "texture_sal_min": 8.0,
    "texture_in_bar_factor": 1.38,
    "texture_in_bar_min": 20.0,
    "texture_bar_core_erode": 5.0,
    "gradient_sal_percentile": 76.0,
    "gradient_sal_min": 12.0,
    "texture_bilateral_d": 5.0,
    "texture_morph_close": 3.0,
    "texture_morph_open": 2.0,
    "fish_reject_vertical_sliver_h": 0.26,
    "fish_score_use_compactness": 1.0,
    # 底部海草/气泡噪点：常在 ROI 底部，偏绿且面积小，容易被宽 fish_hsv 误吃
    "fish_bottom_reject_y_ratio": 0.16,  # 距底部这段高度内启用过滤
    "fish_bottom_reject_max_area_ratio": 0.012,  # 小于该面积比例才认为可能是海草噪声
    "fish_bottom_reject_green_ratio": 0.42,  # 包围盒内偏绿像素占比超过则剔除
    "fish_bottom_reject_green_h_pad": 26.0,  # 以 green_low.h 为中心的宽绿相范围（非纯条绿）
    # 鱼 y 抖动抑制（对模板/颜色两种模式都适用）
    "fish_y_smooth_alpha": 0.45,
    "fish_y_jump_max_px": 42.0,
    # 鱼识别点输出偏移：正数表示“向上移动”这么多像素（你反馈目前偏到底部）
    "fish_y_output_up_offset_px": 20.0,
}


def vision_params(cfg: dict | None) -> dict[str, float]:
    """
    合并默认视觉参数与 ``config.json`` 中的 ``vision`` 覆盖项。

    返回:
        键为参数名、值为 ``float`` 的字典副本。
    """
    d = dict(DEFAULT_VISION)
    if cfg and isinstance(cfg.get("vision"), dict):
        for k, v in cfg["vision"].items():
            if isinstance(v, (int, float)):
                d[k] = float(v)
    return d


# =============================================================================
# 闭环控制器：PD 与 ARX-K 两种实现
# =============================================================================


@dataclass
class ControlState:
    """
    经典 PD 控制器（带鱼速前馈与继电器迟滞）。

    控制律概要:
        e      = fish_y - bar_center_y          # 位置误差（鱼在条上方为正）
        e_dot  = v_fish - v_bar                 # 误差变化率（近似相对速度）
        u      = Kp·e + Kd·e_dot + Kff·v_fish   # 连续控制量
        输出   = u 经死区、施密特迟滞、最短切换时间后量化为按住/松开
    """

    kp: float
    kd: float
    deadband_px: float
    smooth_alpha: float = 0.35
    vel_smooth_alpha: float = 0.45
    kff_fish_vel: float = 0.08
    hysteresis_u: float = 0.02
    min_switch_ms: float = 25.0
    large_error_px: float = 0.0
    large_error_boost: float = 1.0
    prev_time: float = field(default=0.0, repr=False)
    prev_fish_raw: float | None = field(default=None, repr=False)
    prev_bar_raw: float | None = field(default=None, repr=False)
    fish_filt: float | None = field(default=None, repr=False)
    bar_filt: float | None = field(default=None, repr=False)
    v_f_smooth: float = field(default=0.0, repr=False)
    v_b_smooth: float = field(default=0.0, repr=False)
    last_switch_time: float = field(default=0.0, repr=False)

    def decide(
        self,
        fish_y: float | None,
        bar_center_y: float | None,
        holding: bool,
    ) -> tuple[bool, str, dict[str, Any]]:
        meta: dict[str, Any] = {
            "error": None,
            "u": None,
            "e_dot": None,
            "v_fish": None,
            "v_bar": None,
            "d_error_dt": None,
        }
        if fish_y is None:
            self.prev_fish_raw = None
            self.fish_filt = None
            return False, "未识别到鱼（检查 fish_hsv 与 ROI）", meta
        if bar_center_y is None:
            self.prev_bar_raw = None
            self.bar_filt = None
            return False, "未识别到绿条（检查 green_hsv）", meta

        fy = float(fish_y)
        bc = float(bar_center_y)
        now = time.perf_counter()
        dt = max(now - self.prev_time, 1e-3)
        self.prev_time = now

        sa = self.smooth_alpha
        if self.fish_filt is None:
            self.fish_filt = fy
        else:
            self.fish_filt = sa * fy + (1.0 - sa) * self.fish_filt
        if self.bar_filt is None:
            self.bar_filt = bc
        else:
            self.bar_filt = sa * bc + (1.0 - sa) * self.bar_filt

        if self.prev_fish_raw is not None and self.prev_bar_raw is not None:
            v_fr = (fy - self.prev_fish_raw) / dt
            v_br = (bc - self.prev_bar_raw) / dt
            va = self.vel_smooth_alpha
            self.v_f_smooth = va * v_fr + (1.0 - va) * self.v_f_smooth
            self.v_b_smooth = va * v_br + (1.0 - va) * self.v_b_smooth

        self.prev_fish_raw = fy
        self.prev_bar_raw = bc

        e = float(self.fish_filt - self.bar_filt)
        e_dot = float(self.v_f_smooth - self.v_b_smooth)
        meta["error"] = e
        meta["e_dot"] = e_dot
        meta["d_error_dt"] = e_dot
        meta["v_fish"] = float(self.v_f_smooth)
        meta["v_bar"] = float(self.v_b_smooth)

        e_p = e if abs(e) >= self.deadband_px else 0.0
        u = self.kp * e_p + self.kd * e_dot + self.kff_fish_vel * self.v_f_smooth
        if self.large_error_px > 0.0 and abs(e) > self.large_error_px:
            u *= self.large_error_boost
        meta["u"] = u

        hu = self.hysteresis_u
        if hu <= 0.0:
            want_hold = u < 0.0
        elif u < -hu:
            want_hold = True
        elif u > hu:
            want_hold = False
        else:
            want_hold = holding

        min_ms = self.min_switch_ms
        want_out = want_hold
        if min_ms > 0.0 and want_out != holding:
            if (now - self.last_switch_time) * 1000.0 < min_ms:
                want_out = holding
            else:
                self.last_switch_time = now
        elif want_out != holding:
            self.last_switch_time = now

        if want_out:
            reason = (
                f"按住：err={e:.1f}px e'={e_dot:.1f}/s u={u:.3f}（鱼在条上方或需克服惯性）"
            )
        else:
            reason = f"松开：err={e:.1f}px e'={e_dot:.1f}/s u={u:.3f}"
        return want_out, reason, meta

    def reset_integral(self) -> None:
        self.prev_time = 0.0
        self.prev_fish_raw = None
        self.prev_bar_raw = None
        self.fish_filt = None
        self.bar_filt = None
        self.v_f_smooth = 0.0
        self.v_b_smooth = 0.0
        self.last_switch_time = 0.0


@dataclass
class ControlStateArxK:
    r"""
    基于离线 ARX 辨识 + Ackermann 增益 ``K`` 的跟踪控制（继电器实现）。

    **推导概要**

    1) **辨识模型**（仅绿条，\(u_k\in\{0,1\}\) 为按住）：

       .. math:: y_{k+1}=a_1 y_k+a_2 y_{k-1}+b_1 u_k+b_2 u_{k-1}+c

       增广状态 \(x_k=[y_k,\,y_{k-1},\,u_{k-1}]^\top\)，得 \(x_{k+1}=A x_k+B u_k+d\)。

    2) **极点配置**：取期望 \(\lambda_i\in\mathbb{D}\)，Ackermann 得 \(K\) 使 \(A-BK\) 的特征多项式为
       \(\prod_i(z-\lambda_i)\)，名义连续控制 \(u_k^{\mathrm{nom}}=-K x_k\)（实机再量化为 0/1）。

    3) **跟踪鱼 \(r_k\)**：令跟踪误差 \(e_k=r_k-y_k\)（与脚本中 ``fish_y - bar_center`` 一致）。
       在鱼位置**缓变**时，用误差向量 \(\xi_k=[e_k,\,e_{k-1},\,u_{k-1}]^\top\) 代替 \(x_k\) 代入同一
       \(K\)，即 **\(v_k=-K\xi_k\)**，作为对「调节 \(y\to r\)」的 **输出反馈近似**（严格模型需含
       \(r\) 的驱动项或前馈；此处与工程上常用近似一致）。

    4) **继电器**：\(v_k\) 经死区、施密特迟滞与最短切换时间后映射为按住/松开，与 ``ControlState`` 相同。

    **继电器与第三维**：名义连续律 \(u=-Kx\) 中的 \(x_3=u_{k-1}\) 在 **0/1 继电器** 下会引入
    约 \(|K[2]|\) 量级的偏置；辨识中 \(|K[2]|\) 常接近 1，导致「刚按住」后 \(v\) 立刻变号、几乎无法
    维持抬条。故对 ``u_{k-1}`` 乘 **arx_u_prev_weight**（默认 0）或显著小于 1，仅保留 \(e,e_{k-1}\)
    两维主导；再用 **arx_v_scale** 把 \(v\) 拉到与 ``hysteresis_u`` 匹配的量级。

    若实机极性反了，将 ``arx_k`` 三根系数整体变号即可。
    """

    K: np.ndarray  # shape (3,)
    smooth_alpha: float
    deadband_px: float
    hysteresis_u: float
    min_switch_ms: float
    arx_u_prev_weight: float = 0.0
    arx_v_scale: float = 12.0
    large_error_px: float = 0.0
    large_error_boost: float = 1.0
    prev_time: float = field(default=0.0, repr=False)
    fish_filt: float | None = field(default=None, repr=False)
    bar_filt: float | None = field(default=None, repr=False)
    e_prev: float = field(default=0.0, repr=False)
    u_prev: float = field(default=0.0, repr=False)
    last_switch_time: float = field(default=0.0, repr=False)

    def decide(
        self,
        fish_y: float | None,
        bar_center_y: float | None,
        holding: bool,
    ) -> tuple[bool, str, dict[str, Any]]:
        meta: dict[str, Any] = {
            "error": None,
            "u": None,
            "e_dot": None,
            "v_fish": None,
            "v_bar": None,
            "d_error_dt": None,
            "v_arx": None,
            "e_prev": None,
            "u_prev": None,
        }
        if fish_y is None:
            self.fish_filt = None
            return False, "未识别到鱼（检查 fish_hsv 与 ROI）", meta
        if bar_center_y is None:
            self.bar_filt = None
            return False, "未识别到绿条（检查 green_hsv）", meta

        fy = float(fish_y)
        bc = float(bar_center_y)
        now = time.perf_counter()
        self.prev_time = now

        sa = self.smooth_alpha
        if self.fish_filt is None:
            self.fish_filt = fy
        else:
            self.fish_filt = sa * fy + (1.0 - sa) * self.fish_filt
        if self.bar_filt is None:
            self.bar_filt = bc
        else:
            self.bar_filt = sa * bc + (1.0 - sa) * self.bar_filt

        e = float(self.fish_filt - self.bar_filt)
        e_p = e if abs(e) >= self.deadband_px else 0.0
        u_w = self.u_prev * self.arx_u_prev_weight
        xi = np.array([e_p, self.e_prev, u_w], dtype=np.float64)
        v = float(-np.dot(self.K, xi)) * self.arx_v_scale
        if self.large_error_px > 0.0 and abs(e) > self.large_error_px:
            v *= self.large_error_boost
        meta["error"] = e
        meta["e_prev"] = float(self.e_prev)
        meta["u_prev"] = float(self.u_prev)
        meta["u"] = v
        meta["v_arx"] = v

        hu = self.hysteresis_u
        if hu <= 0.0:
            want_hold = v < 0.0
        elif v < -hu:
            want_hold = True
        elif v > hu:
            want_hold = False
        else:
            want_hold = holding

        min_ms = self.min_switch_ms
        want_out = want_hold
        if min_ms > 0.0 and want_out != holding:
            if (now - self.last_switch_time) * 1000.0 < min_ms:
                want_out = holding
            else:
                self.last_switch_time = now
        elif want_out != holding:
            self.last_switch_time = now

        ep_snap = float(self.e_prev)
        uw_snap = float(self.u_prev * self.arx_u_prev_weight)
        self.e_prev = e
        self.u_prev = 1.0 if want_out else 0.0

        if want_out:
            reason = (
                f"ARX-K 按住：e={e:.1f} v={v:.3f} "
                f"ξ≈[{e_p:.1f},{ep_snap:.1f},{uw_snap:.3f}]×scale={self.arx_v_scale:g}"
            )
        else:
            reason = f"ARX-K 松开：e={e:.1f} v={v:.3f}"
        return want_out, reason, meta

    def reset_integral(self) -> None:
        self.prev_time = 0.0
        self.fish_filt = None
        self.bar_filt = None
        self.e_prev = 0.0
        self.u_prev = 0.0
        self.last_switch_time = 0.0


def control_state_from_cfg(ctrl_cfg: dict[str, Any]) -> ControlState:
    return ControlState(
        kp=float(ctrl_cfg["kp"]),
        kd=float(ctrl_cfg.get("kd", 0.15)),
        deadband_px=float(ctrl_cfg["deadband_px"]),
        smooth_alpha=float(ctrl_cfg.get("smooth_alpha", 0.35)),
        vel_smooth_alpha=float(ctrl_cfg.get("vel_smooth_alpha", 0.45)),
        kff_fish_vel=float(ctrl_cfg.get("kff_fish_vel", 0.08)),
        hysteresis_u=float(ctrl_cfg.get("hysteresis_u", 0.02)),
        min_switch_ms=float(ctrl_cfg.get("min_switch_ms", 25.0)),
        large_error_px=float(ctrl_cfg.get("large_error_px", 0.0)),
        large_error_boost=float(ctrl_cfg.get("large_error_boost", 1.35)),
    )


def control_state_arx_from_cfg(ctrl_cfg: dict[str, Any]) -> ControlStateArxK:
    k = ctrl_cfg.get("arx_k")
    if not isinstance(k, list) or len(k) != 3:
        raise ValueError("control.mode=arx_k 时需在 control.arx_k 中提供长度 3 的 [k0,k1,k2]")
    arr = np.array([float(k[0]), float(k[1]), float(k[2])], dtype=np.float64)
    db = ctrl_cfg.get("arx_deadband_px")
    dead = float(db) if db is not None else float(ctrl_cfg["deadband_px"])
    return ControlStateArxK(
        K=arr,
        smooth_alpha=float(ctrl_cfg.get("smooth_alpha", 0.35)),
        deadband_px=dead,
        hysteresis_u=float(ctrl_cfg.get("hysteresis_u", 0.02)),
        min_switch_ms=float(ctrl_cfg.get("min_switch_ms", 25.0)),
        arx_u_prev_weight=float(ctrl_cfg.get("arx_u_prev_weight", 0.0)),
        arx_v_scale=float(ctrl_cfg.get("arx_v_scale", 12.0)),
        large_error_px=float(ctrl_cfg.get("large_error_px", 0.0)),
        large_error_boost=float(ctrl_cfg.get("large_error_boost", 1.45)),
    )


def build_controller_from_cfg(ctrl_cfg: dict[str, Any]) -> ControlState | ControlStateArxK:
    mode = (ctrl_cfg.get("mode") or "pd").strip().lower()
    if mode == "arx_k":
        return control_state_arx_from_cfg(ctrl_cfg)
    return control_state_from_cfg(ctrl_cfg)


# =============================================================================
# 配置加载与热键解析
# =============================================================================


def load_config() -> dict:
    """从 ``CONFIG_PATH`` 读取 JSON 配置；文件不存在时打印提示并退出进程。"""
    if not CONFIG_PATH.is_file():
        print(f"未找到配置文件: {CONFIG_PATH}")
        sys.exit(1)
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


def parse_key(name: str) -> keyboard.Key | keyboard.KeyCode:
    """
    将配置中的热键名字符串解析为 ``pynput`` 键对象。

    支持:
        - 功能键: ``f1`` ~ ``f12``
        - 单字符: ``c``, ``a`` 等
    """
    n = name.lower().strip()
    if n.startswith("f") and n[1:].isdigit():
        return getattr(keyboard.Key, n)
    return keyboard.KeyCode.from_char(n)


# =============================================================================
# 绿条检测
# =============================================================================


def green_bar_binary_mask(hsv: np.ndarray, low: np.ndarray, high: np.ndarray) -> np.ndarray:
    """
    生成绿色有效区域的二值掩膜。

    形态学策略:
        - 开运算去噪
        - 竖向细长闭运算（19×3）连接绿条，同时避免横向吞并草地
    """
    mask = cv2.inRange(hsv, low, high)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
    # 竖向 kernel：增高、压窄，利于连成钓鱼竖条，少横向吞草地
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((19, 3), np.uint8))
    return mask


def detect_green_bar_rect(
    hsv: np.ndarray,
    low: np.ndarray,
    high: np.ndarray,
    *,
    vision: dict[str, float] | None = None,
) -> tuple[int, int, int, int] | None:
    """返回最佳绿条外接矩形 (x, y, w, h)；无则 None。"""
    v = vision or DEFAULT_VISION
    roi_h, roi_w = hsv.shape[:2]
    mask = green_bar_binary_mask(hsv, low, high)
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None

    min_area = max(v["green_min_area_abs"], roi_w * roi_h * v["green_min_area_ratio"])
    min_h = max(24.0, roi_h * v["green_min_height_ratio"])

    scored: list[tuple[float, tuple[int, int, int, int]]] = []
    for c in contours:
        area = cv2.contourArea(c)
        if area < min_area:
            continue
        x, y, bw, bh = cv2.boundingRect(c)
        if bh < min_h:
            continue
        aspect = bh / max(bw, 1)
        if aspect < v["green_min_aspect_ratio"]:
            continue
        if bw > roi_w * v["green_max_width_ratio"]:
            continue
        rect_a = max(bw * bh, 1)
        extent = area / rect_a
        if extent < v["green_min_extent"]:
            continue
        hull = cv2.convexHull(c)
        hull_a = cv2.contourArea(hull)
        solidity = area / max(hull_a, 1e-6)
        if solidity < v["green_min_solidity"]:
            continue
        # 若已知绿条固定高度：太短的候选直接跳过（太长的可能被水草拖长，后续会再精修）
        fixed_h = int(float(v.get("green_fixed_height_px", 0.0)) or 0.0)
        if fixed_h > 0:
            tol = float(v.get("green_height_tolerance_ratio", 0.28))
            tol = min(max(tol, 0.05), 0.6)
            if bh < int(fixed_h * (1.0 - tol)):
                continue
        score = area + aspect * 200.0
        scored.append((score, (x, y, bw, bh)))

    if not scored:
        return None
    scored.sort(key=lambda t: t[0], reverse=True)
    x, y, bw, bh = scored[0][1]

    # 固定长度精修：避免底部水草把绿色块的下沿拖下去；并在遮挡导致绿段变短时用固定长度 offset 补齐
    fixed_h = int(float(v.get("green_fixed_height_px", 0.0)) or 0.0)
    if fixed_h > 0 and bh >= fixed_h and bw > 2:
        try:
            mask = cv2.inRange(hsv, low, high)
            patch = mask[y : y + bh, x : x + bw]
            if patch.size > 0 and patch.shape[0] >= fixed_h:
                # 每行绿像素数
                row_sum = patch.astype(np.uint8).sum(axis=1) / 255.0  # shape: (bh,)
                # 先找“明显为绿”的行范围（应对遮挡：中间被盖住会断开，但上/下限仍可用）
                thr = max(2.0, float(bw) * 0.22)
                idx = np.where(row_sum >= thr)[0]
                if idx.size >= 2:
                    top_i = int(idx[0])
                    bot_i = int(idx[-1])
                    span = bot_i - top_i + 1
                else:
                    top_i = 0
                    bot_i = patch.shape[0] - 1
                    span = bot_i - top_i + 1

                tol = float(v.get("green_height_tolerance_ratio", 0.28))
                tol = min(max(tol, 0.05), 0.6)
                # 若观测到的绿段跨度明显小于固定高度，则认为被遮挡：用上/下限 + offset 补齐固定长度
                if span < int(fixed_h * (1.0 - tol)):
                    # 评估上/下边界“可靠性”：边界附近行的绿密度更高者更可能是真边界
                    def _band_mean(a: np.ndarray, i0: int, i1: int) -> float:
                        i0 = max(0, min(int(i0), a.shape[0] - 1))
                        i1 = max(0, min(int(i1), a.shape[0] - 1))
                        if i1 < i0:
                            i0, i1 = i1, i0
                        seg = a[i0 : i1 + 1]
                        return float(seg.mean()) if seg.size > 0 else 0.0

                    top_m = _band_mean(row_sum, top_i, top_i + 3)
                    bot_m = _band_mean(row_sum, bot_i - 3, bot_i)
                    if top_m >= bot_m:
                        # 以“上限”为锚：下限用固定长度补齐
                        y = y + top_i
                        bh = fixed_h
                    else:
                        # 以“下限”为锚：上限用固定长度补齐
                        y = y + bot_i - fixed_h + 1
                        bh = fixed_h
                else:
                    # 无明显遮挡：滑窗找 fixed_h 高度内绿像素总量最大的一段
                    k = fixed_h
                    csum = np.cumsum(row_sum, dtype=np.float64)
                    # window_sum[i] = sum(row_sum[i:i+k])
                    window_sum = csum[k - 1 :] - np.concatenate(([0.0], csum[:-k]))
                    best_i = int(np.argmax(window_sum))
                    y = y + best_i
                    bh = fixed_h

                # 边界裁剪到 ROI 内
                y = max(0, min(int(y), roi_h - 1))
                bh = max(1, min(int(bh), roi_h - y))
        except Exception:
            # 精修失败则退回原 bbox
            pass
    return x, y, bw, bh


def detect_green_bar(
    hsv: np.ndarray,
    low: np.ndarray,
    high: np.ndarray,
    *,
    vision: dict[str, float] | None = None,
) -> tuple[int, int] | None:
    r = detect_green_bar_rect(hsv, low, high, vision=vision)
    if r is None:
        return None
    _x, y, _w, h = r
    return y, y + h


def fish_warm_color_mask(
    hsv: np.ndarray,
    fish_low: np.ndarray,
    fish_high: np.ndarray,
    green_low: np.ndarray,
) -> np.ndarray:
    """
    鱼标常见为褐/橙/黄/少量偏红；很多鱼身含浅灰白（低 S），仅 inRange 用户 fish_hsv 易漏检。
    扩展暖色时 H 上界严格不超过 green_hsv 的 H 下限之前，避免吃进黄绿草地。
    """
    m = cv2.inRange(hsv, fish_low, fish_high)
    gh = int(green_low[0])
    upper_h = max(0, min(gh - 1, 179))
    # 略向黄色延伸，但绝不越过 upper_h
    h_hi = min(upper_h, int(fish_high[0]) + 10)
    h_hi = max(h_hi, int(fish_high[0]))
    lo_r = np.array(
        [
            int(fish_low[0]),
            max(12, int(fish_low[1]) - 55),
            max(18, int(fish_low[2]) - 65),
        ],
        dtype=np.uint8,
    )
    hi_r = np.array([h_hi, 255, 255], dtype=np.uint8)
    if lo_r[0] <= hi_r[0]:
        m = cv2.bitwise_or(m, cv2.inRange(hsv, lo_r, hi_r))
    # 红色在 OpenCV HSV 两端卷绕（小鱼标上偶见）
    s0 = max(10, int(fish_low[1]) - 60)
    v0 = max(15, int(fish_low[2]) - 70)
    r_hi = min(14, upper_h)
    if r_hi >= lo_r[0]:
        m = cv2.bitwise_or(
            m,
            cv2.inRange(hsv, np.array([0, s0, v0], np.uint8), np.array([r_hi, 255, 255], np.uint8)),
        )
    m = cv2.bitwise_or(
        m,
        cv2.inRange(
            hsv,
            np.array([168, s0, v0], np.uint8),
            np.array([179, 255, 255], np.uint8),
        ),
    )
    return m


def fish_mask_green_mix(
    hsv: np.ndarray,
    fish_low: np.ndarray,
    fish_high: np.ndarray,
    green_low: np.ndarray,
    green_high: np.ndarray,
    vision: dict[str, float],
) -> np.ndarray:
    """
    鱼身为灰绿、黄绿、低饱和绿等「非纯条绿」时：宽绿系 − 高饱和纯绿条（膨胀），
    再并上用户 fish_hsv（用于微调鱼色相），均不与纯条域相交。
    """
    v = vision
    gh, gs, gv = int(green_low[0]), int(green_low[1]), int(green_low[2])
    gh2 = int(green_high[0])
    h_pad = int(v.get("fish_loose_h_pad", 22.0))
    s0 = int(v.get("fish_loose_s_floor", 8.0))
    v0 = int(v.get("fish_loose_v_floor", 15.0))
    lo = np.array([max(0, gh - h_pad), max(0, s0), max(0, v0)], dtype=np.uint8)
    hi = np.array([min(179, gh2 + 18), 255, 255], dtype=np.uint8)
    loose = cv2.inRange(hsv, lo, hi)

    # 纯绿条：与配置 green_hsv 一致，竖向闭运算后膨胀，盖住整条
    pure = cv2.inRange(hsv, green_low, green_high)
    pure = cv2.morphologyEx(pure, cv2.MORPH_CLOSE, np.ones((17, 3), np.uint8))
    kd = int(v.get("fish_pure_dilate", 7.0))
    kd = max(3, kd) | 1
    pure_d = cv2.dilate(pure, np.ones((kd, kd), np.uint8))

    fish_m = cv2.bitwise_and(loose, cv2.bitwise_not(pure_d))
    user_fish = cv2.inRange(hsv, fish_low, fish_high)
    fish_m = cv2.bitwise_or(fish_m, cv2.bitwise_and(user_fish, cv2.bitwise_not(pure_d)))

    if v.get("fish_subtract_blue", 1.0) >= 0.5:
        blue = cv2.inRange(hsv, np.array([88, 38, 38], np.uint8), np.array([118, 255, 255], np.uint8))
        fish_m = cv2.bitwise_and(fish_m, cv2.bitwise_not(blue))

    fish_m = cv2.morphologyEx(fish_m, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    fish_m = cv2.morphologyEx(fish_m, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
    return fish_m


def fish_mask_texture(
    bgr: np.ndarray,
    hsv: np.ndarray,
    green_low: np.ndarray,
    green_high: np.ndarray,
    vision: dict[str, float],
) -> np.ndarray:
    """
    不依赖单色：混合图案鱼 = 边缘/细节多；纯绿条内部相对平滑。
    拉普拉斯(高频) + 梯度幅值，分位阈值自适应；在腐蚀后的纯绿条核心内要求更高纹理，压掉条内噪点、保留叠在条上的鱼。
    """
    v = vision
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    bd = int(v.get("texture_bilateral_d", 5.0))
    bd = max(3, bd) | 1
    gray = cv2.bilateralFilter(gray, bd, 60, 60)
    blur = cv2.GaussianBlur(gray, (0, 0), sigmaX=1.1)
    lap = cv2.Laplacian(blur, cv2.CV_64F, ksize=3)
    sal_u8 = np.clip(np.abs(lap), 0, 255).astype(np.uint8)
    p = float(v.get("texture_sal_percentile", 70.0))
    thr = max(float(v.get("texture_sal_min", 8.0)), float(np.percentile(sal_u8, p)))
    _, m_lap = cv2.threshold(sal_u8, int(thr), 255, cv2.THRESH_BINARY)

    gx = cv2.Sobel(gray, cv2.CV_64F, 1, 0, ksize=3)
    gy = cv2.Sobel(gray, cv2.CV_64F, 0, 1, ksize=3)
    gm_u8 = np.clip(np.sqrt(gx * gx + gy * gy), 0, 255).astype(np.uint8)
    p2 = float(v.get("gradient_sal_percentile", 76.0))
    thr2 = max(float(v.get("gradient_sal_min", 12.0)), float(np.percentile(gm_u8, p2)))
    _, m_g = cv2.threshold(gm_u8, int(thr2), 255, cv2.THRESH_BINARY)

    m = cv2.bitwise_or(m_lap, m_g)

    pure = cv2.inRange(hsv, green_low, green_high)
    pure = cv2.morphologyEx(pure, cv2.MORPH_CLOSE, np.ones((17, 3), np.uint8))
    ker = int(v.get("texture_bar_core_erode", 5.0))
    ker = max(1, ker) | 1
    core = cv2.erode(pure, np.ones((ker, ker), np.uint8))
    fac = float(v.get("texture_in_bar_factor", 1.38))
    strict_tex = max(float(v.get("texture_in_bar_min", 20.0)), thr * fac)
    _, m_inside = cv2.threshold(sal_u8, int(strict_tex), 255, cv2.THRESH_BINARY)
    # 绿条核心区：去掉不够「花」的像素；鱼叠在条上仍保留
    strip_noise = cv2.bitwise_and(core, cv2.bitwise_not(m_inside))
    m = cv2.bitwise_and(m, cv2.bitwise_not(strip_noise))

    kc = int(v.get("texture_morph_close", 3.0))
    kc = max(1, kc) | 1
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((kc, kc), np.uint8))
    ko = int(v.get("texture_morph_open", 2.0))
    ko = max(1, ko)
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((ko, ko), np.uint8))

    if v.get("fish_subtract_blue", 1.0) >= 0.5:
        blue = cv2.inRange(hsv, np.array([88, 38, 38], np.uint8), np.array([118, 255, 255], np.uint8))
        m = cv2.bitwise_and(m, cv2.bitwise_not(blue))
    return m


def fish_mask_raw(
    hsv: np.ndarray,
    fish_low: np.ndarray,
    fish_high: np.ndarray,
    green_low: np.ndarray,
    green_high: np.ndarray,
    vision: dict[str, float] | None = None,
    *,
    fish_mode: str = "warm",
    bgr: np.ndarray | None = None,
) -> np.ndarray:
    v = vision or DEFAULT_VISION
    mode = (fish_mode or "warm").strip().lower()
    if mode == "green_mix":
        return fish_mask_green_mix(hsv, fish_low, fish_high, green_low, green_high, v)
    if mode == "texture":
        if bgr is None:
            return np.zeros(hsv.shape[:2], dtype=np.uint8)
        return fish_mask_texture(bgr, hsv, green_low, green_high, v)
    if mode == "hybrid":
        m_c = fish_mask_green_mix(hsv, fish_low, fish_high, green_low, green_high, v)
        if bgr is None:
            return m_c
        m_t = fish_mask_texture(bgr, hsv, green_low, green_high, v)
        return cv2.bitwise_or(m_c, m_t)

    fish_m = fish_warm_color_mask(hsv, fish_low, fish_high, green_low)
    # 去掉绿条/草地：原始绿域 + 竖条形态绿，略膨胀即可（比 7x7 温和，少吃掉贴条鱼身）
    g_raw = cv2.inRange(hsv, green_low, green_high)
    g_bar = green_bar_binary_mask(hsv, green_low, green_high)
    green_kill = cv2.bitwise_or(
        cv2.dilate(g_raw, np.ones((5, 5), np.uint8)),
        cv2.dilate(g_bar, np.ones((4, 4), np.uint8)),
    )
    fish_m = cv2.bitwise_and(fish_m, cv2.bitwise_not(green_kill))
    if v.get("fish_subtract_blue", 1.0) >= 0.5:
        # 小游戏槽内青蓝水纹易与浅鱼混淆，去掉高饱和青蓝
        blue = cv2.inRange(hsv, np.array([88, 38, 38], np.uint8), np.array([118, 255, 255], np.uint8))
        fish_m = cv2.bitwise_and(fish_m, cv2.bitwise_not(blue))
    fish_m = cv2.morphologyEx(fish_m, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    fish_m = cv2.morphologyEx(fish_m, cv2.MORPH_OPEN, np.ones((2, 2), np.uint8))
    return fish_m


# =============================================================================
# 鱼图标检测：HSV 分割 / 纹理 / 模板匹配
# =============================================================================

TEMPLATE_MATCH_METHODS: dict[str, tuple[int, str]] = {
    "ccoeff_normed": (cv2.TM_CCOEFF_NORMED, "max"),
    "ccorr_normed": (cv2.TM_CCORR_NORMED, "max"),
    "sqdiff_normed": (cv2.TM_SQDIFF_NORMED, "min"),
}


def match_fish_template_full(
    bgr: np.ndarray,
    template_bgr: np.ndarray,
    cfg: dict[str, Any] | None,
    *,
    preferred_y: float | None = None,
) -> tuple[float, int, int, int, int, float] | None:
    """
    返回 (中心y, left, top, tw, th, score)；score 已归一成「越大越好」便于阈值比较。
    支持 multi_scale：在 scale_min～scale_max 间对模板缩放，取最高分（缓解截图/模板分辨率不一致）。
    """
    if bgr is None or template_bgr is None or bgr.size == 0 or template_bgr.size == 0:
        return None
    c = cfg or {}
    method_name = str(c.get("method", "ccoeff_normed")).lower().strip()
    tup = TEMPLATE_MATCH_METHODS.get(method_name)
    if tup is None:
        tup = TEMPLATE_MATCH_METHODS["ccoeff_normed"]
    code, mode = tup
    min_score = float(c.get("min_score", 0.38))

    th0, tw0 = template_bgr.shape[:2]
    ih, iw = bgr.shape[:2]
    if th0 < 4 or tw0 < 4:
        return None
    anchor_y0 = float(c.get("anchor_y", th0 / 2.0))
    y_off0 = float(c.get("y_offset_px", 0.0))

    gray_i = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)

    scales = [1.0]
    if c.get("multi_scale", True):
        smin = float(c.get("scale_min", 0.72))
        smax = float(c.get("scale_max", 1.28))
        n = max(2, int(c.get("scale_steps", 7)))
        scales = np.linspace(smin, smax, n).tolist()

    best: tuple[float, int, int, int, int, float] | None = None
    best_score = -1e9

    for sc in scales:
        tw = max(4, int(round(tw0 * sc)))
        th = max(4, int(round(th0 * sc)))
        if th >= ih or tw >= iw:
            continue
        anchor_y = float(anchor_y0) * float(sc)
        y_off = float(y_off0) * float(sc)
        tpl2 = cv2.resize(
            template_bgr,
            (tw, th),
            interpolation=cv2.INTER_AREA if sc < 1.0 else cv2.INTER_LINEAR,
        )
        gray_t = cv2.cvtColor(tpl2, cv2.COLOR_BGR2GRAY)
        res = cv2.matchTemplate(gray_i, gray_t, code)

        # 将不同 matchTemplate 方法统一成“越大越好”的 score_map
        if mode == "max":
            score_map = res
        else:
            score_map = 1.0 - res

        # 使用上一帧 y 进行引导：在 score_map 上按 y 距离加惩罚，减少误匹配导致的 y 抖动
        c_guided = bool(c.get("guided", True))
        if preferred_y is not None and c_guided:
            band = int(c.get("guided_band_px", 0) or 0)
            pen = float(c.get("guided_penalty_per_px", 0.0016))
            rh = score_map.shape[0]
            # 计算每一行对应的匹配中心 y
            row_cy = (np.arange(rh, dtype=np.float32) + np.float32(anchor_y + y_off)).reshape(-1, 1)
            dist = np.abs(row_cy - float(preferred_y))
            if band > 0:
                # 超出 band 的行直接置为极小值（按行过滤，避免 boolean 形状不匹配）
                score_map = score_map.copy()
                row_mask = (dist.reshape(-1) > float(band))
                score_map[row_mask, :] = -1e9
            if pen > 0.0:
                score_map = score_map - dist * float(pen)

        # 颜色复核（针对你图里的“青蓝鱼”）：从 top-k 候选里找通过复核的候选；
        # 若全部不通过，则可回退到“原始最好”（避开顶部/底部）以避免直接无鱼。
        topk = max(1, int(c.get("topk", 6) or 6))
        ign_top = max(0, int(c.get("ignore_top_px", 10) or 0))
        ign_bot = max(0, int(c.get("ignore_bottom_px", 10) or 0))
        min_cyan = float(c.get("validate_cyan_min_ratio", 0.06))
        min_green = float(c.get("validate_green_min_ratio", 0.02))
        max_green = float(c.get("validate_green_max_ratio", 0.60))
        min_edge = float(c.get("validate_edge_min_ratio", 0.05))
        allow_fallback = bool(c.get("fallback_to_raw_best", True))

        local_best: tuple[float, int, int, int, int, float] | None = None
        local_best_score = -1e9
        raw_best: tuple[float, int, int, int, int, float] | None = None
        raw_best_score = -1e9
        sm = score_map.copy() if topk > 1 else score_map
        for _ in range(topk):
            _minv, maxv, _minl, maxl = cv2.minMaxLoc(sm)
            score = float(maxv)
            lx, ly = int(maxl[0]), int(maxl[1])
            if score <= -1e8:
                break

            cy = ly + anchor_y + y_off
            # 记录“原始最好”（尽量避开顶部/底部），用于复核全失败时回退
            if not ((ly < ign_top) or ((ly + th) > (ih - ign_bot))):
                if score > raw_best_score:
                    raw_best_score = score
                    raw_best = (cy, lx, ly, tw, th, score)

            # 忽略顶部/底部边框区域（通常是 UI/装饰，不是鱼）
            if (ly < ign_top) or ((ly + th) > (ih - ign_bot)):
                if topk > 1:
                    sm[ly, lx] = -1e9
                    continue

            # 青蓝比例复核
            patch = bgr[ly : ly + th, lx : lx + tw]
            ok = True
            if patch.size > 0:
                hsv_p = cv2.cvtColor(patch, cv2.COLOR_BGR2HSV)
                cyan = cv2.inRange(hsv_p, np.array([75, 40, 55], np.uint8), np.array([115, 255, 255], np.uint8))
                # 鱼身常带一点偏绿；纯水面多为蓝且纹理少
                greenish = cv2.inRange(hsv_p, np.array([34, 35, 35], np.uint8), np.array([96, 255, 255], np.uint8))
                cr = float(np.count_nonzero(cyan)) / float(cyan.size)
                gr = float(np.count_nonzero(greenish)) / float(greenish.size)
                # 边缘密度：鱼图案有明显描边/细节，水面/顶部 UI 往往更平滑
                gray_p = cv2.cvtColor(patch, cv2.COLOR_BGR2GRAY)
                edges = cv2.Canny(gray_p, 40, 120)
                er = float(np.count_nonzero(edges)) / float(edges.size)

                # 温和复核：主要依赖青蓝 + 边缘；绿色仅作辅助（可为 0）
                if cr < min_cyan or er < min_edge or gr < min_green or gr > max_green:
                    ok = False
            else:
                ok = False

            if ok and score > local_best_score:
                local_best_score = score
                local_best = (cy, lx, ly, tw, th, score)
                break  # 通过复核的最高分候选即可

            if topk > 1:
                sm[ly, lx] = -1e9

        picked = local_best
        picked_score = local_best_score
        if picked is None and allow_fallback and raw_best is not None:
            picked = raw_best
            picked_score = raw_best_score

        if picked is not None and picked_score > best_score:
            best_score = picked_score
            best = picked

    if best is None:
        return None
    if best_score < min_score:
        return None
    return best


def detect_fish_y_template(
    bgr: np.ndarray | None,
    template_bgr: np.ndarray | None,
    cfg: dict[str, Any] | None,
    *,
    preferred_y: float | None = None,
) -> float | None:
    r = (
        match_fish_template_full(bgr, template_bgr, cfg, preferred_y=preferred_y)
        if bgr is not None
        else None
    )
    return float(r[0]) if r else None


def fish_mask_from_template_match(
    bgr: np.ndarray,
    template_bgr: np.ndarray,
    cfg: dict[str, Any] | None,
) -> np.ndarray:
    """用于预览第三列：在最佳匹配位置画白块。"""
    h, w = bgr.shape[:2]
    out = np.zeros((h, w), dtype=np.uint8)
    r = match_fish_template_full(bgr, template_bgr, cfg)
    if r is None:
        return out
    _cy, lx, ly, tw, th, _sc = r
    cv2.rectangle(out, (lx, ly), (lx + tw - 1, ly + th - 1), 255, -1)
    return out


def detect_fish_y(
    hsv: np.ndarray,
    fish_low: np.ndarray,
    fish_high: np.ndarray,
    green_low: np.ndarray,
    green_high: np.ndarray,
    *,
    vision: dict[str, float] | None = None,
    fish_mode: str = "warm",
    bgr: np.ndarray | None = None,
    fish_template: np.ndarray | None = None,
    template_cfg: dict[str, Any] | None = None,
) -> float | None:
    v = vision or DEFAULT_VISION
    mode = (fish_mode or "warm").strip().lower()
    if mode == "template":
        return detect_fish_y_template(bgr, fish_template, template_cfg)

    fish_mask = fish_mask_raw(
        hsv,
        fish_low,
        fish_high,
        green_low,
        green_high,
        vision=v,
        fish_mode=fish_mode,
        bgr=bgr,
    )
    contours, _ = cv2.findContours(fish_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None
    roi_h, roi_w = hsv.shape[:2]
    roi_area = max(roi_w * roi_h, 1)
    area_max = roi_area * v["fish_max_area_ratio"]
    min_a = v["fish_min_area"]
    use_compact = mode in ("texture", "hybrid") and v.get("fish_score_use_compactness", 0.0) >= 0.5
    sliver_w = max(3, int(roi_w * 0.06))
    sliver_h_ratio = float(v.get("fish_reject_vertical_sliver_h", 0.26))

    candidates: list[tuple[float, np.ndarray]] = []
    for c in contours:
        area = cv2.contourArea(c)
        if area <= min_a or area >= area_max:
            continue
        x, y, bw, bh = cv2.boundingRect(c)
        if bw > roi_w * v["fish_max_width_ratio"] or bh > roi_h * v["fish_max_height_ratio"]:
            continue
        short_side = max(min(bw, bh), 1)
        long_side = max(bw, bh)
        if long_side / short_side > v["fish_max_aspect_ratio"]:
            continue
        # 绿条左右竖边：极窄而高
        if bw <= sliver_w and bh >= roi_h * sliver_h_ratio:
            continue

        # 底部海草过滤：在 ROI 底部的小块“偏绿”噪声（海草/气泡）容易被宽 fish_hsv 误识别成鱼
        # 用包围盒内偏绿像素占比做快速判别（不依赖纯条绿的 green_hsv）。
        y_ratio = float(v.get("fish_bottom_reject_y_ratio", 0.0))
        max_area_r = float(v.get("fish_bottom_reject_max_area_ratio", 0.0))
        if y_ratio > 0.0 and max_area_r > 0.0:
            bottom_start = int(roi_h * (1.0 - y_ratio))
            if (y + bh) >= bottom_start and area <= roi_area * max_area_r:
                patch = hsv[y : y + bh, x : x + bw]
                if patch.size > 0:
                    gh = int(green_low[0])
                    h_pad = int(v.get("fish_bottom_reject_green_h_pad", 26.0))
                    h0 = max(0, gh - h_pad)
                    h1 = min(179, gh + h_pad)
                    # “偏绿”判定：H 落在绿附近，且有一定 S/V（排除灰白/暗噪）
                    g_like = cv2.inRange(
                        patch,
                        np.array([h0, 20, 25], dtype=np.uint8),
                        np.array([h1, 255, 255], dtype=np.uint8),
                    )
                    gr = float(np.count_nonzero(g_like)) / float(g_like.size)
                    if gr >= float(v.get("fish_bottom_reject_green_ratio", 0.42)):
                        continue
        peri = cv2.arcLength(c, True)
        circ = (4.0 * math.pi * area) / (peri * peri + 1e-6) if peri > 1e-3 else 0.0
        if use_compact:
            score = area * (0.12 + min(circ, 1.0)) * (1.0 if long_side / short_side < 3.2 else 0.55)
        else:
            score = area
        candidates.append((score, c))

    if not candidates:
        return None
    cnt = max(candidates, key=lambda t: t[0])[1]
    m = cv2.moments(cnt)
    if m["m00"] == 0:
        return None
    return float(m["m01"] / m["m00"])


def green_mask_vis(hsv: np.ndarray, low: np.ndarray, high: np.ndarray) -> np.ndarray:
    return green_bar_binary_mask(hsv, low, high)


def build_dashboard_bgr(
    last_bgr: np.ndarray,
    hsv: np.ndarray,
    gl: np.ndarray,
    gh: np.ndarray,
    fl: np.ndarray,
    fh: np.ndarray,
    bar: tuple[int, int] | None,
    fish_y: float | None,
    bar_center: float | None,
    holding: bool,
    paused: bool,
    want_hold: bool,
    fps: float,
    *,
    vision: dict[str, float] | None = None,
    fish_mode: str = "warm",
    fish_mask_override: np.ndarray | None = None,
) -> np.ndarray:
    main = last_bgr.copy()
    hh, ww = main.shape[:2]
    if bar is not None:
        y0, y1 = bar
        cv2.rectangle(main, (0, y0), (ww - 1, y1), (46, 204, 113), 2)
        if bar_center is not None:
            cy = int(bar_center)
            cv2.line(main, (0, cy), (ww - 1, cy), (39, 174, 96), 1)
    if fish_y is not None:
        fy = int(fish_y)
        cv2.line(main, (0, fy), (ww - 1, fy), (52, 152, 219), 2)
        cv2.circle(main, (ww // 2, fy), 5, (52, 152, 219), 2)

    v = vision or DEFAULT_VISION
    gm = green_mask_vis(hsv, gl, gh)
    if fish_mask_override is not None:
        fm = fish_mask_override
    else:
        fm = fish_mask_raw(hsv, fl, fh, gl, gh, vision=v, fish_mode=fish_mode, bgr=last_bgr)
    col_g = cv2.applyColorMap(gm, cv2.COLORMAP_VIRIDIS)
    col_f = cv2.applyColorMap(fm, cv2.COLORMAP_MAGMA)
    strip_w = max(72, ww // 5)
    col_g = cv2.resize(col_g, (strip_w, hh), interpolation=cv2.INTER_NEAREST)
    col_f = cv2.resize(col_f, (strip_w, hh), interpolation=cv2.INTER_NEAREST)
    panel = np.hstack([main, col_g, col_f])

    bar_h = 78
    footer = np.zeros((bar_h, panel.shape[1], 3), dtype=np.uint8)
    footer[:] = (34, 40, 49)
    mode = "PAUSED" if paused else ("HOLD" if holding else "release")
    line1 = f"{mode}  want_hold={want_hold}  fps={fps:.0f}"
    cv2.putText(footer, line1, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (224, 224, 224), 1)
    bf = "bar OK" if bar else "no bar"
    ff = f"fish_y={fish_y:.0f}" if fish_y is not None else "no fish"
    bc = f"cy={bar_center:.0f}" if bar_center is not None else "cy=?"
    line2 = f"{bf}  {ff}  {bc}"
    cv2.putText(footer, line2, (10, 56), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (180, 180, 180), 1)
    cv2.putText(footer, "L main | C green mask | R fish mask", (10, 74), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (120, 120, 120), 1)
    return np.vstack([panel, footer])


# =============================================================================
# 图形界面（Tkinter）
# =============================================================================


class FishingApp:
    """
    主窗口应用：后台工作线程负责截屏+识别+控制，主线程刷新预览与日志。

    线程模型:
        - ``_worker_loop``  在守护线程中运行，通过 ``frame_queue`` 向 UI 推送帧。
        - ``_poll_queue``   主线程每 16ms 取最新帧并更新控件。
    """

    def __init__(self, cfg: dict) -> None:
        self.cfg = cfg
        self.roi = cfg["roi"]
        self.gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
        self.gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
        self.fl = np.array(cfg["fish_hsv"]["low"], dtype=np.uint8)
        self.fh = np.array(cfg["fish_hsv"]["high"], dtype=np.uint8)
        self.ctrl_cfg = cfg["control"]
        self.loop_period = 1.0 / max(float(self.ctrl_cfg.get("loop_hz", 60)), 1.0)
        self.vision = vision_params(cfg)
        self.fish_mode = (cfg.get("fish_detect_mode") or "warm").strip().lower()
        self.fish_template, self.template_match_cfg = load_fish_template_bundle(cfg)
        if self.fish_mode == "template" and self.fish_template is None:
            print("警告：fish_detect_mode=template 但未找到有效 template_match.fish_image，鱼将无法识别。")

        gui_cfg = cfg.get("gui") or {}
        self.preview_max_w = int(gui_cfg.get("preview_max_width", 520))

        self.stop_event = threading.Event()
        self.pause_lock = threading.Lock()
        self.paused = True
        self.calibration_request = False
        self.log_lock = threading.Lock()
        self.frame_queue: queue.Queue[dict[str, Any]] = queue.Queue(maxsize=2)

        self._paused_before_sysid = True
        self.sysid_stop_event: threading.Event | None = None
        self._sysid_thread: threading.Thread | None = None

        self.toggle_key = parse_key(cfg["keys"]["toggle_pause"])
        self.quit_key = parse_key(cfg["keys"]["quit"])
        self.calib_key = parse_key(cfg["keys"]["calibration_mode"])

        self.root = tk.Tk()
        self.root.title("星露谷钓鱼助手 · Stardew Fishing")
        self.root.minsize(640, 480)
        self._setup_style()

        main = ttk.Frame(self.root, padding=8)
        main.pack(fill=tk.BOTH, expand=True)

        top = ttk.Frame(main)
        top.pack(fill=tk.X, pady=(0, 8))

        self.status_banner = tk.Label(
            top,
            text="● 已暂停 — 点击「开始辅助」后才会模拟鼠标",
            font=("Microsoft YaHei UI", 11, "bold"),
            fg="#c0392b",
            bg="#fdeded",
            padx=12,
            pady=8,
            anchor="w",
        )
        self.status_banner.pack(fill=tk.X)

        body = ttk.Frame(main)
        body.pack(fill=tk.BOTH, expand=True)

        left = ttk.Frame(body)
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        self.preview_label = tk.Label(left, bg="#1e1e1e")
        self.preview_label.pack(fill=tk.BOTH, expand=True)

        right = ttk.Frame(body, width=300)
        right.pack(side=tk.RIGHT, fill=tk.BOTH, padx=(12, 0))
        right.pack_propagate(False)

        # 先 pack 底部日志区，再 pack 上方 Notebook；否则 Notebook expand 会占满整列，日志高度为 0。
        log_frame = ttk.Frame(right)
        ttk.Separator(log_frame, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=(6, 4))
        ttk.Label(
            log_frame,
            text="日志（钓鱼 / 绿条辨识 共用）",
            font=("Microsoft YaHei UI", 9, "bold"),
        ).pack(anchor="w")
        self.log_box = scrolledtext.ScrolledText(
            log_frame, height=9, width=32, font=("Consolas", 9), state=tk.DISABLED
        )
        self.log_box.pack(fill=tk.BOTH, expand=True)
        log_frame.pack(side=tk.BOTTOM, fill=tk.BOTH, expand=False)

        nb_frame = ttk.Frame(right)
        nb_frame.pack(side=tk.TOP, fill=tk.BOTH, expand=True)
        nb = ttk.Notebook(nb_frame)
        nb.pack(fill=tk.BOTH, expand=True)

        tab_fish = ttk.Frame(nb, padding=4)
        tab_sysid = ttk.Frame(nb, padding=4)
        nb.add(tab_fish, text="钓鱼")
        nb.add(tab_sysid, text="绿条辨识")

        ttk.Label(tab_fish, text="运行状态", font=("Microsoft YaHei UI", 9, "bold")).pack(anchor="w")
        self.var_mouse = tk.StringVar(value="鼠标左键：松开")
        self.var_reason = tk.StringVar(value="—")
        self.var_error = tk.StringVar(value="误差 err：—")
        self.var_u = tk.StringVar(value="控制量 u：—")
        self.var_fps = tk.StringVar(value="FPS：—")
        self.var_roi = tk.StringVar(
            value=f"ROI {self.roi['left']},{self.roi['top']} {self.roi['width']}x{self.roi['height']}"
        )

        for v in (
            self.var_mouse,
            self.var_reason,
            self.var_error,
            self.var_u,
            self.var_fps,
            self.var_roi,
        ):
            ttk.Label(tab_fish, textvariable=v, wraplength=268, justify=tk.LEFT).pack(anchor="w", pady=2)

        ttk.Separator(tab_fish, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=10)
        ttk.Label(tab_fish, text="操作", font=("Microsoft YaHei UI", 9, "bold")).pack(anchor="w")

        bf = ttk.Frame(tab_fish)
        bf.pack(fill=tk.X, pady=4)
        self.btn_toggle = ttk.Button(bf, text="开始辅助", command=self._on_toggle)
        self.btn_toggle.pack(fill=tk.X, pady=2)
        ttk.Button(bf, text="测试按键（点按一次 c）", command=self._on_test_mouse).pack(fill=tk.X, pady=2)
        ttk.Button(bf, text="打印 ROI 中心 HSV（F7）", command=self._request_calib).pack(fill=tk.X, pady=2)

        ttk.Label(
            tab_fish,
            text="若识别正常但游戏无反应：\n1) 游戏窗口在前台\n2) 与脚本同管理员运行\n3) 点「测试按键」看系统是否收到输入",
            wraplength=268,
            justify=tk.LEFT,
            foreground="#555",
        ).pack(anchor="w", pady=(12, 4))

        ttk.Label(
            tab_sysid,
            text="仅对绿条做系统辨识（需进入钓鱼小游戏，绿条在 ROI 内）。\n"
            "辨识过程完全由你来按/松鼠标左键；脚本只监听输入(u)并记录绿条位置(y)。\n"
            "会暂时暂停钓鱼辅助，避免与辨识抢输入。",
            wraplength=268,
            justify=tk.LEFT,
        ).pack(anchor="w", pady=(0, 8))

        f_sec = ttk.Frame(tab_sysid)
        f_sec.pack(fill=tk.X, pady=2)
        ttk.Label(f_sec, text="时长(s)").pack(side=tk.LEFT)
        self.var_sysid_seconds = tk.StringVar(value="25")
        ttk.Entry(f_sec, width=8, textvariable=self.var_sysid_seconds).pack(side=tk.RIGHT)

        f_hz = ttk.Frame(tab_sysid)
        f_hz.pack(fill=tk.X, pady=2)
        ttk.Label(f_hz, text="采样 loop_hz").pack(side=tk.LEFT)
        self.var_sysid_loop_hz = tk.StringVar(value=str(int(self.ctrl_cfg.get("loop_hz", 60))))
        ttk.Entry(f_hz, width=8, textvariable=self.var_sysid_loop_hz).pack(side=tk.RIGHT)

        ttk.Label(tab_sysid, text="闭环极点 λ1 λ2 λ3（空格分隔，单位圆内）").pack(anchor="w")
        self.var_sysid_poles = tk.StringVar(value="0.82 0.86 0.90")
        ttk.Entry(tab_sysid, textvariable=self.var_sysid_poles).pack(fill=tk.X, pady=2)

        ttk.Label(tab_sysid, text="保存 npz 路径").pack(anchor="w")
        self.var_sysid_npz = tk.StringVar(value=str(CONFIG_PATH.parent / "bar_id_data.npz"))
        ttk.Entry(tab_sysid, textvariable=self.var_sysid_npz).pack(fill=tk.X, pady=2)

        sf = ttk.Frame(tab_sysid)
        sf.pack(fill=tk.X, pady=8)
        self.btn_sysid_start = ttk.Button(sf, text="开始手动辨识", command=self._on_sysid_start)
        self.btn_sysid_start.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 4))
        self.btn_sysid_stop = ttk.Button(sf, text="停止", command=self._on_sysid_stop, state=tk.DISABLED)
        self.btn_sysid_stop.pack(side=tk.RIGHT, fill=tk.X, expand=True, padx=(4, 0))

        ttk.Button(
            tab_sysid,
            text="离线拟合（读上方 npz）",
            command=self._on_sysid_fit,
        ).pack(fill=tk.X, pady=4)

        bottom = ttk.Frame(main)
        bottom.pack(fill=tk.X, pady=(8, 0))
        ttk.Label(bottom, text="热键：F8 开始/暂停  ·  F7 HSV  ·  F9 退出", foreground="#666").pack(side=tk.LEFT)
        ttk.Button(bottom, text="退出", command=self._on_close).pack(side=tk.RIGHT)

        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self._photo_ref: ImageTk.PhotoImage | None = None

        self.worker = threading.Thread(target=self._worker_loop, daemon=True)
        self.worker.start()
        self.root.after(16, self._poll_queue)

    def _setup_style(self) -> None:
        style = ttk.Style()
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        self.root.configure(bg="#eceff4")

    def log(self, msg: str) -> None:
        ts = time.strftime("%H:%M:%S")
        line = f"[{ts}] {msg}\n"
        with self.log_lock:

            def _do() -> None:
                self.log_box.configure(state=tk.NORMAL)
                self.log_box.insert(tk.END, line)
                self.log_box.see(tk.END)
                self.log_box.configure(state=tk.DISABLED)

            self.root.after(0, _do)

    def _on_toggle(self) -> None:
        with self.pause_lock:
            self.paused = not self.paused
            on = not self.paused
        self.log("已暂停（不输出鼠标）" if not on else "已开始辅助：正在根据识别结果按/松左键")
        self._sync_banner()

    def _sync_banner(self) -> None:
        with self.pause_lock:
            p = self.paused
        if p:
            self.status_banner.configure(
                text="● 已暂停 — 不会向游戏发送鼠标；请点击「开始辅助」或按 F8",
                fg="#c0392b",
                bg="#fdeded",
            )
            self.btn_toggle.configure(text="开始辅助")
        else:
            self.status_banner.configure(
                text="● 运行中 — 请保持游戏窗口在前台（钓鱼小游戏界面）",
                fg="#1e8449",
                bg="#e8f8ef",
            )
            self.btn_toggle.configure(text="暂停辅助")

    def _request_calib(self) -> None:
        self.calibration_request = True
        self.log("已请求采样 HSV（下一帧有效）")

    def _on_test_mouse(self) -> None:

        def job() -> None:
            kb = KeyboardController()
            try:
                kb.press("c")
                time.sleep(0.08)
                kb.release("c")
                self.log("测试：已发送一次按键 c 按下+松开")
            except Exception as e:
                self.log(f"测试按键失败: {e}")

        threading.Thread(target=job, daemon=True).start()

    def _on_sysid_start(self) -> None:
        if self._sysid_thread is not None and self._sysid_thread.is_alive():
            self.log("绿条辨识已在运行中")
            return
        try:
            sec = float(self.var_sysid_seconds.get().strip())
            loop_hz = float(self.var_sysid_loop_hz.get().strip())
            poles = [float(x) for x in self.var_sysid_poles.get().split()]
            if len(poles) != 3:
                raise ValueError("需要恰好 3 个闭环极点")
            out_path = Path(self.var_sysid_npz.get().strip())
        except Exception as e:
            self.log(f"绿条辨识参数错误：{e}")
            return

        with self.pause_lock:
            self._paused_before_sysid = self.paused
            self.paused = True
        self.root.after(0, self._sync_banner)

        self.sysid_stop_event = threading.Event()
        poles_arr = np.array(poles, dtype=np.float64)
        self._sysid_thread = threading.Thread(
            target=self._sysid_identify_worker,
            args=(sec, loop_hz, poles_arr, out_path),
            daemon=True,
        )
        self.btn_sysid_start.configure(state=tk.DISABLED)
        self.btn_sysid_stop.configure(state=tk.NORMAL)
        self.log("开始绿条手动辨识：请你自己按/松鼠标左键（已自动暂停钓鱼辅助）…")
        self._sysid_thread.start()

    def _sysid_identify_worker(
        self,
        duration_s: float,
        loop_hz: float,
        poles: np.ndarray,
        output_path: Path,
    ) -> None:
        err: Exception | None = None
        report_lines: str = ""
        try:
            import bar_system_id as bsid

            assert self.sysid_stop_event is not None
            _batch, report_lines = bsid.pipeline_identify_fit(
                self.cfg,
                stop_event=self.sysid_stop_event,
                duration_s=duration_s,
                loop_hz=loop_hz,
                prbs_chip_min=4,
                prbs_chip_max=18,
                prbs_seed=42,
                output_path=output_path,
                poles=poles,
                manual_input=True,
                manual_input_source="mouse_left",
            )
        except Exception as e:
            err = e
        self.root.after(0, lambda e=err, r=report_lines: self._sysid_identify_finish(e, r))

    def _sysid_identify_finish(self, err: Exception | None, report: str) -> None:
        if err is not None:
            self.log(f"绿条辨识异常：{err}")
        elif report:
            for line in report.split("\n"):
                if line.strip():
                    self.log(line)
        with self.pause_lock:
            self.paused = self._paused_before_sysid
        self._sync_banner()
        self.btn_sysid_start.configure(state=tk.NORMAL)
        self.btn_sysid_stop.configure(state=tk.DISABLED)
        self.log("绿条辨识流程结束（已恢复先前「暂停/运行」状态）")

    def _on_sysid_stop(self) -> None:
        if self.sysid_stop_event is not None:
            self.sysid_stop_event.set()
            self.log("已请求停止绿条辨识…")

    def _on_sysid_fit(self) -> None:
        # 必须在主线程执行：Tkinter 的 after 从子线程调度不可靠，易导致「无反馈」
        self.log("正在离线拟合（读取 npz）…")
        try:
            import bar_system_id as bsid

            poles = [float(x) for x in self.var_sysid_poles.get().split()]
            if len(poles) != 3:
                raise ValueError("需要恰好 3 个闭环极点")
            p = Path(self.var_sysid_npz.get().strip())
            if not p.is_file():
                raise FileNotFoundError(f"找不到文件：{p}")
            report = bsid.pipeline_fit_npz(p, np.array(poles, dtype=np.float64))
        except Exception as e:
            self.log(f"离线拟合失败：{e}")
            try:
                messagebox.showerror("离线拟合失败", str(e), parent=self.root)
            except tk.TclError:
                pass
            return
        self.log("—— 离线拟合结果 ——")
        for line in report.split("\n"):
            if line.strip():
                self.log(line)
        # 可选：将拟合得到的 K 写回 config.json（更新 control.arx_k）
        k_vec: list[float] | None = None
        try:
            m = re.search(r"\bK\s*=\s*\[([^\]]+)\]", report)
            if m:
                nums = re.findall(r"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", m.group(1))
                if len(nums) >= 3:
                    k_vec = [float(nums[0]), float(nums[1]), float(nums[2])]
        except Exception:
            k_vec = None

        if k_vec is not None:
            try:
                ok = messagebox.askyesno(
                    "更新控制器？",
                    "离线拟合已得到新的 ARX-K 增益 K。\n\n是否自动写入 config.json 的 control.arx_k 并在下次启动生效？\n（会自动备份原 config.json）",
                    parent=self.root,
                )
            except tk.TclError:
                ok = False
            if ok:
                try:
                    # 备份
                    bak = CONFIG_PATH.parent / f"config.backup_{time.strftime('%Y%m%d_%H%M%S')}.json"
                    try:
                        bak.write_text(CONFIG_PATH.read_text(encoding="utf-8"), encoding="utf-8")
                    except Exception:
                        # 备份失败不阻塞写入，但会提示
                        self.log("警告：配置备份失败（仍将尝试写入新 K）")

                    cfg = load_config()
                    ctrl = cfg.get("control") or {}
                    ctrl["mode"] = "arx_k"
                    ctrl["arx_k"] = [float(k_vec[0]), float(k_vec[1]), float(k_vec[2])]
                    cfg["control"] = ctrl
                    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
                        json.dump(cfg, f, ensure_ascii=False, indent=2)
                    self.log(f"已写入 config.json：control.arx_k = {ctrl['arx_k']}")
                    try:
                        messagebox.showinfo(
                            "已更新",
                            "已写入 config.json。\n请重启脚本以加载新的控制器参数。",
                            parent=self.root,
                        )
                    except tk.TclError:
                        pass
                except Exception as e:
                    self.log(f"自动更新控制器失败：{e}")
                    try:
                        messagebox.showerror("自动更新失败", str(e), parent=self.root)
                    except tk.TclError:
                        pass
        try:
            messagebox.showinfo(
                "离线拟合完成",
                "结果已写入下方「日志」框，请向下滚动查看。",
                parent=self.root,
            )
        except tk.TclError:
            pass

    def _on_close(self) -> None:
        if self.stop_event.is_set():
            try:
                self.root.destroy()
            except tk.TclError:
                pass
            return
        self.stop_event.set()
        if self.sysid_stop_event is not None:
            self.sysid_stop_event.set()
        self.root.after(80, self.root.destroy)

    def _poll_queue(self) -> None:
        if not self.root.winfo_exists():
            return
        latest: dict[str, Any] | None = None
        try:
            while True:
                latest = self.frame_queue.get_nowait()
        except queue.Empty:
            pass
        if latest is not None:
            self._apply_frame(latest)
        self.root.after(16, self._poll_queue)

    def _apply_frame(self, d: dict[str, Any]) -> None:
        img_bgr = d["dashboard"]
        rgb = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2RGB)
        pil = Image.fromarray(rgb)
        w, h = pil.size
        if w > self.preview_max_w:
            nh = int(h * self.preview_max_w / w)
            pil = pil.resize((self.preview_max_w, nh), Image.Resampling.LANCZOS)
        self._photo_ref = ImageTk.PhotoImage(pil)
        self.preview_label.configure(image=self._photo_ref)

        self.var_fps.set(f"FPS：{d.get('fps', 0):.0f}")
        self.var_mouse.set(f"鼠标左键：{'按下' if d.get('holding') else '松开'}")
        self.var_reason.set(f"判定：{d.get('reason', '—')}")
        m = d.get("meta") or {}
        err = m.get("error")
        u = m.get("u")
        ed = m.get("e_dot")
        vf = m.get("v_fish")
        vb = m.get("v_bar")
        v_arx = m.get("v_arx")
        if err is not None and v_arx is not None:
            self.var_error.set(f"误差 err：{err:.1f}px  ARX v={v_arx:.4f}")
        elif err is not None and ed is not None:
            self.var_error.set(
                f"误差 err：{err:.1f}px  e'：{ed:.1f}/s  vf：{vf:.0f} vb：{vb:.0f}"
                if vf is not None and vb is not None
                else f"误差 err：{err:.1f}px  e'：{ed:.1f}/s"
            )
        elif err is not None:
            self.var_error.set(f"误差 err：{err:.1f}px")
        else:
            self.var_error.set("误差 err：—")
        self.var_u.set(f"控制量 u：{u:.3f}" if u is not None else "控制量 u：—")

    def _worker_loop(self) -> None:
        kb = KeyboardController()
        holding = False
        ctrl = build_controller_from_cfg(self.ctrl_cfg)
        sct = mss.mss()
        monitor = {
            "left": int(self.roi["left"]),
            "top": int(self.roi["top"]),
            "width": int(self.roi["width"]),
            "height": int(self.roi["height"]),
        }

        running = True

        def on_press(k: keyboard.Key | keyboard.KeyCode | None) -> None:
            nonlocal running
            if k == self.quit_key:
                running = False
                self.stop_event.set()
                self.root.after(0, self._on_close)
            elif k == self.toggle_key:
                with self.pause_lock:
                    self.paused = not self.paused
                self.root.after(0, self._sync_banner)
                with self.pause_lock:
                    st = "暂停" if self.paused else "运行"
                self.log(f"热键 F8：{st}")
            elif k == self.calib_key:
                self.calibration_request = True
                self.log("热键 F7：将在下一帧采样 ROI 中心 HSV")

        listener = keyboard.Listener(on_press=on_press)
        listener.start()

        fps_ema = 30.0
        fish_y_pref: float | None = None
        last_err_sig: str | None = None
        last_err_t = 0.0
        while not self.stop_event.is_set() and running:
            t0 = time.perf_counter()
            try:

                raw = np.array(sct.grab(monitor))
                last_bgr = cv2.cvtColor(raw, cv2.COLOR_BGRA2BGR) if raw.shape[2] == 4 else raw.copy()
                last_bgr = ensure_bgr_matches_roi(
                    last_bgr, int(self.roi["width"]), int(self.roi["height"])
                )

                if self.calibration_request:
                    self.calibration_request = False
                    h, w, _ = last_bgr.shape
                    cy, cx = h // 2, w // 2
                    patch = last_bgr[max(0, cy - 5) : cy + 5, max(0, cx - 5) : cx + 5]
                    if patch.size == 0:
                        self.log("HSV 采样失败：裁剪为空")
                    else:
                        hsv_patch = cv2.cvtColor(patch, cv2.COLOR_BGR2HSV)
                        avg = hsv_patch.reshape(-1, 3).mean(axis=0).round(1).tolist()
                        self.log(f"ROI 中心 10x10 平均 HSV: {avg}")

                hsv = cv2.cvtColor(last_bgr, cv2.COLOR_BGR2HSV)
                bar = detect_green_bar(hsv, self.gl, self.gh, vision=self.vision)
                # 鱼 y：模板模式加入上一帧 y 引导，减少误匹配抖动
                if self.fish_mode == "template":
                    fish_y_raw = detect_fish_y_template(
                        last_bgr,
                        self.fish_template,
                        self.template_match_cfg,
                        preferred_y=fish_y_pref,
                    )
                else:
                    fish_y_raw = detect_fish_y(
                        hsv,
                        self.fl,
                        self.fh,
                        self.gl,
                        self.gh,
                        vision=self.vision,
                        fish_mode=self.fish_mode,
                        bgr=last_bgr,
                        fish_template=self.fish_template,
                        template_cfg=self.template_match_cfg,
                    )

                # y 稳定器：轻微 EMA + 抑制异常跳变（常来自海草/水纹误检）
                fish_y = fish_y_raw
                if fish_y_raw is not None:
                    alpha = float(self.vision.get("fish_y_smooth_alpha", 0.45))
                    alpha = min(max(alpha, 0.0), 1.0)
                    jump = float(self.vision.get("fish_y_jump_max_px", 42.0))
                    if fish_y_pref is not None and jump > 0 and abs(fish_y_raw - fish_y_pref) > jump:
                        fish_y = fish_y_pref
                    elif fish_y_pref is None or alpha >= 0.999:
                        fish_y = fish_y_raw
                    else:
                        fish_y = alpha * fish_y_raw + (1.0 - alpha) * fish_y_pref
                    fish_y_pref = float(fish_y)
                else:
                    fish_y_pref = None

                # 输出点上移：把标记从“鱼底部”往上挪一点
                if fish_y is not None:
                    up = float(self.vision.get("fish_y_output_up_offset_px", 0.0))
                    if up != 0.0:
                        fish_y = float(fish_y) - float(up)
                        fish_y = max(0.0, min(float(fish_y), float(hsv.shape[0] - 1)))

                fm_override = None
                if self.fish_mode == "template" and self.fish_template is not None:
                    fm_override = fish_mask_from_template_match(
                        last_bgr, self.fish_template, self.template_match_cfg
                    )

                bar_center = None
                if bar is not None:
                    y0, y1 = bar
                    bar_center = (y0 + y1) / 2.0

                target_y = fish_y
                # 只有同时识别到绿条与鱼时才允许输出按键，避免小游戏未开始时抢按键
                if bar_center is None or target_y is None:
                    want_hold, reason, meta = (
                        False,
                        "等待小游戏开始（未识别到绿条或鱼）",
                        {"error": None, "u": None},
                    )
                else:
                    want_hold, reason, meta = ctrl.decide(target_y, bar_center, holding)

                with self.pause_lock:
                    is_paused = self.paused

                if is_paused:
                    if holding:
                        kb.release("c")
                        holding = False
                    ctrl.reset_integral()
                    apply_reason = "已暂停（不输出鼠标；识别仍在运行）"
                else:
                    apply_reason = reason
                    if want_hold and not holding:
                        kb.press("c")
                        holding = True
                    elif not want_hold and holding:
                        kb.release("c")
                        holding = False

                elapsed = time.perf_counter() - t0
                inst_fps = 1.0 / max(elapsed, 1e-6)
                fps_ema = 0.9 * fps_ema + 0.1 * inst_fps

                dash = build_dashboard_bgr(
                    last_bgr,
                    hsv,
                    self.gl,
                    self.gh,
                    self.fl,
                    self.fh,
                    bar,
                    fish_y,
                    bar_center,
                    holding,
                    is_paused,
                    want_hold and not is_paused,
                    fps_ema,
                    vision=self.vision,
                    fish_mode=self.fish_mode,
                    fish_mask_override=fm_override,
                )

                payload = {
                    "dashboard": dash,
                    "holding": holding,
                    "reason": apply_reason,
                    "meta": meta,
                    "fps": fps_ema,
                }
                try:
                    self.frame_queue.put_nowait(payload)
                except queue.Full:
                    try:
                        self.frame_queue.get_nowait()
                    except queue.Empty:
                        pass
                    try:
                        self.frame_queue.put_nowait(payload)
                    except queue.Full:
                        pass

                sp = max(0.0, self.loop_period - (time.perf_counter() - t0))
                time.sleep(sp)
            except Exception as e:
                # 避免工作线程异常导致 UI 永久不刷新
                sig = f"{type(e).__name__}: {e}"
                now = time.perf_counter()
                # 同类异常 1s 内只打印一次，避免刷屏
                if sig != last_err_sig or (now - last_err_t) > 1.0:
                    last_err_sig = sig
                    last_err_t = now
                    self.log(f"工作线程异常：{sig}\n{traceback.format_exc()}")
                try:
                    if holding:
                        kb.release("c")
                        holding = False
                except Exception:
                    pass
                time.sleep(0.2)

        if holding:
            kb.release("c")
        listener.stop()

    def run(self) -> None:
        cm = (self.ctrl_cfg.get("mode") or "pd").strip().lower()
        cdesc = "ARX-K（辨识 Ackermann）" if cm == "arx_k" else "PD 二阶近似"
        self.log(
            f"启动完成。鱼识别：{self.fish_mode}；控制：{cdesc}（mode={cm}）。默认暂停。"
        )
        self._sync_banner()
        self.root.mainloop()


# =============================================================================
# 程序入口
# =============================================================================


def main_headless_cv_debug(cfg: dict) -> None:
    """
    无 GUI 模式（``config.gui.enabled = false``）。

    行为:
        - 控制台打印 F8/F9/F7 热键状态
        - 可选 OpenCV 调试窗口（``debug_visual.enabled = true``）
    """
    roi = cfg["roi"]
    gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
    gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
    fl = np.array(cfg["fish_hsv"]["low"], dtype=np.uint8)
    fh = np.array(cfg["fish_hsv"]["high"], dtype=np.uint8)
    ctrl_cfg = cfg["control"]
    loop_period = 1.0 / max(float(ctrl_cfg.get("loop_hz", 60)), 1.0)
    toggle_key = parse_key(cfg["keys"]["toggle_pause"])
    quit_key = parse_key(cfg["keys"]["quit"])
    calib_key = parse_key(cfg["keys"]["calibration_mode"])

    running = True
    paused = True
    calibration = False
    lock = threading.Lock()

    def on_press(k: keyboard.Key | keyboard.KeyCode | None) -> None:
        nonlocal running, paused, calibration
        if k == quit_key:
            with lock:
                running = False
        elif k == toggle_key:
            with lock:
                paused = not paused
                print("已暂停" if paused else "运行中")
        elif k == calib_key:
            with lock:
                calibration = True

    listener = keyboard.Listener(on_press=on_press)
    listener.start()
    kb = KeyboardController()
    holding = False
    ctrl = build_controller_from_cfg(ctrl_cfg)
    sct = mss.mss()
    monitor = {
        "left": int(roi["left"]),
        "top": int(roi["top"]),
        "width": int(roi["width"]),
        "height": int(roi["height"]),
    }
    dbg = cfg.get("debug_visual") or {}
    debug_enabled = bool(dbg.get("enabled", False))
    debug_max_h = int(dbg.get("window_max_height", 520))
    vp = vision_params(cfg)
    fish_mode_h = (cfg.get("fish_detect_mode") or "warm").strip().lower()
    fish_tpl, tpl_cfg = load_fish_template_bundle(cfg)
    if debug_enabled:
        cv2.namedWindow("fishing_debug", cv2.WINDOW_NORMAL)

    last_bgr: np.ndarray | None = None
    while running:
        t0 = time.perf_counter()
        with lock:
            calib_now = calibration
            is_paused = paused
        if calib_now and last_bgr is not None:
            h, w, _ = last_bgr.shape
            patch = last_bgr[h // 2 - 5 : h // 2 + 5, w // 2 - 5 : w // 2 + 5]
            hsv_patch = cv2.cvtColor(patch, cv2.COLOR_BGR2HSV)
            print("ROI 中心 HSV:", hsv_patch.reshape(-1, 3).mean(axis=0).round(1).tolist())
            with lock:
                calibration = False

        raw = np.array(sct.grab(monitor))
        last_bgr = cv2.cvtColor(raw, cv2.COLOR_BGRA2BGR) if raw.shape[2] == 4 else raw.copy()
        last_bgr = ensure_bgr_matches_roi(last_bgr, int(roi["width"]), int(roi["height"]))
        hsv = cv2.cvtColor(last_bgr, cv2.COLOR_BGR2HSV)
        bar = detect_green_bar(hsv, gl, gh, vision=vp)
        fish_y = detect_fish_y(
            hsv,
            fl,
            fh,
            gl,
            gh,
            vision=vp,
            fish_mode=fish_mode_h,
            bgr=last_bgr,
            fish_template=fish_tpl,
            template_cfg=tpl_cfg,
        )
        if fish_y is not None:
            up = float(vp.get("fish_y_output_up_offset_px", 0.0))
            if up != 0.0:
                fish_y = float(fish_y) - float(up)
                fish_y = max(0.0, min(float(fish_y), float(hsv.shape[0] - 1)))
        bar_center = None
        if bar is not None:
            y0, y1 = bar
            bar_center = (y0 + y1) / 2.0
        target_y = fish_y
        if bar_center is None or target_y is None:
            want_hold = False
        else:
            want_hold, _reason, _meta = ctrl.decide(target_y, bar_center, holding)

        fm_ov = None
        if fish_mode_h == "template" and fish_tpl is not None:
            fm_ov = fish_mask_from_template_match(last_bgr, fish_tpl, tpl_cfg)

        if is_paused:
            if holding:
                kb.release("c")
                holding = False
            ctrl.reset_integral()
        else:
            if want_hold and not holding:
                kb.press("c")
                holding = True
            elif not want_hold and holding:
                kb.release("c")
                holding = False

        if debug_enabled:
            dbg_img = build_dashboard_bgr(
                last_bgr,
                hsv,
                gl,
                gh,
                fl,
                fh,
                bar,
                fish_y,
                bar_center,
                holding,
                is_paused,
                want_hold and not is_paused,
                0.0,
                vision=vp,
                fish_mode=fish_mode_h,
                fish_mask_override=fm_ov,
            )
            hh, ww = dbg_img.shape[:2]
            if hh > debug_max_h:
                sc = debug_max_h / hh
                dbg_img = cv2.resize(dbg_img, (int(ww * sc), debug_max_h))
            cv2.imshow("fishing_debug", dbg_img)
            cv2.waitKey(1)

        time.sleep(max(0.0, loop_period - (time.perf_counter() - t0)))

    if holding:
        kb.release("c")
    if debug_enabled:
        cv2.destroyWindow("fishing_debug")
    listener.stop()


def main() -> None:
    cfg = load_config()
    gui_on = bool((cfg.get("gui") or {}).get("enabled", True))
    if gui_on:
        app = FishingApp(cfg)
        app.run()
    else:
        main_headless_cv_debug(cfg)


if __name__ == "__main__":
    main()
