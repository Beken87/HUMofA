# -*- coding: utf-8 -*-
"""
comment_reels_v6.py — VIRAL FORMAT (kullanici talimatlari 2026-11-10):

1. Kaynak video HAM halde ~20 sn'lik part'lara kesilir (editlenmemis goruntu)
2. Her part YORUMLA BASLAR: part acilis karesi donuk, ilk yorum okunur
3. Yorum okunurken video DONAR; yorumun son harfi okunur okunmaz video
   kaldigi kareden akmaya devam eder (fazla bekleme yok)
4. Oynatma suresiyle her ~4.5 sn'de bir sonraki yorum (4-5 sn bandi)
5. Son yorumdan sonra part'in kalan goruntusu akarak biter (part sonda video akar)
6. CEVAP karti: yorum kartiyla AYNI ANDA gorunur, yorumla birlikte kaybolur
7. Tekrarsiz (video geneli), sadece videonun kendi yorumlari, komiklik siralamasi
8. TTS normal hiz, ses metninde emoji yok; sesler asla ust uste binmez
9. Her yorum penceresi + part acilisi + CEVAP karti piksel dogrulamasi

Kullanim:
  python comment_reels_v6.py          # 5 videoyu da uretir
  python comment_reels_v6.py 5        # sadece video 5
"""
import asyncio
import hashlib
import importlib.util
import json
import os
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
OUT_DIR = Path(os.environ.get(
    "IG_OUT_DIR", str(VIDEO_DIR / "Editlenmişler")))
SRC_DIR = Path(os.environ.get("IG_SRC_DIR", str(VIDEO_DIR)))
LOCAL_OUT = Path(r"C:\Users\beken\AppData\Local\hermes\cache\scratch\sss_local") / "outputs_v6"
REPORT = VIDEO_DIR / "videoeditor_report.json"
POOL_FILE = Path(os.environ.get(
    "IG_POOL_FILE", r"C:\Users\beken\AppData\Local\Temp\diag\full_comments.json"))

spec = importlib.util.spec_from_file_location("v2", str(SCRIPTS_DIR / "comment_reels_v2.py"))
v2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v2)
spec5 = importlib.util.spec_from_file_location("v5", str(SCRIPTS_DIR / "comment_reels_v5.py"))
v5 = importlib.util.module_from_spec(spec5)
spec5.loader.exec_module(v5)

SEG_LEN = 20.0        # ham kesim uzunlugu (kullanicinin "20 saniye civari" talimati)
MERGE_MIN = 3.0       # son segment bu kisaaysa onceki segmente birlesir
CADENCE = 4.5         # oynatma bazli yorum araligi (4-5 sn bandinin ortasi)
MIN_TAIL = 1.2        # son yorumdan sonra akmasi gereken min goruntu
TAIL_PAD = 0.05       # yorum bitisi -> devam gecikmesi (~0: aninda)
MAX_TTS = 4.6         # okuma ust siniri (kadansa uygun)
TXT_TEMPLATE = "reel_sahenesirasisende_{vid}_comments.txt"


# ----------------------------------------------------------------------------
# Planlama
# ----------------------------------------------------------------------------
def plan_segments(dur):
    """Kaynak videoyu ~20 sn'lik HAM segmentlere bol."""
    segs = []
    t = 0.0
    while t < dur - 0.05:
        segs.append([t, min(SEG_LEN, dur - t)])
        t += SEG_LEN
    if len(segs) > 1 and segs[-1][1] < MERGE_MIN:
        segs[-2][1] += segs[-1][1]
        segs.pop()
    return segs


def plan_positions_local(seg_len, n_comments=None):
    """Part ici yorum pozisyonlari (segment locak oynatma zamani).
    Ilk yorum HER ZAMAN 0'da (part yorumla baslar).
    n_comments=None -> 4.5 sn kadansla kac yorum sigiyorsa o pozisyonlar.
    n_comments verildiyse -> o sayiyi 4-5 sn bandinda (havuz yetmezse esit) yayar."""
    limit = seg_len - MIN_TAIL
    if limit <= 0:
        return [0.0]
    if n_comments is None:
        pos = [0.0]
        p = 0.0
        while True:
            p += CADENCE
            if p > limit:
                break
            pos.append(round(p, 2))
        return pos
    if n_comments <= 1:
        return [0.0]
    step = max(4.0, limit / (n_comments - 1))
    pos = [0.0]
    p = 0.0
    while len(pos) < n_comments:
        p += step
        if p > limit + 0.01:
            break
        pos.append(round(p, 2))
    return pos


