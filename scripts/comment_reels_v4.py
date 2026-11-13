# -*- coding: utf-8 -*-
"""
comment_reels_v4.py — Freeze-frame yorumlu Reels motoru (kullanici talimatlari).

KURALLAR (emir):
- Her 3-4 saniyede bir yorum (yorumlar arasi video kisa oynar, sonra donar)
- Yorum okunurken VIDEO DURUR (son kare clone/tpad ile donuk)
- Seslendirme NORMAL HIZDA (rate yok)
- Sesler ASLA ust uste binmez: TTS sadece freeze sirasinda calar, kaynak sesi
  sadece motion sirasinda calar
- Cikti part'lari en fazla 20 saniye; part2, part3... diye devam
- Yorum havuzu: kendi reel yorumlari + ayni profilin diger reel'lerinden
  canli kazinmis gercek yorumlar (extra_comments.json)
- Her yorum penceresi piksel dogrulamasi
"""
import asyncio
import hashlib
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
LOCAL_OUT = LOCAL_DIR / "outputs_v4"
REPORT = VIDEO_DIR / "videoeditor_report.json"
DIAG = Path(r"C:\Users\beken\AppData\Local\Temp\diag")
OWN_COMMENTS = DIAG / "all_comments.json"
EXTRA_COMMENTS = DIAG / "extra_comments.json"

spec = importlib.util.spec_from_file_location("v2", str(SCRIPTS_DIR / "comment_reels_v2.py"))
v2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v2)

MAX_PART = 19.8          # part ust siniri (kullanici: max 20s)
MOTION_FIRST = 0.45      # part basindaki kisa oynama
MOTION = 0.5             # yorumlar arasi oynama
FREEZE_PAD = 0.25        # yorum bitince donuk kalinan ek sure
MAX_TTS_DUR = 3.9        # daha uzun okunan yorumlar atilir (3-4 sn kadans icin)
TARGET_POOL = 16         # video basina hedef yorum sayisi
MAX_CHARS = 62           # uzun yorumlar kadensi bozar -> ele

HUMOR = ["ahah", "haha", "koptum", "öldüm", "güldüm", "komik", "😂", "🤣", "😭",
         "💀", "efsane", "mükemmel", "delirdim", "olayı", "abi", "ya"]


# ----------------------------------------------------------------------------
# Havuz
# ----------------------------------------------------------------------------
def clean_text(t):
    t = re.sub(r"\s*Beğen\s*$", "", (t or "").strip())
    t = re.sub(r"\s+", " ", t)
    return t.strip()


def usable(c, max_chars=MAX_CHARS):
    t = clean_text(c.get("text"))
    if not (4 <= len(t) <= max_chars):
        return None
    if sum(1 for ch in v2.EMOJI_RE.sub("", t) if ch.isalpha()) < 3:
        return None
    if re.match(r"^[^A-Za-zÇĞİÖŞÜçğıöşü]*$", t):
        return None
    return t


def rank(c):
    t = clean_text(c["text"]).lower()
    s = 0.0
    try:
        s += float(c.get("likes") or 0) * 2
    except Exception:
        pass
    for kw in HUMOR:
        if kw in t:
            s += 1.2
    s -= max(0, len(t) - 45) * 0.05  # kisa yorumlar hafif onluklu
    return s


def build_pools():
    pools = {}
    own = json.loads(OWN_COMMENTS.read_text(encoding="utf-8"))
    for vid, blob in own.items():
        cs = []
        for c in blob["comments"]:
            t = usable(c)
            if t:
                cs.append({"user": c["user"], "text": t, "likes": c.get("likes") or "1"})
        cs.sort(key=lambda c: -rank(c))
        seen, u = set(), []
        for c in cs:
            if (c["user"], c["text"]) not in seen:
                seen.add((c["user"], c["text"]))
                u.append(c)
        pools[int(vid)] = u
    txt4 = VIDEO_DIR / "reel_sahenesirasisende_4_comments.txt"
    if txt4.exists():
        pools[4] = v2.parse_comments(txt4, v2.load_likes())

    extras = []
    for sc, cs in json.loads(EXTRA_COMMENTS.read_text(encoding="utf-8")).items():
        for c in cs:
            t = usable(c)
            if t:
                extras.append({"user": c["user"], "text": t, "likes": c.get("likes") or "1"})
    extras.sort(key=lambda c: -rank(c))
    seen = {(c["user"], c["text"]) for cs in pools.values() for c in cs}
    for vid in (1, 2, 3, 4, 5):
        while len(pools.get(vid, [])) < TARGET_POOL and extras:
            c = extras.pop(0)
            if (c["user"], c["text"]) in seen:
                continue
            seen.add((c["user"], c["text"]))
            pools[vid].append(c)
    return pools


