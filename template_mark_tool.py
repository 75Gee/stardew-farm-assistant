# -*- coding: utf-8 -*-
"""
鱼图标模板标注工具。

功能:
    在钓鱼小游戏 ROI 截图上框选「小鱼」图案，保存为 ``templates/fish_template.png``，
    并自动将 ``config.json`` 的 ``fish_detect_mode`` 设为 ``template``。

前置条件:
    - 截图尺寸必须与 ``config.json`` 中 ``roi.width × roi.height`` 一致
      （或先用 ``roi_calibrator.py`` 校准 ROI 后再截同区域）。
    - 框选时尽量只包含鱼身，减少背景干扰。

用法:
    python template_mark_tool.py 你的截图.png

窗口操作（焦点在图像窗口上）:
    鼠标左键拖动  画矩形框
    S             保存模板并写回 config.json
    C             清除当前框
    R             重新载入图片
    Q / ESC       退出
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2

from image_io import imread_unicode, imwrite_unicode

# =============================================================================
# 路径常量
# =============================================================================

ROOT = Path(__file__).resolve().parent
CONFIG_PATH = ROOT / "config.json"
TEMPLATES_DIR = ROOT / "templates"
DEFAULT_OUT = "templates/fish_template.png"

# 交互状态：由鼠标回调与主循环共享
_state: dict = {
    "img": None,
    "path": None,
    "x0": 0,
    "y0": 0,
    "x1": 0,
    "y1": 0,
    "drag": False,
}


def _norm_rect() -> tuple[int, int, int, int]:
    """将任意方向拖动的两点规范为 (x_min, y_min, x_max, y_max)。"""
    x0, y0, x1, y1 = _state["x0"], _state["y0"], _state["x1"], _state["y1"]
    return (
        min(x0, x1),
        min(y0, y1),
        max(x0, x1),
        max(y0, y1),
    )


def _draw_overlay() -> None:
    """在预览窗口重绘当前图像与框选矩形。"""
    img = _state["img"]
    if img is None:
        return
    vis = img.copy()
    if _state["drag"] or (_state["x0"] != _state["x1"] and _state["y0"] != _state["y1"]):
        xa, ya, xb, yb = _norm_rect()
        cv2.rectangle(vis, (xa, ya), (xb, yb), (0, 255, 255), 2)
    cv2.putText(
        vis,
        "Drag rect | S save | C clear | R reload | Q quit",
        (8, 24),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.5,
        (0, 255, 255),
        1,
    )
    cv2.imshow("mark_fish_template", vis)


def _mouse(event: int, x: int, y: int, _flags: int, _param: object) -> None:
    """OpenCV 鼠标回调：记录框选起止坐标。"""
    if _state["img"] is None:
        return
    if event == cv2.EVENT_LBUTTONDOWN:
        _state["drag"] = True
        _state["x0"] = _state["x1"] = x
        _state["y0"] = _state["y1"] = y
    elif event == cv2.EVENT_MOUSEMOVE and _state["drag"]:
        _state["x1"], _state["y1"] = x, y
        _draw_overlay()
    elif event == cv2.EVENT_LBUTTONUP:
        _state["drag"] = False
        _state["x1"], _state["y1"] = x, y
        _draw_overlay()


def _save_template() -> None:
    """
    裁剪框选区域，保存 PNG 模板，并更新 config.json。

    写入字段:
        - fish_detect_mode = "template"
        - template_match.fish_image = "templates/fish_template.png"
    """
    img = _state["img"]
    if img is None:
        print("无图像")
        return
    xa, ya, xb, yb = _norm_rect()
    if xb - xa < 4 or yb - ya < 4:
        print("框太小或未拖动，请先拖出一个矩形包住鱼")
        return
    crop = img[ya:yb, xa:xb]
    TEMPLATES_DIR.mkdir(parents=True, exist_ok=True)
    out_path = TEMPLATES_DIR / "fish_template.png"
    imwrite_unicode(out_path, crop)
    print(f"已保存模板: {out_path}  ({crop.shape[1]}x{crop.shape[0]} px)")

    if not CONFIG_PATH.is_file():
        print(f"未找到 {CONFIG_PATH}，请手动在 config.json 添加 template_match")
        return
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    cfg["fish_detect_mode"] = "template"
    cfg["template_match"] = {
        "fish_image": DEFAULT_OUT,
        "method": "ccoeff_normed",
        "min_score": 0.38,
    }
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    print(f"已设置 fish_detect_mode=template，并写入 template_match 到 {CONFIG_PATH}")
    print("若匹配不稳，可在 config.json 里把 min_score 降到 0.30～0.35 试试。")


def main() -> None:
    """命令行入口：加载图片并进入交互标注循环。"""
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    path = Path(sys.argv[1]).expanduser().resolve()
    if not path.is_file():
        print(f"文件不存在: {path}")
        sys.exit(1)
    img = imread_unicode(path)
    if img is None:
        print(f"无法读取图像: {path}")
        sys.exit(1)
    _state["img"] = img
    _state["path"] = path
    _state["x0"] = _state["y0"] = _state["x1"] = _state["y1"] = 0

    win = "mark_fish_template"
    cv2.namedWindow(win, cv2.WINDOW_NORMAL)
    cv2.setMouseCallback(win, _mouse)
    _draw_overlay()
    print(__doc__)

    while True:
        key = cv2.waitKey(30) & 0xFF
        if key in (ord("q"), ord("Q"), 27):
            break
        if key in (ord("s"), ord("S")):
            _save_template()
        if key in (ord("c"), ord("C")):
            _state["x0"] = _state["y0"] = _state["x1"] = _state["y1"] = 0
            _draw_overlay()
        if key in (ord("r"), ord("R")) and _state["path"]:
            img2 = imread_unicode(_state["path"])
            if img2 is not None:
                _state["img"] = img2
                _draw_overlay()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