# ----------------------------------------------------------------------------
# Part render
# ----------------------------------------------------------------------------
async def render_part_v6(src, out_path, seg_start, seg_len, comments,
                         tts_files, tts_durs, positions, W, H, quiet=False):
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.parent / f"_tmp6_{out_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)
    has_a = v2.has_audio(src)

    # --- bloklar: her yorum = [oynama + donma], sonda [kalan oynama] ---
    blocks = []
    prev_p = 0.0
    for i, (c, d) in enumerate(zip(comments, tts_durs)):
        p = positions[i]
        play = max(0.05, p - prev_p) if i > 0 else 0.05
        blocks.append({"kind": "comment", "i": i, "src": seg_start + prev_p,
                       "play": play, "tts": d})
        prev_p = p
    rest = seg_len - prev_p
    if rest > 0.1:
        blocks.append({"kind": "tail", "src": seg_start + prev_p, "play": rest, "tts": 0.0})

    t = 0.0
    for b in blocks:
        b["t0"] = t
        if b["kind"] == "comment":
            b["freeze_start"] = t + b["play"]
            b["freeze_end"] = b["freeze_start"] + b["tts"]
            b["dur"] = b["play"] + b["tts"] + TAIL_PAD
        else:
            b["freeze_start"] = b["freeze_end"] = None
            b["dur"] = b["play"]
        t += b["dur"]
    part_dur = t + 0.1

    # --- kartlar ---
    cards, card_sizes, replies = [], [], []
    for i, c in enumerate(comments):
        cp = tmp / f"card_{i}.png"
        pw, ph = v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", cp, target_width=W)
        cards.append(cp)
        card_sizes.append((pw, ph))
        if c.get("snark"):
            rp = tmp / f"reply_{i}.png"
            v5.render_reply_card(c["snark"], rp, target_width=W)
            replies.append((i, rp))
    n_replies = len(replies)

    # --- ffmpeg kur ---
    cmd = ["ffmpeg", "-y", "-v", "error", "-i", str(src)]
    for i, b in enumerate([b for b in blocks if b["kind"] == "comment"]):
        cmd += ["-loop", "1", "-t", f"{b['tts'] + 0.2:.2f}", "-i", str(cards[b['i']])]
    for _, rp in replies:
        cmd += ["-loop", "1", "-t", "6.0", "-i", str(rp)]
    for vf in tts_files:
        cmd += ["-i", str(vf)]

    n = len(blocks)
    ncom = len(comments)
    fc = []
    seg_v, seg_a = [], []
    for bi, b in enumerate(blocks):
        clone = (b["tts"] + TAIL_PAD) if b["kind"] == "comment" else 0.05
        fc.append(f"[0:v]trim=start={b['src']:.3f}:end={b['src'] + b['play']:.3f},"
                  f"setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={clone:.3f}[bv{bi}]")
        if has_a:
            fc.append(f"[0:a]atrim=start={b['src']:.3f}:end={b['src'] + b['play']:.3f},"
                      f"asetpts=PTS-STARTPTS,apad=whole_dur={b['dur']:.3f},volume=0.5[ba{bi}]")
        else:
            fc.append(f"anullsrc=r=44100:cl=stereo,atrim=0:{b['dur']:.3f}[ba{bi}]")
        seg_v.append(f"[bv{bi}]")
        seg_a.append(f"[ba{bi}]")
    fc.append("".join(v + a for v, a in zip(seg_v, seg_a)) + f"concat=n={n}:v=1:a=1[vc][ac]")

    # yorum kartlari: yalnizca donma penceresinde (yorumla birlikte gelir/gider)
    for j, b in enumerate([b for b in blocks if b["kind"] == "comment"]):
        fc.append(f"[{1 + j}:v]setsar=1[pc{j}]")
    for j, (ci, rp) in enumerate(replies):
        fc.append(f"[{1 + ncom + j}:v]setsar=1[pr{j}]")

    last_v = "vc"
    for j, b in enumerate([b for b in blocks if b["kind"] == "comment"]):
        fc.append(f"[{last_v}][pc{j}]overlay=(W-w)/2:main_h*0.56-h/2:"
                  f"enable='between(t,{b['freeze_start']:.2f},{b['freeze_end']:.2f})'[ov{j}]")
        last_v = f"ov{j}"

    # CEVAP kartlari: yorum kartinin altinda, AYNI pencerede (birlikte kaybolur)
    s_scale = max(W, 480) / 720.0
    m_px = int(26 * s_scale)
    for j, (ci, rp) in enumerate(replies):
        b = next(bb for bb in blocks if bb["kind"] == "comment" and bb["i"] == ci)
        pw, ph = card_sizes[ci]
        ry = int(H * 0.56) + ph // 2 - m_px + int(10 * s_scale)
        fc.append(f"[{last_v}][pr{j}]overlay=(W-w)/2:{ry}:"
                  f"enable='between(t,{b['freeze_start']:.2f},{b['freeze_end']:.2f})'[rv{j}]")
        last_v = f"rv{j}"

    mix = ["[ac]"]
    for j, b in enumerate([b for b in blocks if b["kind"] == "comment"]):
        idx = 1 + ncom + n_replies + j
        dms = int(b["freeze_start"] * 1000)
        fc.append(f"[{idx}:a]adelay={dms}|{dms},volume=1.4[ta{j}]")
        mix.append(f"[ta{j}]")
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

    # --- dogrulama ---
    checks = []
    for j, b in enumerate([b for b in blocks if b["kind"] == "comment"]):
        i = b["i"]
        c = comments[i]
        mid = b["freeze_start"] + min(b["tts"] / 2, 1.2)
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

        # CEVAP karti dogrulamasi (amber pikseller, ayni pencerede)
        snark_ok = None
        if c.get("snark"):
            rpy = int(H * 0.56) + ph // 2 - m_px + int(10 * s_scale)
            amber = 0
            for y in range(max(rpy, 0), min(rpy + ph, H), 2):
                for x in range(0, W, 2):
                    r_, g_, b_ = fpx[x, y]
                    if r_ > 180 and 120 < g_ < 210 and b_ < 100:
                        amber += 1
            snark_ok = amber >= 120
            ok = ok and snark_ok
        checks.append({"comment": i + 1, "t": round(mid, 1), "ratio": round(ratio, 2),
                       "snark_ok": snark_ok, "ok": ok})

    # part ACILIS dogrulamasi: ilk yorum karti part'in ilk saniyesinde gorunmeli
    if comments:
        fp0 = tmp / "chk_open.png"
        subprocess.run(["ffmpeg", "-y", "-v", "error", "-ss", "0.4", "-i",
                        str(out_path), "-frames:v", "1", str(fp0)], check=True)
        first = next(b for b in blocks if b["kind"] == "comment" and b["i"] == 0)
        open_ok = first["freeze_start"] <= 0.4 < first["freeze_end"]
        checks.append({"comment": "acilis", "t": 0.4, "ok": open_ok})
        if not open_ok:
            raise RuntimeError(f"part acilisi yorumla baslamiyor: freeze={first['freeze_start']:.2f}-{first['freeze_end']:.2f}")

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
# Video render
# ----------------------------------------------------------------------------
async def render_video_v6(src, vid, pool, voice, quiet=False):
    W, H, dur = v2.probe_video(src)
    segs = plan_segments(dur)
    n_comments_needed = sum(len(plan_positions_local(sl)) for _, sl in segs)
    print(f"\n### Video {vid}: {dur:.1}s | {len(segs)} part (ham ~20sn) | "
          f"gereken yorum: {n_comments_needed} | havuz: {len(pool)}", flush=True)

    # TTS uret (normal hiz, emoji'siz), uzunlari at
    selected, tts_files, tts_durs = [], [], []
    tmp_root = LOCAL_OUT / f"_tts_v{vid}"
    tmp_root.mkdir(parents=True, exist_ok=True)
    for c in pool:
        if len(selected) >= n_comments_needed:
            break
        speech = v2.EMOJI_RE.sub(" ", c["text"]).strip()
        if not speech or sum(1 for ch in speech if ch.isalpha()) < 2:
            continue
        vf = tmp_root / f"voice_{len(selected)}.mp3"
        await v2.tts_speak(speech, vf, voice)
        d = v2.probe_audio(vf)
        if d > MAX_TTS or d < 0.8:
            vf.unlink(missing_ok=True)
            continue
        selected.append(c)
        tts_files.append(vf)
        tts_durs.append(d)
    if not selected:
        raise RuntimeError("kullanilabilir yorum yok")

    # yorumlari part'lara dagit (her part kendi pozisyon sayisi kadar)
    # kinayeli cevaplar (video basina <=2, yorumla birlikte gorunup gider)
    used_snarks = set()
    n_snark = 0
    for c in selected:
        if n_snark >= 2:
            break
        theme = v5.detect_lynch(c)
        if theme:
            c["snark"] = v5.pick_snark(theme, used_snarks)
            n_snark += 1
            if not quiet:
                print(f"  [kinayeli] @{c['user']}: '{c['text'][:35]}' -> CEVAP: {c['snark'][:40]}")

    # txt (editten ONCE, kaynak zamaniyla)
    txt_rows = []
    # yorumlari part'lara dagitim plani
    total_footage = sum(sl for _, sl in segs)
    short = len(selected) < n_comments_needed
    if short:
        print(f"  [!] havuz yetmedi ({len(selected)}/{n_comments_needed}) — yorumlar videoya orantili yayiliyor", flush=True)
        alloc = [1] * len(segs)
        extra = len(selected) - len(segs)
        if extra > 0:
            weights = [sl / total_footage for _, sl in segs]
            fracs = [w * extra for w in weights]
            alloc = [1 + int(f) for f in fracs]
            diff = len(selected) - sum(alloc)
            order = sorted(range(len(segs)), key=lambda i: -(fracs[i] - int(fracs[i])))
            i = 0
            while diff > 0:
                alloc[order[i % len(order)]] += 1
                diff -= 1
                i += 1
    else:
        alloc = [len(plan_positions_local(sl)) for _, sl in segs]

    results = []
    ci = 0
    for pi, (seg_start, seg_len) in enumerate(segs, start=1):
        natural = len(plan_positions_local(seg_len))
        remaining = len(selected) - ci
        n_here = min(alloc[pi - 1], remaining)
        if n_here <= 0:
            break
        positions = plan_positions_local(seg_len, n_here)
        p_comments = selected[ci: ci + n_here]
        p_tts = tts_files[ci: ci + n_here]
        p_durs = tts_durs[ci: ci + n_here]
        ci += n_here
        if not p_comments:
            break
        for p, c in zip(positions, p_comments):
            src_t = seg_start + p
            mm, ss = int(src_t // 60), int(src_t % 60)
            txt_rows.append(f"[{mm:02d}:{ss:02d}] @{c['user'].lstrip('@')}: {c['text']}")

        name = f"yorumlu_reel_sahenesirasisende_{vid}_part{pi}.mp4"
        local_out = LOCAL_OUT / name
        final_out = OUT_DIR / name
        try:
            checks = await render_part_v6(src, local_out, seg_start, seg_len, p_comments,
                                          p_tts, p_durs, positions, W, H, quiet=quiet)
            v5.copy_verified(local_out, final_out)
            d_out = v2.probe_video(final_out)[2]
            results.append({"video": vid, "part": pi, "output": str(final_out),
                            "raw_footage_s": round(seg_len, 1),
                            "duration_s": round(d_out, 2), "comments": n_here,
                            "size_mb": round(os.path.getsize(final_out) / 1024 / 1024, 2),
                            "checks": checks, "verified": True})
            print(f"  ✔ part{pi}: {seg_len:.1f}s ham + {n_here} yorum -> {d_out:.1f}s", flush=True)
        except Exception as e:
            print(f"  !!! part{pi} HATA: {e}", flush=True)
            results.append({"video": vid, "part": pi, "error": str(e)})

    tp = VIDEO_DIR / TXT_TEMPLATE.format(vid=vid)
    tp.write_text("\n".join(txt_rows) + "\n", encoding="utf-8")
    print(f"  txt: {tp.name} ({len(txt_rows)} yorum)", flush=True)
    return results


async def main():
    only = [int(a) for a in sys.argv[1:] if a.isdigit()]
    if not only and os.environ.get("IG_VIDEOS"):
        only = [int(x) for x in os.environ["IG_VIDEOS"].replace(",", " ").split()]
    vids = only if only else [1, 2, 3, 4, 5]
    LOCAL_OUT.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    all_results, failures = [], []
    for vid in vids:
        src = SRC_DIR / f"source_{vid}.mp4"
        if not (src.exists() and src.stat().st_size > 100_000):
            failures.append({"video": vid, "error": f"kaynak yok: {src}"})
            continue
        pool = v5.load_pool(vid)
        if not pool:
            failures.append({"video": vid, "error": "yorum havuzu bos"})
            continue
        voice = "tr-TR-AhmetNeural" if vid % 2 == 0 else "tr-TR-EmelNeural"
        try:
            all_results.extend(await render_video_v6(src, vid, pool, voice))
        except Exception as e:
            failures.append({"video": vid, "error": str(e)})

    status = "success" if not failures and all_results else ("partial" if all_results else "failed")
    report = {"status": status, "mode": "v6-raw20-partstart-comment-4.5s",
              "rules": ["ham ~20sn kesim", "part yorumla baslar", "yorum sonrasi aninda devam",
                        "4-5sn oynatma kadansi", "son yorumdan sonra video akarak biter",
                        "CEVAP karti yorumla birlikte gelir/gider", "tekrarsiz",
                        "sadece kendi yorumlari", "normal TTS hizi"],
              "rendered_parts": len(all_results), "outputs": all_results, "failures": failures}
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n==== RAPOR: {REPORT}", flush=True)
    print(json.dumps({"status": status, "parts": len(all_results), "failures": failures},
                     ensure_ascii=False), flush=True)
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
