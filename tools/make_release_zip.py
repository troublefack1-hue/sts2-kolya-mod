from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

root = Path(__file__).resolve().parent.parent
with ZipFile(root / "KolyaMod_StS2.zip", "w", ZIP_DEFLATED) as archive:
    for path in sorted((root / "dist" / "kolya_mod").iterdir()):
        if path.is_file():
            archive.write(path, Path("kolya_mod") / path.name)