# ----------------------------------------------------------------------------
# Freeze-frame part render
# ----------------------------------------------------------------------------
async def render_freeze_part(src, out_path, comment_pool, voice, src_ss, quiet=False):
    """comment_pool'dan part'a sigan yorumlari alir; freeze-frame montaj yapar.
    Donus: (kullanilan_yorum_sayisi, harcanan_kaynak_sure, checks)."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.parent / f"_tmp_f_{out_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)

    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height:format=duration", "-of", "json", str(src)],
        capture_output=True, text=True, check=True)
    pj = json.loads(probe.stdout)
    W, H = int(pj["streams"][0]["width"]), int(pj["streams"][0]["height"])
    dur = float(pj.get("format", {}).get("duration") or 0)

    has_a = v2.has_audio(src)

    # 1) yorumlari sec: TTS sureleri olcule olcule part'a sigan kadar al
    used, cards, tts_files, tts_durs = [], [], [], []
    skipped = []
    timeline = []  # (motion, freeze_start, freeze_end)
    t_cursor = 0.0
    src_cursor = src_ss
    est = 0.0
    for c in comment_pool:
        motion = MOTION_FIRST if not used else MOTION
        if src_cursor + motion + 0.1 > dur:
            break
        # TTS normal hiz; ses metninden emoji temizle (kartta gorunur, okunmaz)
        speech = v2.EMOJI_RE.sub(" ", c["text"]).strip()
        if not speech or sum(1 for ch in speech if ch.isalpha()) < 2:
            continue
        vf = tmp / f"voice_{len(used)}.mp3"
        await v2.tts_speak(speech, vf, voice)  # rate YOK: normal hiz
        d = v2.probe_audio(vf)
        if d > MAX_TTS_DUR:
            # uzun okunan yorum kadensi bozar -> atla (havuzdan dusulmez, sonraki part'ta da atlanir)
            vf.unlink(missing_ok=True)
            skipped.append(c)
            continue
        seg_end = t_cursor + motion + d + FREEZE_PAD
        if used and seg_end > MAX_PART:
            vf.unlink(missing_ok=True)
            break
        card = tmp / f"card_{len(used)}.png"
        v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", card, target_width=W)
        freeze_start = t_cursor + motion
        timeline.append({"motion": motion, "freeze_start": freeze_start,
                         "freeze_end": freeze_start + d, "tts": d})
        used.append(c)
        cards.append(card)
        tts_files.append(vf)
        tts_durs.append(d)
        t_cursor = seg_end
        src_cursor += motion
        if not quiet:
            print(f"    y{len(used)} [{c['user']}] ses {d:.1f}s | freeze {freeze_start:.1f}-{freeze_start + d:.1f}s | toplam {t_cursor:.1f}s")

    if not used:
        raise RuntimeError("part'a yorum sigmadi")
    part_dur = t_cursor + 0.15
    spent_src = src_cursor - src_ss

    # 2) ffmpeg: segment segment trim + tpad(clone) + concat
    cmd = ["ffmpeg", "-y", "-v", "error", "-i", str(src)]
    for tl, cp in zip(timeline, cards):
        w = tl["freeze_end"] - tl["freeze_start"] + 0.25
        cmd += ["-loop", "1", "-t", f"{w:.2f}", "-i", str(cp)]
    for vf in tts_files:
        cmd += ["-i", str(vf)]

    n = len(used)
    fc = []
    # video+audio segmentleri
    seg_v, seg_a = [], []
    s_off = src_ss
    for i, tl in enumerate(timeline):
        m = tl["motion"]
        seg_len = m + tl["tts"] + FREEZE_PAD
        fc.append(f"[0:v]trim=start={s_off:.3f}:end={s_off + m:.3f},setpts=PTS-STARTPTS,"
                  f"tpad=stop_mode=clone:stop_duration={tl['tts'] + FREEZE_PAD:.3f}[sv{i}]")
        if has_a:
            fc.append(f"[0:a]atrim=start={s_off:.3f}:end={s_off + m:.3f},asetpts=PTS-STARTPTS,"
                      f"apad=whole_dur={seg_len:.3f},volume=0.5[sa{i}]")
        else:
            fc.append(f"anullsrc=r=44100:cl=stereo,atrim=0:{seg_len:.3f}[sa{i}]")
        seg_v.append(f"[sv{i}]")
        seg_a.append(f"[sa{i}]")
        s_off += m
    fc.append("".join(v + a for v, a in zip(seg_v, seg_a)) + f"concat=n={n}:v=1:a=1[vc][ac]")

    # kart overlay'leri (concat zaman cizgisinde)
    for i, tl in enumerate(timeline):
        fc.append(f"[{1 + i}:v]setsar=1[pc{i}]")
    last_v = "vc"
    for i, tl in enumerate(timeline):
        st, et = tl["freeze_start"], tl["freeze_end"] + 0.2
        nxt = f"ov{i}"
        fc.append(f"[{last_v}][pc{i}]overlay=(W-w)/2:main_h*0.56-h/2:"
                  f"enable='between(t,{st:.2f},{et:.2f})'[{nxt}]")
        last_v = nxt

    # ses: kaynak (motion kısımları) + TTS (freeze başlangıçlarında) — çakışma yok
    mix = ["[ac]"]
    for i, tl in enumerate(timeline):
        idx = 1 + n + i
        dms = int(tl["freeze_start"] * 1000)
        fc.append(f"[{idx}:a]adelay={dms}|{dms},volume=1.4[ta{i}]")
        mix.append(f"[ta{i}]")
    fc.append(f"{''.join(mix)}amix=inputs={len(mix)}:duration=longest[fa]")

    cmd += ["-filter_complex", ";".join(fc),
            "-map", f"[{last_v}]", "-map", "[fa]",
            "-t", f"{part_dur:.2f}",
            "-c:v", "libx264", "-preset", "fast", "-crf", "21",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart", str(out_path)]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError("ffmpeg: " + res.stderr[-600:])

    # 3) dogrulama: her freeze ortasinda kart metni
    checks = []
    s_scale = max(W, 480) / 720.0
    m_px = int(26 * s_scale)
    for i, tl in enumerate(timeline):
        mid = tl["freeze_start"] + min(tl["tts"] / 2, 1.2)
        c = used[i]
        fp = tmp / f"chk_{i}.png"
        subprocess.run(["ffmpeg", "-y", "-v", "error", "-ss", f"{mid:.2f}", "-i",
                        str(out_path), "-frames:v", "1", str(fp)], check=True)
        exp = tmp / f"exp_{i}.png"
        pw, ph = v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", exp, target_width=W)
        im = Image.open(exp).convert("RGBA")
        epx = im.load()
        exp_light = sum(1 for y in range(m_px, m_px + ph - 2 * m_px) for x in range(m_px, m_px + pw - 2 * m_px)
                        if sum(epx[x, y][:3]) / 3 > 170 and epx[x, y][3] > 100)
        frm = Image.open(fp).convert("RGB")
        fpx = frm.load()
        fx = (W - pw) // 2
        fy = int(H * 0.56) - ph // 2
        bx0, by0 = fx + m_px, fy + m_px
        frame_light = sum(1 for y in range(max(by0, 0), min(by0 + ph - 2 * m_px, H))
                          for x in range(max(bx0, 0), min(bx0 + pw - 2 * m_px, W))
                          if sum(fpx[x, y]) / 3 > 170)
        ratio = frame_light / max(exp_light, 1)
        ok = ratio >= 0.5
        checks.append({"comment": i + 1, "t": round(mid, 1), "ratio": round(ratio, 2), "ok": ok})
        if not quiet:
            print(f"    dogrulama y{i+1} @{mid:.1f}s: oran={ratio:.2f} -> {'OK' if ok else 'HATA'}")
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
    return len(used), spent_src, checks, skipped


# ----------------------------------------------------------------------------
# Ana akis
# ----------------------------------------------------------------------------
def copy_verified(src, dst):
    for _ in range(10):
        try:
            shutil.copy2(src, dst)
            if os.path.getsize(dst) == os.path.getsize(src):
                h1 = hashlib.sha256(Path(src).read_bytes()).hexdigest()
                h2 = hashlib.sha256(Path(dst).read_bytes()).hexdigest()
                if h1 == h2:
                    return
        except Exception:
            pass
        time.sleep(2)
    raise RuntimeError(f"kopyalama dogrulanamadi: {dst}")


async def main():
    pools = build_pools()
    results, failures = [], []
    LOCAL_OUT.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    for vid in (1, 2, 3, 4, 5):
        src_candidates = [LOCAL_DIR / f"vid{vid}.mp4", LOCAL_DIR / f"reel_sahenesirasisende_{vid}.mp4",
                          VIDEO_DIR / f"reel_sahenesirasisende_{vid}.mp4"]
        src = None
        for c in src_candidates:
            try:
                if c.exists() and c.stat().st_size > 100_000:
                    src = c
                    break
            except OSError:
                continue
        if not src:
            failures.append({"video": vid, "error": "kaynak yok"})
            continue
        W, H, dur = v2.probe_video(src)
        pool = pools.get(vid, [])
        voice = "tr-TR-AhmetNeural" if vid % 2 == 0 else "tr-TR-EmelNeural"
        print(f"\n### Video {vid}: kaynak {dur:.1f}s | havuz {len(pool)} yorum | ses {voice}")

        src_ss = 0.0
        p = 0
        while src_ss + MOTION_FIRST + 0.1 < dur and pool:
            p += 1
            name = f"yorumlu_reel_sahenesirasisende_{vid}_part{p}.mp4"
            local_out = LOCAL_OUT / name
            final_out = OUT_DIR / name
            try:
                cnt, spent, checks, skipped = await render_freeze_part(src, local_out, pool, voice, src_ss)
                pool = pool[cnt:]
                if skipped:
                    skip_ids = {id(c) for c in skipped}
                    pool = [c for c in pool if id(c) not in skip_ids]
                src_ss += spent
                copy_verified(local_out, final_out)
                d = v2.probe_video(final_out)[2]
                results.append({
                    "video": vid, "part": p, "output": str(final_out),
                    "size_mb": round(os.path.getsize(final_out) / 1024 / 1024, 2),
                    "duration_s": round(d, 2), "comments": cnt,
                    "comment_detail": [], "voice": voice, "checks": checks, "verified": True})
                print(f"  ✔ {final_out.name} ({results[-1]['size_mb']} MB, {d:.1f}s, {cnt} yorum)")
            except Exception as e:
                print(f"  !!! part{p} HATA: {e}")
                failures.append({"video": vid, "part": p, "error": str(e)})
                break

    status = "success" if not failures and results else ("partial" if results else "failed")
    report = {"status": status, "mode": "v4-freeze",
              "rules": ["max ~20s part", "yorum ~3-4 sn'de bir", "yorum okunurken video donar",
                        "normal TTS hizi", "ses cakismasi yok"],
              "rendered_parts": len(results), "outputs": results, "failures": failures,
              "notes": "Freeze-frame motoru: yorumlar arasi video ~0.5s oynar, yorum okunurken "
                       "son kare donuk kalir; TTS yalnizca freeze sirasinda calar (cakisma yok), "
                       "kaynak sesi yalnizca motion sirasinda (volume 0.5). TTS normal hizda. "
                       "Yorum havuzu: reel'in kendi yorumlari + ayni profilin diger reel'lerinden "
                       "canli kazinmis gercek yorumlar."}
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n==== RAPOR: {REPORT}")
    print(json.dumps({"status": status, "parts": len(results), "failures": failures}, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())
