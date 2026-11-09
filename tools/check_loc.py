#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
核对 BTAudioSink 的文案表一致性。

检查五件事：
  1. 中文表和英文表的键是否一一对应（漏一条 → 英文界面直接显示键名）
  2. 同一条文案两边占位符是否一致（{0} / {1:0} 等）
  3. XAML 里 {DynamicResource 键} 引用的键是否都存在
  4. 代码里 Loc.Get / Loc.Format 引用的键是否都存在
  5. 动态拼接的键（Loc.Get($"Status.{x}")）提示人工确认

用法：python tools/check_loc.py <项目根目录>
退出码 0 = 通过，1 = 有错误。

注意：表格边界不能靠找 "};" 来切——文案内容里可能有 "(State={1});" 这种
带 "};" 的字符串，会把表格提前截断。所以按行号定位每条记录属于哪张表。
"""
import os
import re
import sys

# 控制台编码必须自己定，不能看环境。
# GitHub Actions 的 windows runner 控制台是 cp1252，print 中文会直接抛
# UnicodeEncodeError 把整个构建判失败（而且这个坑只在 CI 上出现，本机是 UTF-8）。
# 所以这里强制 stdout/stderr 用 UTF-8，errors="replace" 兜底——
# 编码再乱的机器上最多显示成乱码，不会崩。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        # 极老的 Python 没有 reconfigure，跳过即可（那时多半也不是 UTF-8 控制台）
        pass

root = sys.argv[1] if len(sys.argv) > 1 else "."

loc_path = os.path.join(root, "Services", "Loc.cs")
with open(loc_path, encoding="utf-8-sig") as f:
    loc_lines = f.read().split("\n")

# ---------- 定位两张表的行号范围 ----------
def find_line(pattern):
    for i, line in enumerate(loc_lines):
        if re.search(pattern, line):
            return i
    raise SystemExit("在 Loc.cs 里找不到：%s" % pattern)

zh_start = find_line(r'Dictionary<string,\s*string>\s+Zh\s*=')
en_start = find_line(r'Dictionary<string,\s*string>\s+En\s*=')

# 两张表都从各自的 start 行到下一个 start 行（或文件尾）
zh_end = en_start
en_end = len(loc_lines)

# ---------- 逐行解析条目 ----------
# 一条记录形如：        ["Key"] = "value",
entry_re = re.compile(r'\["([^"]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"')

def parse_range(start, end):
    out = {}
    order = []
    for i in range(start, end):
        m = entry_re.search(loc_lines[i])
        if not m:
            continue
        key = m.group(1)
        value = m.group(2)
        # 占位符：{0} {1} {1:0} {2:X8}
        ph = set(re.findall(r'\{(\d+)(?::[^}]*)?\}', value))
        if key in out:
            # 同一个键出现两次：后写的覆盖先写的，但要记住
            pass
        out[key] = (value, ph, i + 1)
        if key not in order:
            order.append(key)
    return out, order

zh, zh_order = parse_range(zh_start, zh_end)
en, en_order = parse_range(en_start, en_end)

errors = []
warnings = []

# ---------- 1. 键集合对照 ----------
missing_en = [k for k in zh_order if k not in en]
missing_zh = [k for k in en_order if k not in zh]

if missing_en:
    errors.append("英文表缺少 %d 条：" % len(missing_en))
    for k in missing_en:
        errors.append("    %s  (Loc.cs:%d)" % (k, zh[k][2]))
if missing_zh:
    errors.append("中文表缺少 %d 条（英文表多出来的）：" % len(missing_zh))
    for k in missing_zh:
        errors.append("    %s  (Loc.cs:%d)" % (k, en[k][2]))

if not missing_en and not missing_zh and zh_order != en_order:
    warnings.append("两表键顺序不同（不影响功能，只是可读性）")

# ---------- 2. 占位符对照 ----------
for k in zh_order:
    if k not in en:
        continue
    zph, eph = zh[k][1], en[k][1]
    if zph != eph:
        errors.append("占位符不一致 %s：中文 {%s} vs 英文 {%s}" %
                      (k, ",".join(sorted(zph)), ",".join(sorted(eph))))

# ---------- 收集被引用的键 ----------
SKIP_DIRS = {"bin", "obj", ".git", "publish", "publish-installer", "publish-standalone",
              "_ui_preview", "_tray_test", "installer", "node_modules"}

def walk_files(exts):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if os.path.splitext(fn)[1].lower() in exts:
                yield os.path.join(dirpath, fn)

used_xaml = {}
dyn_re = re.compile(r'\{DynamicResource\s+([A-Za-z0-9_.]+)\}')

# 主题画刷（WindowBgBrush、AccentBrush 等）定义在 Theme/*.xaml 里，
# 不属于文案表。把这些 x:Key 收集起来，DynamicResource 校验时一并认。
theme_keys = set()
x_key_re = re.compile(r'x:Key\s*=\s*"([^"]+)"')
for path in walk_files({".xaml"}):
    if os.sep + "Theme" + os.sep not in path:
        continue
    with open(path, encoding="utf-8-sig") as f:
        content = f.read()
    theme_keys.update(x_key_re.findall(content))

for path in walk_files({".xaml"}):
    with open(path, encoding="utf-8-sig") as f:
        content = f.read()
    for k in dyn_re.findall(content):
        if k in theme_keys:
            continue
        used_xaml.setdefault(k, set()).add(os.path.relpath(path, root))

used_code = {}
loc_ref_re = re.compile(r'Loc\.(?:Get|Format)\(\s*"([^"]+)"')
# 键可能出现在 Loc.Get("Xxx") 里，也可能出现在三元表达式里：
#     Loc.Get(enabled ? "Log.MicaOn" : "Log.MicaOff")
#     Loc.Get(IsConnected ? "Ui.Device.Disconnect" : "Ui.Device.Connect")
# 所以这里不只找紧跟在括号后的字面量，而是抓整个 Loc.Get/Format 调用里
# 所有形如 "Xxx.Yyy" 的字符串字面量。
loc_call_re = re.compile(r'Loc\.(?:Get|Format)\(([^;]*?)\)\s*[;,\)]')
key_like_re = re.compile(r'"([A-Z][A-Za-z0-9_]*\.[A-Za-z0-9_.]+)"')
for path in walk_files({".cs"}):
    # Loc.cs 自己的文档注释里有 Loc.Format("Log.Xxx", 参数) 这种示例，
    # 不是真引用，跳过。
    if os.path.basename(path) == "Loc.cs" and os.path.dirname(path).endswith("Services"):
        continue
    with open(path, encoding="utf-8-sig") as f:
        content = f.read()
    for m in loc_call_re.finditer(content):
        for k in key_like_re.findall(m.group(1)):
            used_code.setdefault(k, set()).add(os.path.relpath(path, root))
    # 直接字面量形式（Loc.Get("Xxx")）也覆盖到
    for k in loc_ref_re.findall(content):
        used_code.setdefault(k, set()).add(os.path.relpath(path, root))

for label, used in (("XAML", used_xaml), ("代码", used_code)):
    for k, files in used.items():
        if k not in zh:
            errors.append("%s 引用了不存在的键：%s（%s）" %
                          (label, k, ", ".join(sorted(files))))
        elif k not in en:
            errors.append("%s 引用了英文表缺失的键：%s（%s）" %
                          (label, k, ", ".join(sorted(files))))

# ---------- 动态拼接的键 ----------
# Loc.Get($"Status.{code}") 这种，静态检查抓不到。
# 这里按前缀展开：把表里所有同前缀的键列出来，确认前缀覆盖是完整的
# （比如 Status.* 必须覆盖代码里 DeviceStatus 枚举的全部取值）。
dyn_ref = []
for path in walk_files({".cs"}):
    with open(path, encoding="utf-8-sig") as f:
        content = f.read()
    for m in re.finditer(r'Loc\.(?:Get|Format)\(\s*\$"([^"]*)"', content):
        dyn_ref.append((m.group(1), os.path.relpath(path, root)))

# 设备状态枚举 → Status.* 文案，必须一一对应
status_enum = os.path.join(root, "Models", "DeviceItem.cs")
if os.path.exists(status_enum):
    with open(status_enum, encoding="utf-8-sig") as f:
        dev_src = f.read()
    m = re.search(r'enum\s+DeviceStatus\s*\{(.*?)\}', dev_src, re.S)
    if m:
        members = re.findall(r'^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|,|$)', m.group(1), re.M)
        members = [x for x in members if x and x != "Unknown"]
        for mem in members:
            key = "Status." + mem
            if key not in zh:
                errors.append("DeviceStatus.%s 没有对应的文案键 %s" % (mem, key))
            elif key not in en:
                errors.append("DeviceStatus.%s 的英文表缺 %s" % (mem, key))
            else:
                print("  状态映射 OK: DeviceStatus.%-20s → %s" % (mem, key))

# 蓝牙 Class 主类别码 0-9 → Class.* 文案
for code in range(10):
    key = "Class.%d" % code
    if key not in zh:
        errors.append("蓝牙类别码 %d 没有对应的文案键 %s" % (code, key))
    elif key not in en:
        errors.append("蓝牙类别码 %d 的英文表缺 %s" % (code, key))

# ---------- 未被静态引用的键 ----------
# 动态拼接出来的键（Status.Connecting、Class.3 等）本来就不会被静态引用到，
# 单独归类，免得和真正没人用的历史遗留混在一起。
dynamic_covered = set()
if os.path.exists(status_enum):
    with open(status_enum, encoding="utf-8-sig") as f:
        dev_src = f.read()
    m = re.search(r'enum\s+DeviceStatus\s*\{(.*?)\}', dev_src, re.S)
    if m:
        for mem in re.findall(r'^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|,|$)', m.group(1), re.M):
            if mem and mem != "Unknown":
                dynamic_covered.add("Status." + mem)
for code in range(10):
    dynamic_covered.add("Class.%d" % code)

all_used = set(used_xaml) | set(used_code)
unused = [k for k in zh_order if k not in all_used and k not in dynamic_covered]
if unused:
    warnings.append("有 %d 条文案没有任何地方引用（可能是历史遗留）：" % len(unused))
    for k in unused:
        warnings.append("    " + k)
else:
    print("  没有孤儿文案（所有键都有引用或由动态拼接覆盖）")

# ---------- 输出 ----------
print("中文表 %d 条 / 英文表 %d 条" % (len(zh), len(en)))
print("XAML 引用 %d 个键 / 代码静态引用 %d 个键" % (len(used_xaml), len(used_code)))
if dyn_ref:
    print("动态拼接的键 %d 处（需人工确认前缀覆盖完整）：" % len(dyn_ref))
    for pat, path in dyn_ref:
        print("    %s  (%s)" % (pat, path))
print()

if warnings:
    print("=== 提示 ===")
    for w in warnings:
        print("  " + w)
    print()

if errors:
    print("=== 错误 %d 项 ===" % len(errors))
    for e in errors:
        print("  " + e)
    sys.exit(1)

print("=== 全部通过 ===")
