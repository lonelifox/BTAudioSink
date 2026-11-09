"""生成应用图标：圆角方形底 + 蓝牙符号。

用法：python tools/make_icon.py

输出一套品牌蓝图标：
  app.ico（多尺寸，exe 文件图标 + 窗口/任务栏）、logo.png（标题栏小图）、
  logo@1024.png（README）

托盘图标不在这里生成——它是 App.xaml.cs 的 TrayIconFactory 运行时画的，
未连接是中性灰、已连接是同一套品牌蓝，都写在那边。

形状参数（圆角半径 26%、inset 4%、符号占比 56%、描边 6.2%）跟 TrayIconFactory
是同一套比例，所以 exe 图标和托盘上那枚小图标的轮廓长得一样。唯一的例外是托盘
16~24px 那几档符号不缩小（56% 的白线在这个尺寸里会碎成虚线）。

小尺寸（16/20/24/32）单独处理：底色仍做超采样抗锯齿，但符号按原生像素
描线。蓝牙符号有 5 条线，6.2% 描边做平滑缩放在这段尺寸里会粘成一团白斑，
改用 1 像素细线反而能看出形状；符号也相应放大一点（小图里留白太多，
符号就糊成一粒米，认不出是蓝牙了）。
"""
from PIL import Image, ImageChops, ImageDraw, ImageOps
import io
import os
import struct

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets")

# 品牌蓝：(左上, 右下)
BLUE = ((86, 162, 255), (22, 62, 190))    # #56A2FF → #163EBE
# 托盘未连接态用的是灰 #92929E → #60606C，写在 App.xaml.cs 里，不从这里出

# 圆角方块：inset 4%，圆角半径 26%（与 TrayIconFactory.RoundedRect 相同）
INSET_RATIO = 0.04
RADIUS_RATIO = 0.26

# 符号：占边长 56%，描边 6.2%（与 TrayIconFactory 相同）
# 之前是 70%/7%，白符号顶到方块边缘，四周几乎没有蓝色留白，看着发闷。
GLYPH_SCALE = 0.56
GLYPH_STROKE = 0.062

# 标准蓝牙符号：一条连续折线，取自 24x24 坐标系后归一化
BT_PATH = [
    (6.5, 6.5), (17.5, 17.5), (12.0, 23.0),
    (12.0, 1.0), (17.5, 6.5), (6.5, 17.5),
]

# 小尺寸描线方案：
#   16px   —— 硬像素描线（不再缩放，边缘绝对锐利，最适合资源管理器小图标列表）
#   20~32px —— 超采样后按 1 原生像素描线，既有细线又保留抗锯齿，比硬边均匀
CRISP_HARD = {16: 0.82}
# 20~32px：(符号占比, 描边宽度/原生像素)。宽度随尺寸慢慢加粗，
# 避免 32px 的细线跳到 40px 的 6.2% 粗线时观感断裂。
CRISP_AA = {20: (0.68, 1.0), 24: (0.66, 1.1), 32: (0.62, 1.4)}


def make_gradient(size: int, colors) -> Image.Image:
    """左上→右下对角渐变，跟托盘的 LinearGradientBrush(0,0 → size,size) 同向。

    水平、垂直两个线性渐变取均值，就是精确的对角渐变。
    这里不能用 rotate(45) 的写法：旋转后画布四角是空白的纯黑，
    再被 colorize 涂成第一个颜色，就会在圆角内侧留下四块硬边楔形，
    ico 放大看像四个角上贴了斜边。数学算出来没有这个接缝。
    """
    c1, c2 = colors
    vertical = Image.linear_gradient("L")              # 上黑（0）下白（255）
    horizontal = vertical.transpose(Image.ROTATE_90)   # 逆时针 90°：左黑右白
    ramp = ImageChops.add(horizontal, vertical, scale=2.0)
    ramp = ramp.resize((size, size), Image.BICUBIC)
    return ImageOps.colorize(ramp, c1, c2)


def rounded_mask(size: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    inset = size * INSET_RATIO
    ImageDraw.Draw(mask).rounded_rectangle(
        [inset, inset, size - 1 - inset, size - 1 - inset],
        radius=size * RADIUS_RATIO, fill=255,
    )
    return mask


def glyph_polyline(size: int, scale_ratio: float) -> list:
    scale = size * scale_ratio
    center = size / 2
    return [
        (center + (x / 24 - 0.5) * scale, center + (y / 24 - 0.5) * scale)
        for x, y in BT_PATH
    ]


def _stroke_glyph(canvas: Image.Image, scale_ratio: float, width: int) -> None:
    """在给定画布上描蓝牙折线，端点补圆点（PIL 的 line 没有圆头）。"""
    draw = ImageDraw.Draw(canvas)
    pts = glyph_polyline(canvas.width, scale_ratio)
    draw.line(pts, fill=(255, 255, 255, 255), width=width, joint="curve")
    r = width / 2
    for point in (pts[0], pts[-1]):
        draw.ellipse([point[0] - r, point[1] - r, point[0] + r, point[1] + r],
                     fill=(255, 255, 255, 255))


def _base(size: int, colors) -> Image.Image:
    base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    base.paste(make_gradient(size, colors).convert("RGBA"), (0, 0), rounded_mask(size))
    return base


def draw_hard(size: int, scale_ratio: float, colors) -> Image.Image:
    """16px 专用：不缩放，直接按 1 像素硬边描线，边缘不糊。"""
    base = _base(size, colors)
    glyph = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    _stroke_glyph(glyph, scale_ratio, 1)
    return Image.alpha_composite(base, glyph)


def draw_aa_thin(size: int, scale_ratio: float, width: float, colors) -> Image.Image:
    """20~32px：超采样后按 width 个原生像素描线，缩回来是带抗锯齿的细线。"""
    ss = 8
    S = size * ss
    base = _base(S, colors)
    _stroke_glyph(base, scale_ratio, max(1, round(width * ss)))
    return base.resize((size, size), Image.LANCZOS)


def draw_detailed(size: int, colors) -> Image.Image:
    ss = 8 if size <= 128 else 4
    S = size * ss
    base = _base(S, colors)
    _stroke_glyph(base, GLYPH_SCALE, max(2, round(S * GLYPH_STROKE)))
    return base.resize((size, size), Image.LANCZOS)


def draw_icon(size: int, detailed: bool, colors) -> Image.Image:
    """detailed=True 用超采样平滑绘制（≥40px），False 走小尺寸细线方案。"""
    if not detailed:
        if size in CRISP_HARD:
            return draw_hard(size, CRISP_HARD[size], colors)
        if size in CRISP_AA:
            ratio, width = CRISP_AA[size]
            return draw_aa_thin(size, ratio, width, colors)
    return draw_detailed(size, colors)


def build_ico(images, path):
    """手写 ICO 容器：每个尺寸用独立 PNG，避免统一缩放把 16px 画糊。"""
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        blobs.append(buf.getvalue())

    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = b""
    for im, blob in zip(images, blobs):
        w = 0 if im.width >= 256 else im.width
        h = 0 if im.height >= 256 else im.height
        entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    with open(path, "wb") as f:
        f.write(header + entries + b"".join(blobs))


SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    images = [draw_icon(s, detailed=s >= 40, colors=BLUE) for s in SIZES]
    build_ico(images, os.path.join(OUT_DIR, "app.ico"))
    draw_detailed(256, BLUE).save(os.path.join(OUT_DIR, "logo.png"))
    draw_detailed(1024, BLUE).save(os.path.join(OUT_DIR, "logo@1024.png"))
    print("icons written to", os.path.abspath(OUT_DIR))


if __name__ == "__main__":
    main()
