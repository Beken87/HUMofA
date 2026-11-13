# -*- coding: utf-8 -*-
"""
comment_reels_v3.py — Part'li yorumlu Reels motoru.

Kurallar (kullanici istegi):
- Cikti videolari EN FAZLA 20 saniye
- Her 4 saniyede bir yorum (0.5, 4.5, 8.5, ...)
- 20 sn'den uzun kaynaklar esit uzunlukta part'lara bolunur (part1, part2, ...)
- Yorum basina uyarlanabilir TTS hizi (slota sigmasin diye, max +%110)
- Her yorum penceresi piksel dogrulamasi
Yorum havuzu: all_comments.json (canli tarayici kazimasi) + v4 icin mevcut txt.
"""
import asyncio
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

from PIL import Image

SCRIPTS_DIR = Path(r"C:\Users\beken\Desktop\Files\BofA\scripts")
VIDEO_DIR = Path(r"C:\Users\beken\Desktop\Files\SSS_Yorumlu_Video")
OUT_DIR = VIDEO_DIR / "Editlenmişler"
LOCAL_DIR = Path(r"C:\Users\beken\AppData\Local\hermes\cache\scratch\sss_local")
LOCAL_OUT = LOCAL_DIR / "outputs_v3"
REPORT = VIDEO_DIR / "videoeditor_report.json"
ALL_COMMENTS = Path(r"C:\Users\beken\AppData\Local\Temp\diag\all_comments.json")
DIAG = Path(r"C:\Users\beken\AppData\Local\Temp\diag")

spec = importlib.util.spec_from_file_location("v2", str(SCRIPTS_DIR / "comment_reels_v2.py"))
v2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v2)

MAX_PART = 20.0
CADENCE = 4.0
FIRST_SLOT = 0.5
TTS_TARGET = 3.4
TTS_RATE_CAP = 110

HUMOR_KEYWORDS = ["ahah", "haha", "koptum", "öldüm", "oldum", "gümledim", "patladım",
                  "yarıldım", "😂", "🤣", "😭", "💀", "efsane", "mükemmel", "delirdim",
                  "güldüm", "komik", "olayı", "gol", "maç"]


# ----------------------------------------------------------------------------
# Yorum havuzu
# ----------------------------------------------------------------------------
def clean_text(t):
    t = re.sub(r"\s*Beğen\s*$", "", t.strip())
    t = re.sub(r"\s+", " ", t)
    return t.strip()


def usable(c):
    t = clean_text(c.get("text") or "")
    if len(t) < 4:
        return False
    stripped = v2.EMOJI_RE.sub("", t)
    # en az 3 harf icer ve harf olmayan karakter bollugu yok
    letters = sum(1 for ch in stripped if ch.isalpha())
    return letters >= 3


def score(c):
    t = clean_text(c["text"]).lower()
    s = 0.0
    try:
        s += float(c.get("likes") or 0) * 3
    except Exception:
        pass
    for kw in HUMOR_KEYWORDS:
        if kw in t:
            s += 2.0
    if 8 <= len(t) <= 110:
        s += 1.5
    return s


def build_pool():
    pools = {}
    data = json.loads(ALL_COMMENTS.read_text(encoding="utf-8"))
    for vid, blob in data.items():
        cs = [dict(c, text=clean_text(c["text"])) for c in blob["comments"]]
        cs = [c for c in cs if usable(c)]
        cs.sort(key=lambda c: -score(c))
        # ayni kullanici-komment tekrarlarini at
        seen, uniq = set(), []
        for c in cs:
            k = (c["user"], c["text"])
            if k not in seen:
                seen.add(k)
                uniq.append(c)
        pools[int(vid)] = uniq
    # v4: mevcut tek yorum
    txt4 = VIDEO_DIR / "reel_sahenesirasisende_4_comments.txt"
    if txt4.exists():
        pools[4] = v2.parse_comments(txt4, v2.load_likes())
    return pools


