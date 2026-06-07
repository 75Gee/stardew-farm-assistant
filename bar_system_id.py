# -*- coding: utf-8 -*-
r"""
绿条离散系统辨识与 Ackermann 状态反馈设计。

===============================================================================
问题背景
===============================================================================
钓鱼小游戏中，绿条（有效区域）在按住/松开「使用工具」时呈现近似二阶惯性响应。
本模块**仅对绿条**建模，与「追踪鱼的位置」解耦，用于设计 ``control.mode=arx_k`` 的增益 ``K``。

===============================================================================
离散记号（采样周期 :math:`T_s`）
===============================================================================
- 输出 :math:`y_k`：ROI 图像坐标系下绿条几何中心纵坐标（向下为正，单位 pixel）。
- 输入 :math:`u_k \in \{0,1\}`：是否按住使用工具键（鼠标左键或键盘 c 为 1）。

===============================================================================
ARX(2,2) + 仿射项模型
===============================================================================

    .. math::

        y_{k+1} = a_1 y_k + a_2 y_{k-1} + b_1 u_k + b_2 u_{k-1} + c

回归形式（用于最小二乘）:

    .. math::

        y_k = a_1 y_{k-1} + a_2 y_{k-2} + b_1 u_{k-1} + b_2 u_{k-2} + c

增广状态 :math:`x_k = [y_k,\, y_{k-1},\, u_{k-1}]^\top`，得状态空间
:math:`x_{k+1} = A x_k + B u_k + d`。

对期望闭环极点 :math:`\{\lambda_i\} \subset \mathbb{D}`，用 Ackermann 公式求
:math:`K` 使 :math:`u_k = -K x_k`（实机经继电器量化后由 ``fishing_bot.ControlStateArxK`` 执行）。

===============================================================================
命令行用法
===============================================================================
    python bar_system_id.py identify --seconds 25 --manual
    python bar_system_id.py fit --input bar_id_data.npz

也可在 ``fishing_bot`` 图形界面「绿条辨识」标签页中操作。

免责声明：仅作控制学习与信号处理实验；请自行承担使用风险。
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import dataclass, field
from threading import Event
from pathlib import Path
from typing import Any

import cv2
import mss
import numpy as np
from pynput import keyboard
from pynput import mouse
from pynput.mouse import Button, Controller as MouseController

# 复用主工程中的 ROI 与绿条检测（不引入鱼逻辑）
from fishing_bot import (
    CONFIG_PATH,
    detect_green_bar,
    ensure_bgr_matches_roi,
    load_config,
    vision_params,
)


# ---------------------------------------------------------------------------
# 数据结构
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class IOBatch:
    """一次实验记录：与 :math:`k` 对齐的序列。"""

    t_s: np.ndarray  # shape (N,)
    y_px: np.ndarray  # y_k
    u_binary: np.ndarray  # u_k ∈ {0,1}
    sample_hz: float
    meta: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class ARX2Affine:
    r"""ARX(2,2) + 常数项：对应上文 :math:`a_1,\ldots,c`。"""

    a1: float
    a2: float
    b1: float
    b2: float
    c: float
    r_squared: float
    theta: np.ndarray
    feature_matrix: np.ndarray
    residual: np.ndarray


@dataclass(frozen=True)
class AckermannResult:
    """极点配置结果。"""

    K: np.ndarray  # shape (1, 3)
    desired_poles: np.ndarray
    closed_loop_eigs: np.ndarray
    controllability_rank: int


# ---------------------------------------------------------------------------
# 激励：伪随机二进制序列（PRBS）
# ---------------------------------------------------------------------------


def prbs_sequence(
    n_samples: int,
    *,
    seed: int,
    chip_min: int,
    chip_max: int,
) -> np.ndarray:
    """
    生成长度为 ``n_samples`` 的 PRBS，芯片长度在 ``[chip_min, chip_max]`` 内均匀随机。
    用于对继电器通道做持续激励（避免单一阶跃信息量不足）。
    """
    rng = np.random.default_rng(seed)
    u = np.zeros(n_samples, dtype=np.int8)
    i = 0
    bit: int = 0
    while i < n_samples:
        span = int(rng.integers(chip_min, chip_max + 1))
        bit = 1 - bit
        u[i : min(i + span, n_samples)] = bit
        i += span
    return u


# ---------------------------------------------------------------------------
# 绿条观测
# ---------------------------------------------------------------------------


def grab_roi_bgr(roi: dict[str, int]) -> np.ndarray:
    with mss.mss() as sct:
        mon = {
            "left": int(roi["left"]),
            "top": int(roi["top"]),
            "width": int(roi["width"]),
            "height": int(roi["height"]),
        }
        raw = np.array(sct.grab(mon))
    bgr = cv2.cvtColor(raw, cv2.COLOR_BGRA2BGR) if raw.shape[2] == 4 else raw.copy()
    return ensure_bgr_matches_roi(bgr, int(roi["width"]), int(roi["height"]))


def measure_bar_center_y(
    bgr: np.ndarray,
    gl: np.ndarray,
    gh: np.ndarray,
    *,
    vision: dict[str, float],
) -> float | None:
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    bar = detect_green_bar(hsv, gl, gh, vision=vision)
    if bar is None:
        return None
    y0, y1 = bar
    return float((y0 + y1) / 2.0)


# ---------------------------------------------------------------------------
# 辨识：带仿射项的线性最小二乘
# ---------------------------------------------------------------------------


def fit_arx2_affine(y: np.ndarray, u: np.ndarray) -> ARX2Affine:
    """
    对 ``y[k] = a1*y[k-1] + a2*y[k-2] + b1*u[k-1] + b2*u[k-2] + c`` 做 OLS。

    ``y`` 可含 ``nan``（某帧未检测到绿条）；仅使用 ``y[k],y[k-1],y[k-2]`` 均有限之样本行。
    """
    y = np.asarray(y, dtype=np.float64).ravel()
    u = np.asarray(u, dtype=np.float64).ravel()
    assert y.shape == u.shape
    n = y.shape[0]
    if n < 6:
        raise ValueError("样本过少，无法估计 5 个参数。")

    rows: list[int] = []
    for k in range(2, n):
        if np.all(np.isfinite(y[k - 2 : k + 1])):
            rows.append(k)
    if len(rows) < 6:
        raise ValueError("有效样本过少（绿条检测连续中断过多）。")

    rows_a = np.asarray(rows, dtype=np.int64)
    y_t = y[rows_a]
    phi = np.column_stack(
        [
            y[rows_a - 1],
            y[rows_a - 2],
            u[rows_a - 1],
            u[rows_a - 2],
            np.ones(len(rows_a)),
        ]
    )
    theta, *_rest = np.linalg.lstsq(phi, y_t, rcond=None)
    y_hat = phi @ theta
    resid = y_t - y_hat
    ss_res = float(np.sum(resid**2))
    ss_tot = float(np.sum((y_t - np.mean(y_t)) ** 2))
    r2 = 1.0 - ss_res / ss_tot if ss_tot > 1e-12 else 0.0

    return ARX2Affine(
        a1=float(theta[0]),
        a2=float(theta[1]),
        b1=float(theta[2]),
        b2=float(theta[3]),
        c=float(theta[4]),
        r_squared=r2,
        theta=theta,
        feature_matrix=phi,
        residual=resid,
    )


def companion_ss_affine(m: ARX2Affine) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """由 ARX 参数得到 :math:`(A,B,d)`。"""
    a1, a2, b1, b2, c = m.a1, m.a2, m.b1, m.b2, m.c
    A = np.array([[a1, a2, b2], [1.0, 0.0, 0.0], [0.0, 0.0, 0.0]], dtype=np.float64)
    B = np.array([[b1], [0.0], [1.0]], dtype=np.float64)
    d = np.array([[c], [0.0], [0.0]], dtype=np.float64)
    return A, B, d


def controllability_matrix(A: np.ndarray, B: np.ndarray) -> np.ndarray:
    n = A.shape[0]
    cols = [B]
    for _ in range(1, n):
        cols.append(A @ cols[-1])
    return np.hstack(cols)


def ackermann_single_input(
    A: np.ndarray,
    B: np.ndarray,
    desired_poles: np.ndarray,
) -> AckermannResult:
    r"""
    单输入 Ackermann 极点配置。

    给定首一多项式 :math:`\chi_d(z)=\prod_i (z-\lambda_i)`，计算
    :math:`K` 使得 :math:`\chi_{A-BK}(z)=\chi_d(z)`。
    """
    n = A.shape[0]
    poles = np.asarray(desired_poles, dtype=np.complex128).ravel()
    assert poles.size == n
    Cm = controllability_matrix(A, B)
    rank = int(np.linalg.matrix_rank(Cm))
    if rank < n:
        raise np.linalg.LinAlgError(f"对 (A,B) 不完全能控，rank={rank} < {n}。")

    chi = np.poly(poles)  # monic, degree n
    phi = np.zeros_like(A)
    for i, coeff in enumerate(chi):
        power = n - i
        phi += coeff * np.linalg.matrix_power(A, power)

    e_n = np.zeros((1, n))
    e_n[0, -1] = 1.0
    K = (e_n @ np.linalg.inv(Cm) @ phi).real
    cl = A - B @ K
    eig_cl = np.linalg.eigvals(cl)

    return AckermannResult(
        K=K,
        desired_poles=poles.astype(np.complex128),
        closed_loop_eigs=eig_cl,
        controllability_rank=rank,
    )


# ---------------------------------------------------------------------------
# 实验主循环
# ---------------------------------------------------------------------------


def run_identification_experiment(
    cfg: dict[str, Any],
    *,
    duration_s: float,
    loop_hz: float,
    prbs_chip_min: int,
    prbs_chip_max: int,
    prbs_seed: int,
    stop_event: Event,
) -> IOBatch:
    roi = cfg["roi"]
    gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
    gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
    vp = vision_params(cfg)

    period = 1.0 / max(loop_hz, 1.0)
    n = max(3, int(duration_s * loop_hz))
    u_plan = prbs_sequence(n, seed=prbs_seed, chip_min=prbs_chip_min, chip_max=prbs_chip_max)

    y_buf = np.full(n, np.nan, dtype=np.float64)
    t_s_buf = np.full(n, np.nan, dtype=np.float64)
    u_buf = u_plan.astype(np.float64)
    t0 = time.perf_counter()
    mouse = MouseController()
    holding = False

    k = 0
    while k < n and not stop_event.is_set():
        loop_start = time.perf_counter()
        uk = float(u_plan[k])

        if uk >= 0.5 and not holding:
            mouse.press(Button.left)
            holding = True
        elif uk < 0.5 and holding:
            mouse.release(Button.left)
            holding = False

        bgr = grab_roi_bgr(roi)
        yk = measure_bar_center_y(bgr, gl, gh, vision=vp)

        now = time.perf_counter() - t0
        if yk is not None:
            y_buf[k] = yk
        t_s_buf[k] = now

        k += 1
        elapsed = time.perf_counter() - loop_start
        time.sleep(max(0.0, period - elapsed))

    if holding:
        mouse.release(Button.left)

    n_valid = int(np.sum(np.isfinite(y_buf)))

    return IOBatch(
        t_s=t_s_buf,
        y_px=y_buf,
        u_binary=u_buf,
        sample_hz=loop_hz,
        meta={
            "duration_s": duration_s,
            "prbs_seed": prbs_seed,
            "n_planned": n,
            "n_valid": n_valid,
        },
    )


def run_identification_experiment_manual(
    cfg: dict[str, Any],
    *,
    duration_s: float,
    loop_hz: float,
    stop_event: Event,
    input_source: str = "mouse_left",
) -> IOBatch:
    """
    人工输入版辨识实验：由用户自行按/松输入，脚本仅监听输入状态 u_k 并记录绿条位置 y_k。

    - input_source="mouse_left": 监听鼠标左键按住为 1
    - input_source="key_c": 监听键盘 c 按住为 1
    """
    roi = cfg["roi"]
    gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
    gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
    vp = vision_params(cfg)

    period = 1.0 / max(loop_hz, 1.0)
    n = max(3, int(duration_s * loop_hz))
    y_buf = np.full(n, np.nan, dtype=np.float64)
    t_s_buf = np.full(n, np.nan, dtype=np.float64)
    u_buf = np.zeros(n, dtype=np.float64)
    t0 = time.perf_counter()

    holding = False

    def on_click(_x: int, _y: int, button: Button, pressed: bool) -> None:
        nonlocal holding
        if button == Button.left:
            holding = bool(pressed)

    def on_key(key: keyboard.Key | keyboard.KeyCode | None) -> None:
        nonlocal holding
        try:
            if isinstance(key, keyboard.KeyCode) and key.char and key.char.lower() == "c":
                holding = True
        except Exception:
            pass

    def on_key_release(key: keyboard.Key | keyboard.KeyCode | None) -> None:
        nonlocal holding
        try:
            if isinstance(key, keyboard.KeyCode) and key.char and key.char.lower() == "c":
                holding = False
        except Exception:
            pass

    m_listener: mouse.Listener | None = None
    k_listener: keyboard.Listener | None = None
    if input_source == "mouse_left":
        m_listener = mouse.Listener(on_click=on_click)
        m_listener.start()
    elif input_source == "key_c":
        k_listener = keyboard.Listener(on_press=on_key, on_release=on_key_release)
        k_listener.start()
    else:
        raise ValueError(f"未知 input_source: {input_source}")

    k = 0
    while k < n and not stop_event.is_set():
        loop_start = time.perf_counter()
        u_buf[k] = 1.0 if holding else 0.0

        bgr = grab_roi_bgr(roi)
        yk = measure_bar_center_y(bgr, gl, gh, vision=vp)

        now = time.perf_counter() - t0
        if yk is not None:
            y_buf[k] = yk
        t_s_buf[k] = now

        k += 1
        elapsed = time.perf_counter() - loop_start
        time.sleep(max(0.0, period - elapsed))

    try:
        if m_listener is not None:
            m_listener.stop()
        if k_listener is not None:
            k_listener.stop()
    except Exception:
        pass

    n_valid = int(np.sum(np.isfinite(y_buf)))
    return IOBatch(
        t_s=t_s_buf,
        y_px=y_buf,
        u_binary=u_buf,
        sample_hz=loop_hz,
        meta={
            "duration_s": duration_s,
            "input_source": input_source,
            "n_planned": n,
            "n_valid": n_valid,
        },
    )


def save_batch(path: Path, batch: IOBatch) -> None:
    np.savez_compressed(
        path,
        t_s=batch.t_s,
        y_px=batch.y_px,
        u_binary=batch.u_binary,
        sample_hz=batch.sample_hz,
        meta=json.dumps(batch.meta, ensure_ascii=False),
    )


def load_batch(path: Path) -> IOBatch:
    z = np.load(path, allow_pickle=True)
    meta = json.loads(str(z["meta"]))
    return IOBatch(
        t_s=np.asarray(z["t_s"]),
        y_px=np.asarray(z["y_px"]),
        u_binary=np.asarray(z["u_binary"]),
        sample_hz=float(z["sample_hz"]),
        meta=meta,
    )


# ---------------------------------------------------------------------------
# 报告与 CLI
# ---------------------------------------------------------------------------


def default_bar_id_npz_path() -> Path:
    """与 config.json 同目录下的默认辨识数据文件。"""
    return CONFIG_PATH.parent / "bar_id_data.npz"


def format_identification_report(m: ARX2Affine, A: np.ndarray, B: np.ndarray) -> str:
    lines = [
        "—— ARX(2,2)+affine 最小二乘 ——",
        f"  a1={m.a1:.6f}  a2={m.a2:.6f}  b1={m.b1:.6f}  b2={m.b2:.6f}  c={m.c:.6f}",
        f"  R² = {m.r_squared:.6f}",
    ]
    oh = np.roots([1.0, -m.a1, -m.a2])
    lines.append("  齐次部分极点（仅 y 子系统）： " + np.array2string(oh, precision=4))
    lines.append(f"  能控性矩阵秩：{np.linalg.matrix_rank(controllability_matrix(A, B))}")
    return "\n".join(lines)


def format_control_report(ack: AckermannResult) -> str:
    lines = [
        "—— Ackermann 状态反馈 K（u = -K x，实机需继电器量化）——",
        "  K = " + np.array2string(ack.K, precision=6),
        "  指定极点： " + np.array2string(ack.desired_poles, precision=4),
        "  闭环谱： " + np.array2string(ack.closed_loop_eigs, precision=4),
        f"  能控性秩：{ack.controllability_rank}",
    ]
    return "\n".join(lines)


def print_identification_report(m: ARX2Affine, A: np.ndarray, B: np.ndarray) -> None:
    print(format_identification_report(m, A, B))


def print_control_report(ack: AckermannResult) -> None:
    print(format_control_report(ack))


def pipeline_identify_fit(
    cfg: dict[str, Any],
    *,
    stop_event: Event,
    duration_s: float,
    loop_hz: float,
    prbs_chip_min: int,
    prbs_chip_max: int,
    prbs_seed: int,
    output_path: Path,
    poles: np.ndarray,
    manual_input: bool = False,
    manual_input_source: str = "mouse_left",
) -> tuple[IOBatch | None, str]:
    """
    在线 PRBS → 保存 npz → OLS → Ackermann。
    返回 ``(batch, 报告文本)``；样本过少时 ``batch`` 仍返回，报告中含警告。
    """
    if manual_input:
        batch = run_identification_experiment_manual(
            cfg,
            duration_s=duration_s,
            loop_hz=loop_hz,
            stop_event=stop_event,
            input_source=manual_input_source,
        )
    else:
        batch = run_identification_experiment(
            cfg,
            duration_s=duration_s,
            loop_hz=loop_hz,
            prbs_chip_min=prbs_chip_min,
            prbs_chip_max=prbs_chip_max,
            prbs_seed=prbs_seed,
            stop_event=stop_event,
        )
    save_batch(output_path, batch)
    n_valid = int(batch.meta.get("n_valid", 0))
    lines: list[str] = [
        f"已保存 {output_path}",
        f"有效帧（检测到绿条）{n_valid} / {batch.y_px.size}",
    ]
    if n_valid < 32:
        lines.append("警告：有效样本过少，辨识可能不可靠（检查 ROI / green_hsv）。")
        return batch, "\n".join(lines)

    m = fit_arx2_affine(batch.y_px, batch.u_binary)
    A, B, _d = companion_ss_affine(m)
    lines.append(format_identification_report(m, A, B))
    try:
        ack = ackermann_single_input(A, B, np.asarray(poles, dtype=np.float64).ravel())
        lines.append(format_control_report(ack))
    except np.linalg.LinAlgError as e:
        lines.append(f"极点配置失败：{e}")
    return batch, "\n".join(lines)


def pipeline_fit_npz(input_path: Path, poles: np.ndarray) -> str:
    """对已保存 npz 离线 OLS + Ackermann，返回报告文本。"""
    batch = load_batch(input_path)
    n_valid = int(np.sum(np.isfinite(batch.y_px)))
    lines: list[str] = [
        f"数据文件：{input_path}",
        f"有效 y 样本数：{n_valid}",
    ]
    if n_valid < 32:
        lines.append("警告：有效样本过少。")
    m = fit_arx2_affine(batch.y_px, batch.u_binary)
    A, B, _d = companion_ss_affine(m)
    lines.append(format_identification_report(m, A, B))
    try:
        ack = ackermann_single_input(A, B, np.asarray(poles, dtype=np.float64).ravel())
        lines.append(format_control_report(ack))
    except np.linalg.LinAlgError as e:
        lines.append(f"极点配置失败：{e}")
    return "\n".join(lines)


def cmd_identify(args: argparse.Namespace) -> None:
    cfg = load_config()
    stop_event = Event()

    def on_press(key: keyboard.Key | keyboard.KeyCode | None) -> None:
        if key == keyboard.Key.esc:
            stop_event.set()

    listener = keyboard.Listener(on_press=on_press)
    listener.start()

    print("辨识实验开始：请在钓鱼小游戏界面保持绿条可见（无需管鱼）。")
    print("按 Esc 可提前结束。")
    if args.manual:
        src = str(args.manual_source)
        if src == "mouse_left":
            print("人工输入模式：请你自己按/松鼠标左键，脚本会监听并记录 u_k。")
        else:
            print("人工输入模式：请你自己按/松键盘 c，脚本会监听并记录 u_k。")
    else:
        print("自动输入模式：正在施加 PRBS 鼠标激励…")

    try:
        _batch, report = pipeline_identify_fit(
            cfg,
            stop_event=stop_event,
            duration_s=args.seconds,
            loop_hz=args.loop_hz,
            prbs_chip_min=args.chip_min,
            prbs_chip_max=args.chip_max,
            prbs_seed=args.seed,
            output_path=Path(args.output),
            poles=np.array(args.poles, dtype=np.float64),
            manual_input=bool(args.manual),
            manual_input_source=str(args.manual_source),
        )
        print(report)
    finally:
        listener.stop()


def cmd_fit(args: argparse.Namespace) -> None:
    report = pipeline_fit_npz(Path(args.input), np.array(args.poles, dtype=np.float64))
    print(report)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="绿条离散 ARX 辨识 + Ackermann 极点配置（学术向单文件实验）",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    sub = p.add_subparsers(dest="cmd", required=True)

    pi = sub.add_parser("identify", help="在线 PRBS 激励并保存数据，同时 OLS + 设计 K")
    pi.add_argument("--seconds", type=float, default=25.0, help="实验时长（秒）")
    pi.add_argument("--loop-hz", type=float, default=60.0, help="采样/控制回路标称频率")
    pi.add_argument("--chip-min", type=int, default=4, help="PRBS 最短芯片（帧）")
    pi.add_argument("--chip-max", type=int, default=18, help="PRBS 最长芯片（帧）")
    pi.add_argument("--seed", type=int, default=42, help="PRBS 随机种子")
    pi.add_argument("--manual", action="store_true", help="人工输入：不自动点击，由你自己按/松输入")
    pi.add_argument(
        "--manual-source",
        type=str,
        default="mouse_left",
        choices=["mouse_left", "key_c"],
        help="人工输入的来源：鼠标左键或键盘 c",
    )
    pi.add_argument(
        "--output",
        type=str,
        default=str(Path(__file__).resolve().parent / "bar_id_data.npz"),
        help="输出 npz 路径",
    )
    pi.add_argument(
        "--poles",
        type=float,
        nargs=3,
        default=[0.82, 0.86, 0.90],
        metavar=("λ1", "λ2", "λ3"),
        help="期望离散闭环极点（单位圆内）",
    )

    pf = sub.add_parser("fit", help="对已保存 npz 离线 OLS + 设计 K")
    pf.add_argument("--input", type=str, required=True, help="npz 路径")
    pf.add_argument(
        "--poles",
        type=float,
        nargs=3,
        default=[0.82, 0.86, 0.90],
        metavar=("λ1", "λ2", "λ3"),
        help="期望离散闭环极点（单位圆内）",
    )

    return p


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    if args.cmd == "identify":
        cmd_identify(args)
    elif args.cmd == "fit":
        cmd_fit(args)
    else:
        parser.error("未知子命令")


if __name__ == "__main__":
    if not CONFIG_PATH.is_file():
        print(f"未找到配置文件: {CONFIG_PATH}", file=sys.stderr)
        sys.exit(1)
    main()
