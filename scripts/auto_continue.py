# -*- coding: utf-8 -*-
"""Otomatik devam: Instagram kilidini test et; kalktiysa yorumlari cek + videolari uret."""
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PY = r"C:\Program Files\Python314\python.exe"
DIAG = Path(r"C:\Users\beken\AppData\Local\Temp\diag")
BRAVE_CDP = DIAG / "brave_cdp.py"
V5 = Path(r"C:\Users\beken\Desktop\Files\BofA\scripts\comment_reels_v5.py")
STATUS = Path(r"C:\Users\beken\Desktop\Files\SSS_Yorumlu_Video\OTO_DURUM.txt")
FULL = DIAG / "full_comments.json"
LOG = DIAG / "auto_continue.log"


def log(msg):
    line = f"[{datetime.now():%Y-%m-%d %H:%M:%S}] {msg}"
    print(line)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def run(cmd, timeout=1800):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                          encoding="utf-8", errors="replace")


def main():
    log("kilit testi basliyor")
    r = run([PY, str(BRAVE_CDP), "test"], timeout=300)
    out = (r.stdout or "") + (r.stderr or "")
    log("test ciktisi: " + " | ".join(out.strip().splitlines()[-3:]))
    if "CALISIYOR" not in out:
        STATUS.write_text(f"{datetime.now():%Y-%m-%d %H:%M} — kilit hala aktif, sonraki deneme 2 saat sonra\n",
                          encoding="utf-8")
        log("kilit hala aktif — cikiliyor")
        return 0

    log("KILIT KALKTI! yorumlar cekiliyor...")
    r = run([PY, str(BRAVE_CDP), "pull"], timeout=1800)
    out = (r.stdout or "") + (r.stderr or "")
    log("pull ciktisi: " + " | ".join(out.strip().splitlines()[-6:]))
    if not FULL.exists():
        STATUS.write_text(f"{datetime.now():%Y-%m-%d %H:%M} — pull basarisiz\n", encoding="utf-8")
        log("pull basarisiz")
        return 1

    log("v5 render basliyor (5 video)...")
    import os
    env = dict(os.environ, IG_POOL_FILE=str(FULL))
    r = subprocess.run([PY, str(V5)], capture_output=True, text=True, timeout=3600,
                       encoding="utf-8", errors="replace", env=env,
                       cwd=r"C:\Users\beken\Desktop\Files\BofA")
    out = (r.stdout or "") + (r.stderr or "")
    log("v5 ciktisi (son satirlar): " + " | ".join(out.strip().splitlines()[-8:]))
    ok = '"status": "success"' in out or '"status":"success"' in out
    STATUS.write_text(
        f"{datetime.now():%Y-%m-%d %H:%M} — kilitt kalkti, pull + render tamamlandi "
        f"(basari={ok}). Videolar Editlenmisler klasorunde.\n", encoding="utf-8")
    log(f"TAMAMLANDI (basari={ok})")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        log(f"HATA: {e}")
        STATUS.write_text(f"{datetime.now():%Y-%m-%d %H:%M} — hata: {e}\n", encoding="utf-8")
        sys.exit(1)