# ----------------------------------------------------------------------------
# Part plani
# ----------------------------------------------------------------------------
def plan_parts(dur, n_comments):
    n_parts = max(1, min(int(dur // 1), -(-dur // MAX_PART)))  # ceil
    n_parts = int(-(-dur // MAX_PART)) if dur > MAX_PART else 1
    part_dur = dur / n_parts
    slots = []
    t = FIRST_SLOT
    while t + 3.0 <= part_dur + 0.05:
        slots.append(round(t, 2))
        t += CADENCE
    # yorumlari part'lara esit dagit
    per_part = [0] * n_parts
    for i in range(min(n_comments, n_parts * len(slots))):
        per_part[i % n_parts] += 1
    # once dolu part'lar: az yorumlu part'i sona birak (round-robin yerine: esit)
    return part_dur, slots, per_part


# ----------------------------------------------------------------------------
# TTS: yorumu slota sigdir
# ----------------------------------------------------------------------------
async def tts_fit(text, out_mp3, voice, target):
    await v2.tts_speak(text, out_mp3, voice)
    d = v2.probe_audio(out_mp3)
    if d <= target + 0.05:
        return d
    rate = min(TTS_RATE_CAP, max(0, int((d / target - 1) * 100)))
    if rate > 4:
        await v2.tts_speak(text, out_mp3, voice, rate=f"+{rate}%")
        d2 = v2.probe_audio(out_mp3)
        if d2 < d:
            return d2
    return d


# ----------------------------------------------------------------------------
# Part render
# ----------------------------------------------------------------------------
async def render_part(src, out_path, comments, voice, ss, part_dur, quiet=False):
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.parent / f"_tmp_p_{out_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)

    audios, cards, windows = [], [], []
    slot_i = 0
    for c in comments:
        slot = FIRST_SLOT + CADENCE * slot_i
        if slot + 3.0 > part_dur + 0.05:
            break
        target = min(TTS_TARGET, part_dur - slot - 0.25)
        card_png = tmp / f"card_{slot_i}.png"
        voice_mp3 = tmp / f"voice_{slot_i}.mp3"
        v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", card_png,
                               target_width=720)  # gecici; asagida gercek W ile yeniden
        audios.append(voice_mp3)
        cards.append(card_png)
        windows.append((slot, c))
        slot_i += 1

    # gercek cozunurluk ile kartlari yeniden uret
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height", "-of", "json", str(src)],
        capture_output=True, text=True, check=True)
    W = int(json.loads(probe.stdout)["streams"][0]["width"])
    H = int(json.loads(probe.stdout)["streams"][0]["height"])
    for i, (slot, c) in enumerate(windows):
        v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", cards[i], target_width=W)

    # TTS (uyarlanabilir hiz) ve pencereler
    final_windows = []
    for i, (slot, c) in enumerate(windows):
        target = min(TTS_TARGET, part_dur - slot - 0.25)
        adur = await tts_fit(c["text"], audios[i], voice, target)
        et = min(slot + adur + 0.35, part_dur - 0.05)
        final_windows.append((slot, et, adur))
        if not quiet:
            print(f"    slot {slot:.1f}s [{c['user']}] ses {adur:.1f}s -> {slot:.1f}-{et:.1f}s")

    if not final_windows:
        raise RuntimeError("part'a yorum sigmadi")

    cmd = ["ffmpeg", "-y", "-v", "error",
           "-ss", f"{ss:.3f}", "-t", f"{part_dur:.3f}", "-i", str(src)]
    for (st, et, _), cp in zip(final_windows, cards):
        cmd += ["-loop", "1", "-t", f"{et - st + 0.3:.2f}", "-i", str(cp)]
    for ap in audios[:len(final_windows)]:
        cmd += ["-i", str(ap)]

    n = len(final_windows)
    fc = []
    for i in range(n):
        fc.append(f"[{1+i}:v]setsar=1[c{i}]")
    last_v = "0:v"
    for i, (st, et, _) in enumerate(final_windows):
        nxt = f"v{i+1}"
        fc.append(f"[{last_v}][c{i}]overlay=(W-w)/2:main_h*0.56-h/2:"
                  f"enable='between(t,{st:.2f},{et:.2f})'[{nxt}]")
        last_v = nxt

    if v2.has_audio(src):
        fc.append("[0:a]volume=0.15[bg]")
        mix = ["[bg]"]
    else:
        mix = []
    for i, (st, _, _) in enumerate(final_windows):
        idx = 1 + n + i
        dms = int(st * 1000)
        fc.append(f"[{idx}:a]adelay={dms}|{dms},volume=1.35[a{i}]")
        mix.append(f"[a{i}]")
    fc.append(f"{''.join(mix)}amix=inputs={len(mix)}:duration=longest[fa]")

    cmd += ["-filter_complex", ";".join(fc),
            "-map", f"[{last_v}]", "-map", "[fa]",
            "-t", f"{part_dur:.2f}",
            "-c:v", "libx264", "-preset", "fast", "-crf", "21",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart", str(out_path)]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError("ffmpeg: " + res.stderr[-500:])

    # piksel dogrulama
    checks = []
    for i, (st, et, _) in enumerate(final_windows):
        mid = (st + et) / 2
        c = windows[i][1]
        fp = tmp / f"chk_{i}.png"
        subprocess.run(["ffmpeg", "-y", "-v", "error", "-ss", f"{mid:.2f}", "-i",
                        str(out_path), "-frames:v", "1", str(fp)], check=True)
        # beklenen kart
        exp = tmp / f"exp_{i}.png"
        pw, ph = v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", exp, target_width=W)
        s = max(W, 480) / 720.0
        m = int(26 * s)
        fx = (W - pw) // 2
        fy = int(H * 0.56) - ph // 2
        im = Image.open(exp).convert("RGBA")
        epx = im.load()
        exp_light = sum(1 for y in range(m, m + ph - 2 * m) for x in range(m, m + pw - 2 * m)
                        if sum(epx[x, y][:3]) / 3 > 170 and epx[x, y][3] > 100)
        frm = Image.open(fp).convert("RGB")
        fpx = frm.load()
        bx0, by0 = fx + m, fy + m
        frame_light = sum(1 for y in range(max(by0, 0), min(by0 + ph - 2 * m, H))
                          for x in range(max(bx0, 0), min(bx0 + pw - 2 * m, W))
                          if sum(fpx[x, y]) / 3 > 170)
        ratio = frame_light / max(exp_light, 1)
        ok = ratio >= 0.5
        checks.append({"comment": i + 1, "t": round(mid, 1), "ratio": round(ratio, 2), "ok": ok})
        if not quiet:
            print(f"    dogrulama slot{st:.0f}s @{mid:.1f}s: oran={ratio:.2f} -> {'OK' if ok else 'HATA'}")
    if not all(c_["ok"] for c_ in checks):
        raise RuntimeError(f"dogrulama hatasi: {checks}")

    for f in tmp.glob("*"):
        try:
            f.unlink()
        except Exception:
            pass
    try:
        tmp.rmdir()
    except Exception:
        pass
    return checks


# ----------------------------------------------------------------------------
# Ana akis
# ----------------------------------------------------------------------------
def copy_verified(src, dst):
    for _ in range(10):
        try:
            shutil.copy2(src, dst)
            if os.path.getsize(dst) == os.path.getsize(src):
                import hashlib
                h1 = hashlib.sha256(Path(src).read_bytes()).hexdigest()
                h2 = hashlib.sha256(Path(dst).read_bytes()).hexdigest()
                if h1 == h2:
                    return
        except Exception:
            pass
        time.sleep(2)
    raise RuntimeError(f"kopyalama dogrulanamadi: {dst}")


async def main():
    pools = build_pool()
    results, failures = [], []
    LOCAL_OUT.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    for vid in (1, 2, 3, 5):
        src_candidates = [LOCAL_DIR / f"vid{vid}.mp4", LOCAL_DIR / f"reel_sahenesirasisende_{vid}.mp4",
                          VIDEO_DIR / f"reel_sahenesirasisende_{vid}.mp4"]
        src = next((c for c in src_candidates if c.exists() and c.stat().st_size > 100_000), None)
        if not src:
            failures.append({"video": vid, "error": "kaynak yok"})
            continue
        W, H, dur = v2.probe_video(src)
        pool = pools.get(vid, [])
        part_dur, slots, per_part = plan_parts(dur, len(pool))
        n_parts = len(per_part)
        voice = "tr-TR-AhmetNeural" if vid % 2 == 0 else "tr-TR-EmelNeural"
        print(f"\n### Video {vid}: {dur:.1f}s -> {n_parts} part x {part_dur:.1f}s | havuz {len(pool)} yorum | slotlar {slots}")

        idx = 0
        for p in range(1, n_parts + 1):
            cnt = per_part[p - 1]
            part_comments = pool[idx: idx + cnt]
            idx += cnt
            if not part_comments:
                continue
            ss = (p - 1) * part_dur
            name = f"yorumlu_reel_sahenesirasisende_{vid}_part{p}.mp4"
            local_out = LOCAL_OUT / name
            final_out = OUT_DIR / name
            print(f"  -- part{p} ({ss:.1f}s+{part_dur:.1f}s): {len(part_comments)} yorum")
            try:
                checks = await render_part(src, local_out, part_comments, voice, ss, part_dur)
                copy_verified(local_out, final_out)
                d = v2.probe_video(final_out)[2]
                results.append({
                    "video": vid, "part": p, "input": str(src), "output": str(final_out),
                    "size_mb": round(os.path.getsize(final_out) / 1024 / 1024, 2),
                    "duration_s": round(d, 2), "comments": len(part_comments),
                    "comment_detail": [{"user": c["user"], "text": c["text"][:60]} for c in part_comments],
                    "voice": voice, "checks": checks, "verified": True})
                print(f"  ✔ {final_out.name} ({results[-1]['size_mb']} MB, {d:.1f}s)")
            except Exception as e:
                print(f"  !!! part{p} HATA: {e}")
                failures.append({"video": vid, "part": p, "error": str(e)})

    # eski tek-dosya ciktilari kaldir (part'lar yerine gecti; v4 haric)
    for vid in (1, 2, 3, 5):
        old = OUT_DIR / f"yorumlu_reel_sahenesirasisende_{vid}.mp4"
        if old.exists():
            old.unlink()
            print(f"eski cikti silindi: {old.name}")

    status = "success" if not failures and results else ("partial" if results else "failed")
    report = {"status": status, "mode": "v3-parts", "max_part_s": MAX_PART, "cadence_s": CADENCE,
              "rendered_parts": len(results), "outputs": results, "failures": failures,
              "notes": "Videolar max 20s esit part'lara bolundu; her 4 sn'de bir yorum; "
                       "yorum basina uyarlanabilir TTS hizi (max +110%); her slot piksel "
                       "dogrulamasi. Yorum havuzu canli tarayici kazimasi (all_comments.json). "
                       "Video 4 tek yorum icerdigi ve ciktisi zaten <20s oldugu icin part'lanmadi."}
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n==== RAPOR: {REPORT}")
    print(json.dumps({"status": status, "parts": len(results), "failures": failures}, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())
