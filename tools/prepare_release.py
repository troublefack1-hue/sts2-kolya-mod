import json
from pathlib import Path
import shutil
import zipfile

root = Path(__file__).resolve().parent.parent
version = json.loads((root / "src/kolya_mod.json").read_text(encoding="utf-8"))["version"]
dist = root / "dist/kolya_mod"
files = ["kolya_mod.dll", "kolya_mod.json", "option_fuse.png", "option_merge.png", "option_costdown.png", "option_trade.png"]
dist.mkdir(parents=True, exist_ok=True)
for name in files:
    source = root / "src/bin/Release/net9.0" / name if name.endswith(".dll") else root / "src" / name
    shutil.copy2(source, dist / name)
archive = root / "KolyaMod_StS2.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
    for name in files:
        output.write(dist / name, f"kolya_mod/{name}")
content = root / "workshop/KolyaMod/content"
for name in files:
    if (dist / name).exists():
        shutil.copy2(dist / name, content / name)
print(f"Prepared Emberforge {version}: {archive} ({archive.stat().st_size} bytes)")
print(f"Steam content: {content}")
