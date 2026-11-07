"""把刚打出来的预览版包改成 2.2 专用包。

CreateCipx 只会压 bin 输出目录，manifest 只能用项目里那份（apiVersion 2.1.0.1）。
预览版必须声明 apiVersion 2.1.1.1，否则正式版用户会误装 net10 的包然后加载失败。

用法：
    dotnet build -c Release -p:PreviewBuild=true -p:CreateCipx=true -p:PowershellBinaryName=powershell
    python pack-preview.py
"""

import io
import os
import sys
import zipfile

root = os.path.dirname(os.path.abspath(__file__))
cipx_dir = os.path.join(root, "cipx")
src = os.path.join(cipx_dir, "ClassIsland.SleepWatch.cipx")
dst = os.path.join(cipx_dir, "ClassIsland.SleepWatch_for_version_1.5.0.0-2.2.cipx")
manifest_path = os.path.join(root, "manifest.preview.yml")

if not os.path.exists(src):
    sys.exit("找不到 %s，先用 -p:PreviewBuild=true -p:CreateCipx=true 构建" % src)

manifest = io.open(manifest_path, encoding="utf-8").read()

with zipfile.ZipFile(src) as zin:
    entries = [(i.filename, zin.read(i.filename)) for i in zin.infolist()]

with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zout:
    replaced = False
    for name, data in entries:
        if name.lower() == "manifest.yml":
            data = manifest.encode("utf-8")
            replaced = True
        zout.writestr(name, data)

if not replaced:
    sys.exit("包里没有 manifest.yml，别用这个脚本")

print("已生成 %s（%.1f MB）" % (dst, os.path.getsize(dst) / 1048576))
