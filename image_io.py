# -*- coding: utf-8 -*-
"""
图像读写工具（Windows 中文路径兼容层）。

背景:
    OpenCV 的 ``cv2.imread`` / ``cv2.imwrite`` 在 Windows 上遇到含中文或特殊字符的路径时，
    常会静默失败并返回 ``None``。本模块通过「先读字节 → 内存解码」的方式绕过该限制。

用法:
    from image_io import imread_unicode, imwrite_unicode

    img = imread_unicode("D:/截图/鱼模板.png")
    imwrite_unicode("templates/fish_template.png", crop)
"""

from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np


def imread_unicode(path: str | Path) -> np.ndarray | None:
    """
    从磁盘读取 BGR 彩色图像，支持 Unicode 路径。

    参数:
        path: 图像文件路径（支持中文目录名）。

    返回:
        BGR 格式的 ``numpy.ndarray``；文件不存在、为空或解码失败时返回 ``None``。
    """
    p = Path(path)
    try:
        with open(p, "rb") as f:
            buf = f.read()
    except OSError:
        return None
    if not buf:
        return None
    arr = np.frombuffer(buf, dtype=np.uint8)
    return cv2.imdecode(arr, cv2.IMREAD_COLOR)


def imwrite_unicode(path: str | Path, img: np.ndarray) -> bool:
    """
    将 BGR 图像写入磁盘，支持 Unicode 路径。

    参数:
        path: 目标文件路径；父目录不存在时会自动创建。
        img:  BGR 格式图像数组。

    返回:
        写入成功返回 ``True``，编码失败或磁盘写入失败返回 ``False``。
    """
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    ext = p.suffix.lower() if p.suffix else ".png"
    ok, buf = cv2.imencode(ext, img)
    if not ok or buf is None:
        return False
    try:
        with open(p, "wb") as f:
            f.write(buf.tobytes())
    except OSError:
        return False
    return True
