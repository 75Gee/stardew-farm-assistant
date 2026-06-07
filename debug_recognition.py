# -*- coding: utf-8 -*-
"""
钓鱼 ROI 识别离线调试工具。

功能:
    对静态图片运行与 ``fishing_bot`` 完全相同的绿条/鱼检测流水线，
    在控制台打印数值，并可选弹出四宫格可视化窗口。

尺寸处理:
    - 直接传入 ROI 小图：任意尺寸会先缩放到 ``config.roi`` 的 width×height。
    - 传入整屏截图：加 ``--full-frame``，先按 roi 裁剪再缩放。

用法:
    python debug_recognition.py 截图.png
    python debug_recognition.py 整屏.png --full-frame
    python debug_recognition.py 截图.png --no-window   # 仅打印，不弹窗

弹窗四宫格:
    左上  main+overlay   绿框=绿条，橙线=鱼 y
    右上  green_mask     绿条二值掩膜
    左下  fish_mask      当前 fish_detect_mode 下的鱼掩膜
    右下  template_heat  模板模式下的匹配得分热力图（亮=高相关）

调参提示:
    控制台会打印模板「原始最高分」，即使低于 min_score 也会显示，便于调整阈值。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

print("debug_recognition: 正在载入 OpenCV / fishing_bot（首次运行可能需几秒）...", flush=True)

import cv2
import numpy as np

from image_io import imread_unicode

from fishing_bot import (
    CONFIG_PATH,
    TEMPLATE_MATCH_METHODS,
    detect_fish_y,
    detect_green_bar,
    fish_mask_from_template_match,
    fish_mask_raw,
    green_bar_binary_mask,
    load_fish_template_bundle,
    match_fish_template_full,
    vision_params,
)


def _resolve_image_path(arg: str) -> Path | None:
    """
    解析用户传入的图片路径。

    查找顺序:
        1. 绝对路径
        2. 当前工作目录
        3. 脚本同目录（仅按文件名）

    返回:
        第一个存在的文件路径；均不存在时返回 ``None``。
    """
    raw = Path(arg).expanduser()
    script_dir = Path(__file__).resolve().parent
    candidates: list[Path] = []
    if raw.is_absolute():
        candidates.append(raw)
    else:
        candidates.append(Path.cwd() / raw)
        candidates.append(script_dir / raw.name)
        candidates.append(raw)
    seen: set[str] = set()
    for p in candidates:
        try:
            r = p.resolve()
        except OSError:
            continue
        key = str(r)
        if key in seen:
            continue
        seen.add(key)
        if r.is_file():
            return r
    return None


def _resize_h(img: np.ndarray, target_h: int) -> np.ndarray:
    if img.size == 0:
        return img
    h, w = img.shape[:2]
    if h == target_h:
        return img
    scale = target_h / h
    return cv2.resize(img, (int(w * scale), target_h), interpolation=cv2.INTER_NEAREST)


def _gray_to_bgr(g: np.ndarray) -> np.ndarray:
    if len(g.shape) == 2:
        return cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)
    return g


def _align_rows_for_vstack(row1: np.ndarray, row2: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """热力图与主图缩放后宽高可能不一致，右侧/下侧填黑后再纵向拼接。"""
    h = max(row1.shape[0], row2.shape[0])
    w = max(row1.shape[1], row2.shape[1])

    def pad(r: np.ndarray) -> np.ndarray:
        cur_h, cur_w = r.shape[:2]
        if len(r.shape) == 2:
            r = cv2.cvtColor(r, cv2.COLOR_GRAY2BGR)
        out = np.zeros((h, w, 3), dtype=np.uint8)
        out[:cur_h, :cur_w] = r[:cur_h, :cur_w]
        return out

    return pad(row1), pad(row2)


def _template_match_heatmap(
    bgr: np.ndarray,
    tpl: np.ndarray,
    method: str,
) -> np.ndarray | None:
    if tpl is None or bgr is None or tpl.size == 0:
        return None
    th, tw = tpl.shape[:2]
    ih, iw = bgr.shape[:2]
    if th >= ih or tw >= iw:
        return None
    tup = TEMPLATE_MATCH_METHODS.get(method.lower(), TEMPLATE_MATCH_METHODS["ccoeff_normed"])
    code, _mode = tup
    gi = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    gt = cv2.cvtColor(tpl, cv2.COLOR_BGR2GRAY)
    res = cv2.matchTemplate(gi, gt, code)
    res_u8 = cv2.normalize(res, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    return cv2.applyColorMap(res_u8, cv2.COLORMAP_JET)


def main() -> None:
    parser = argparse.ArgumentParser(description="钓鱼 ROI 识别离线调试")
    parser.add_argument("image", help="图片路径（BGR）")
    parser.add_argument(
        "--full-frame",
        action="store_true",
        help="整屏截图：按 config.json 的 roi 裁剪后再识别",
    )
    parser.add_argument(
        "--config",
        type=Path,
        default=None,
        help=f"配置文件（默认 {CONFIG_PATH}）",
    )
    parser.add_argument(
        "--no-window",
        action="store_true",
        help="不弹 OpenCV 窗口，只打印控制台结果（适合远程/无界面排查）",
    )
    args = parser.parse_args()
    cfg_path = args.config or CONFIG_PATH
    if not cfg_path.is_file():
        print(f"找不到配置: {cfg_path}")
        sys.exit(1)

    img_path = _resolve_image_path(args.image)
    if img_path is None:
        print(
            f"找不到图片: {args.image!r}\n"
            f"  当前工作目录: {Path.cwd()}\n"
            f"  脚本目录: {Path(__file__).resolve().parent}\n"
            f"  请使用完整路径，例如: python debug_recognition.py D:\\\\截图\\\\roi.png",
            flush=True,
        )
        sys.exit(1)
    print(f"使用图片: {img_path}", flush=True)

    bgr = imread_unicode(img_path)
    if bgr is None:
        print(f"无法读取图片（文件损坏或格式不支持）: {img_path}", flush=True)
        sys.exit(1)

    with open(cfg_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    roi = cfg["roi"]
    rw, rh = int(roi["width"]), int(roi["height"])
    if args.full_frame:
        l, t = int(roi["left"]), int(roi["top"])
        h_img, w_img = bgr.shape[:2]
        if l + rw > w_img or t + rh > h_img:
            print(
                f"裁剪越界：roi left={l} top={t} size={rw}x{rh}，图像仅 {w_img}x{h_img}。"
                "请确认整屏截图分辨率与游戏窗口一致，或不用 --full-frame 直接传 ROI 小图。"
            )
            sys.exit(1)
        bgr = bgr[t : t + rh, l : l + rw].copy()
        print(f"已从整屏裁剪 ROI: ({l},{t}) {rw}x{rh}")

    ih, iw = bgr.shape[:2]
    if iw != rw or ih != rh:
        print(f"已将图片 {iw}x{ih} 线性缩放到 ROI 配置 {rw}x{rh}（与 fishing_bot 运行时一致）")
        bgr = cv2.resize(bgr, (rw, rh), interpolation=cv2.INTER_LINEAR)

    gl = np.array(cfg["green_hsv"]["low"], dtype=np.uint8)
    gh = np.array(cfg["green_hsv"]["high"], dtype=np.uint8)
    fl = np.array(cfg["fish_hsv"]["low"], dtype=np.uint8)
    fh = np.array(cfg["fish_hsv"]["high"], dtype=np.uint8)
    vision = vision_params(cfg)
    fish_mode = (cfg.get("fish_detect_mode") or "warm").strip().lower()
    fish_tpl, tpl_cfg = load_fish_template_bundle(cfg)

    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)

    print("========== 识别调试 ==========")
    print(f"图片: {img_path.name}  实际像素: {bgr.shape[1]}x{bgr.shape[0]}")
    print(f"fish_detect_mode: {fish_mode}")
    print(f"template_match: {cfg.get('template_match')}")

    # 模板文件检查 + 常见拼写
    root = Path(__file__).resolve().parent
    tm = cfg.get("template_match") or {}
    tpath = tm.get("fish_image")
    if tpath:
        p = (root / tpath).resolve()
        print(f"模板路径解析为: {p}  存在={p.is_file()}")
        if not p.is_file():
            alt = root / "templates" / "fish_temple.png"
            if alt.is_file():
                print(f"提示：发现可能是拼写错误文件 {alt.name}，请改名为 fish_template.png 或改 config 中 fish_image。")

    bar = detect_green_bar(hsv, gl, gh, vision=vision)
    if bar is None:
        print("绿条: 未识别")
        bar_center = None
    else:
        y0, y1 = bar
        bar_center = (y0 + y1) / 2.0
        print(f"绿条: y0={y0} y1={y1}  center_y={bar_center:.1f}")

    fish_y = detect_fish_y(
        hsv,
        fl,
        fh,
        gl,
        gh,
        vision=vision,
        fish_mode=fish_mode,
        bgr=bgr,
        fish_template=fish_tpl,
        template_cfg=tpl_cfg,
    )
    if fish_y is None:
        print("鱼: 未识别（当前阈值/模式下无有效结果）")
    else:
        print(f"鱼: center_y={fish_y:.1f}")

    if fish_mode == "template" and fish_tpl is not None:
        min_sc = float(tpl_cfg.get("min_score", 0.38))
        cfg_any = {**tpl_cfg, "min_score": -1.0}
        r_any = match_fish_template_full(bgr, fish_tpl, cfg_any)
        if r_any is not None:
            _cy, lx, ly, twm, thm, raw_best = r_any
            print(
                f"模板匹配（多尺度）最佳 score={raw_best:.4f}（config min_score={min_sc}），"
                f"框={twm}x{thm} @({lx},{ly})"
            )
            if raw_best < min_sc:
                print(f"  → 可把 template_match.min_score 降到 {max(0.05, raw_best - 0.03):.2f} 以下再试。")
        else:
            print("模板匹配：多尺度下仍无法放入 ROI（检查模板是否过大）。")
    elif fish_mode == "template" and fish_tpl is None:
        print("模板模式但未加载到模板图：请检查 template_match.fish_image 路径与文件名。")

    green_m = green_bar_binary_mask(hsv, gl, gh)
    if fish_mode == "template" and fish_tpl is not None:
        fish_m_vis = fish_mask_from_template_match(bgr, fish_tpl, tpl_cfg)
    else:
        fish_m_vis = fish_mask_raw(hsv, fl, fh, gl, gh, vision=vision, fish_mode=fish_mode, bgr=bgr)

    vis = bgr.copy()
    hh, ww = vis.shape[:2]
    if bar is not None:
        y0, y1 = bar
        cv2.rectangle(vis, (0, y0), (ww - 1, y1), (0, 200, 0), 2)
        if bar_center is not None:
            cv2.line(vis, (0, int(bar_center)), (ww - 1, int(bar_center)), (0, 160, 0), 1)
    if fish_y is not None:
        fy = int(fish_y)
        cv2.line(vis, (0, fy), (ww - 1, fy), (255, 120, 0), 2)

    heat = None
    if fish_mode == "template" and fish_tpl is not None:
        heat = _template_match_heatmap(bgr, fish_tpl, str(tpl_cfg.get("method", "ccoeff_normed")))

    h_disp = min(420, max(200, bgr.shape[0]))
    row1 = np.hstack(
        [
            _resize_h(vis, h_disp),
            _resize_h(_gray_to_bgr(cv2.applyColorMap(green_m, cv2.COLORMAP_VIRIDIS)), h_disp),
        ]
    )
    row2_left = _resize_h(_gray_to_bgr(cv2.applyColorMap(fish_m_vis, cv2.COLORMAP_MAGMA)), h_disp)
    if heat is not None:
        row2_right = _resize_h(heat, h_disp)
    else:
        row2_right = np.zeros_like(row2_left)
        cv2.putText(row2_right, "no template heat", (12, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (200, 200, 200), 1)
    row2 = np.hstack([row2_left, row2_right])
    row1, row2 = _align_rows_for_vstack(row1, row2)
    panel = np.vstack([row1, row2])
    cv2.putText(
        panel,
        "1:main+overlay 2:green 3:fish 4:tpl heat | any key close",
        (6, 20),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.45,
        (255, 255, 255),
        1,
    )

    if args.no_window:
        print("已跳过弹窗（--no-window）。", flush=True)
    else:
        try:
            cv2.namedWindow("debug_recognition", cv2.WINDOW_NORMAL)
            cv2.imshow("debug_recognition", panel)
            print(
                "已打开窗口「debug_recognition」。若桌面无反应，请 Alt+Tab 或看任务栏；在窗口内按任意键关闭。",
                flush=True,
            )
            cv2.waitKey(0)
        except Exception as e:
            print(f"OpenCV 无法显示窗口: {e}\n可改用: python debug_recognition.py <图> --no-window", flush=True)
        finally:
            try:
                cv2.destroyAllWindows()
            except Exception:
                pass
    print("========== 结束 ==========", flush=True)


if __name__ == "__main__":
    main()
