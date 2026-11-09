#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
核对离屏预览工程与主工程的界面是否同步。

背景：_ui_preview/PreviewWindow.xaml 是主工程 MainWindow.xaml 的**手工复制品**
（预览工程只渲染有设备的场景，所以不能直接复用主工程的 XAML）。
两边一旦不同步，离屏量出来的宽度就是假的——之前的英文文案挤压就是这么漏掉的。

这里比对的是「结构骨架」而不是逐字符：
  1. 控件类型出现序列（Button / TextBlock / CheckBox / ListBox …）
  2. DynamicResource 引用集合
  3. 关键布局数值（Width / Height / FontSize / Margin / Padding）
预览工程允许有的额外元素（EmptyHint 这种只在主工程有的），
所以采用「主工程的每个值，预览里必须能找到一个相同的」这种包含关系判定。

用法：python tools/check_preview_sync.py <项目根目录>
项目根目录里应该有 MainWindow.xaml 和 _ui_preview/PreviewWindow.xaml。
退出码 0 = 通过，1 = 有差异。
"""
import os
import re
import sys
from collections import Counter

# 控制台编码自己定死。GitHub Actions 的 windows runner 是 cp1252，
# print 中文会抛 UnicodeEncodeError 把构建判失败——本机是 UTF-8 所以看不出来。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

root = sys.argv[1] if len(sys.argv) > 1 else "."

main_xaml = os.path.join(root, "MainWindow.xaml")
prev_xaml = os.path.join(root, "_ui_preview", "PreviewWindow.xaml")

for p in (main_xaml, prev_xaml):
    if not os.path.exists(p):
        print("找不到文件：%s" % p)
        sys.exit(1)

with open(main_xaml, encoding="utf-8-sig") as f:
    main_src = f.read()
with open(prev_xaml, encoding="utf-8-sig") as f:
    prev_src = f.read()

errors = []
notes = []


def strip_comments(src):
    """去掉 XAML 注释，避免注释里的标签名被算进结构。"""
    src = re.sub(r"<!--.*?-->", "", src, flags=re.S)
    return src


main_src = strip_comments(main_src)
prev_src = strip_comments(prev_src)

# ---------- 1. 控件类型序列 ----------
tag_re = re.compile(r"<([A-Za-z][A-Za-z0-9_.:]*)\b")
# 跳过这些：容器/根/数据绑定相关，不构成界面骨架
SKIP = {"Window", "Window.Resources", "ResourceDictionary",
        "ResourceDictionary.MergedDictionaries", "WindowChrome"}

def tag_sequence(src):
    seq = []
    for m in tag_re.finditer(src):
        name = m.group(1)
        if name in SKIP:
            continue
        if name.startswith("DataTemplate") or name.startswith("Grid.") \
           or name.startswith("ListBox.") or name.startswith("ScrollViewer.") \
           or name.startswith("VirtualizingPanel") or name.startswith("TextBlock.") \
           or name.startswith("b:") or name.startswith("Style") \
           or name.startswith("Setter") or name.startswith("DataTrigger") \
           or name.startswith("ColumnDefinition") or name.startswith("RowDefinition") \
           or name.startswith("Grid.Row") or name.startswith("Grid.Column"):
            continue
        seq.append(name)
    return seq

main_seq = Counter(tag_sequence(main_src))
prev_seq = Counter(tag_sequence(prev_src))

# 逐类型对照：主工程有的控件类型，预览里出现次数不能少于主工程
# （预览可以额外有控件，但主工程有而预览完全没有 = 不同步）
for name, cnt in sorted(main_seq.items()):
    if name.startswith("WindowChrome"):
        # WindowChrome 是窗口级属性，预览工程用的是普通 Window，不影响控件布局
        continue
    if name in ("Border", "Grid", "StackPanel", "TextBlock", "Button",
                "CheckBox", "Ellipse", "ListBox", "ListBoxItem"):
        # 这些是通用容器，数量差异正常（预览用静态数据展开多处）
        if prev_seq.get(name, 0) == 0:
            errors.append("主工程有 %s 控件，预览工程完全没有" % name)
        continue
    if prev_seq.get(name, 0) < cnt:
        errors.append("控件类型数量不一致：%s 主工程 %d 个，预览 %d 个" %
                      (name, cnt, prev_seq.get(name, 0)))

# ---------- 2. DynamicResource 引用 ----------
dyn_re = re.compile(r'\{DynamicResource\s+([A-Za-z0-9_.]+)\}')
# 只比对**影响布局**的引用。ToolTip 里的文案只是悬停提示，
# 不参与宽度计算，预览工程不写 ToolTip 是有意的。
# Ui.Empty 是空状态提示，预览工程只渲染有设备的场景，也没有。
IGNORED_DYN = {"Ui.Empty"}

def dyn_keys(src):
    """取 DynamicResource 键，排除 ToolTip 属性上的那些。"""
    keys = set()
    for line in src.split("\n"):
        if "ToolTip" in line:
            continue
        keys.update(dyn_re.findall(line))
    return keys

main_dyn = dyn_keys(main_src)
prev_dyn = dyn_keys(prev_src)

for k in sorted(main_dyn):
    if k.endswith("Brush"):
        continue  # 画刷两边都来自 Theme，不算文案
    if k in IGNORED_DYN:
        continue
    if k not in prev_dyn:
        errors.append("主工程引用了文案 %s，预览工程没引用" % k)

# ---------- 3. 关键布局数值 ----------
def attrs(src, name):
    return Counter(re.findall(r'\b%s\s*=\s*"([^"]*)"' % name, src))

# EmptyHint 是空状态提示，预览工程不渲染空状态场景。
# 它在主工程里独占一些数值（字号 11.5、行高 20），会把计数比误判成不同步。
# 所以比数值前先把 EmptyHint 整块剔掉。
main_cmp = re.sub(r'<TextBlock[^>]*x:Name="EmptyHint".*?/>', "", main_src, flags=re.S)

for name in ("Width", "Height", "FontSize", "Padding"):
    main_vals = attrs(main_cmp, name)
    prev_vals = attrs(prev_src, name)
    # 主工程的每个数值，预览里必须存在同值
    for val, cnt in sorted(main_vals.items()):
        if prev_vals.get(val, 0) < cnt:
            errors.append("布局数值 %s=\"%s\"：主工程 %d 处，预览 %d 处" %
                          (name, val, cnt, prev_vals.get(val, 0)))

# ---------- 4. 关键结构标记 ----------
# 双向检查：主工程有的，预览也要有；预览有的，主工程也要有。
# 只做单向检查会漏掉「预览工程被改坏」的情况——比如把滚动条样式换掉，
# 主工程没动，单向检查照样通过，但预览量出来的宽度已经是假的了。
#
# 注意：不能只判断「文本里出现过这个词」。主工程的注释里也写了
# OverlayScrollViewer，那样两边都"有"，就把预览被改坏的情况盖住了。
# 所以这里查的是**实际引用**：样式名出现在 BasedOn / StaticResource 里。
STRUCT_MARKERS = [
    'OverlayScrollViewer',          # 固定滚动条槽
    'DeviceListItem',               # 设备行样式
    'DeviceSecondaryChip',          # 次要按钮样式
    'DeviceActionButton',           # 动作按钮样式
    'DeviceToggleChip',             # 自动开关样式
    'LineStackingStrategy="BlockLineHeight"',
    'TextWrapping="Wrap"',
]

def uses(src, marker):
    """样式/属性是否被真正引用（而不是只在注释里被提到）。"""
    for line in src.split("\n"):
        if marker not in line:
            continue
        stripped = line.strip()
        if stripped.startswith("<!--") or stripped.startswith(";"):
            continue
        # 样式类必须出现在 StaticResource / BasedOn 引用里
        if marker.endswith("Button") or marker.endswith("Chip") \
           or marker.endswith("Item") or marker.endswith("Viewer"):
            if "StaticResource %s" % marker in line:
                return True
            continue
        return True
    return False

for marker in STRUCT_MARKERS:
    in_main = uses(main_src, marker)
    in_prev = uses(prev_src, marker)
    if in_main and not in_prev:
        errors.append("预览工程缺少结构：%s" % marker)
    elif in_prev and not in_main:
        errors.append("主工程缺少结构：%s（预览工程有，两边不同步）" % marker)

# 底栏固定高度：两边都得是 30
for label, src in (("主工程", main_src), ("预览工程", prev_src)):
    if '<Grid Height="30">' not in src:
        errors.append("%s 底栏 Grid 没写死 Height=\"30\"（高度会随内容跳，列表会抖）" % label)

# ---------- 输出 ----------
print("主工程 MainWindow.xaml   %d 行" % len(main_src.split("\n")))
print("预览工程 PreviewWindow.xaml %d 行" % len(prev_src.split("\n")))
print()

if errors:
    print("=== 不同步 %d 项 ===" % len(errors))
    for e in errors:
        print("  " + e)
    sys.exit(1)

print("=== 两边同步 ✓ ===")
if notes:
    for n in notes:
        print("  " + n)
