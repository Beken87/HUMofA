# -*- coding: utf-8 -*-
"""
comment_reels_v5.py — Kullanici talimatlari (nihai kurallar):

1. Yorum bitince bekleme YOK — video aninda devam eder (freeze pad ~0.05s)
2. Iki yorum arasi (start-to-start) ~7 sn (6-8 sn bandi)
3. Part'lar video BITENE kadar devam eder, her part <= 19.8s
4. Once uzunluga gore gereken yorum sayisi belirlenir:
   gereken = ceil((dur - 1.2) / 7) + 1  (7 sn kadans ile tam kapsama)
   havuz yetmezse yorumlar videoya esit yayilir (aralik genisler)
5. Tekrarsiz: kuresel used-set ile garanti (part sinirlarinda tekrar imkansiz)
6. SADECE videonun KENDI yorumlari (havuz JSON'u: diag/full_comments.json)
7. TTS normal hiz; ses metninde emoji yok (kartta gorunur)
8. Ses cakismasi yok: TTS yalnizca freeze sirasinda, kaynak sesi yalnizca oynama sirasinda
9. Her yorum penceresi piksel dogrulamasi

Kullanim:
  python comment_reels_v5.py            # 5 videoyu da uretir
  python comment_reels_v5.py 1 3        # sadece video 1 ve 3
"""
import asyncio
import hashlib
import importlib.util
import json
import math
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
LOCAL_OUT = LOCAL_DIR / "outputs_v5"
REPORT = VIDEO_DIR / "videoeditor_report.json"
POOL_FILE = Path(os.environ.get(
    "IG_POOL_FILE", r"C:\Users\beken\AppData\Local\Temp\diag\full_comments.json"))
DIAG = Path(r"C:\Users\beken\AppData\Local\Temp\diag")

spec = importlib.util.spec_from_file_location("v2", str(SCRIPTS_DIR / "comment_reels_v2.py"))
v2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v2)

CADENCE = 5.0            # oynama boslugu hedefi (v2_part1 ritmi: ~5 sn oynama)
MIN_PLAY = 0.8           # iki yorum arasindaki min oynama suresi
MAX_PART = 19.8          # part ust siniri
FIRST_POS = 0.5          # ilk yorumun kaynak zamani
TAIL_PAD = 0.05          # yorum sonrasi donuk kuyruk (aninda devam icin ~0)
MAX_TTS = 5.5            # okuma ust siniri
MAX_CHARS = 85
REPLY_DUR = 3.5          # kinayeli cevap kartinin gorunme suresi (seslendirmesiz)
TXT_TEMPLATE = "reel_sahenesirasisende_{vid}_comments.txt"

# --- komiklik puani agirliklari ---
W_LIKES = 3.0
W_HUMOR = 2.0
W_LEN_FIT = 1.5

HUMOR = ["ahah", "haha", "koptum", "öldüm", "güldüm", "komik", "😂", "🤣", "😭",
         "💀", "efsane", "mükemmel", "delirdim", "abi", "ya"]

# --- linç / yanlis varsayim tespiti (bunlar kinayeli cevap alir) ---
LYNCH_PATTERNS = [
    (r"karısı|karim|karım", "evlilik"),
    (r"evli", "evlilik"),
    (r"yaş|yaşında|dedesi yaşında", "yaş"),
    (r"mide", "genel"),
    (r"nasıl .{0,12}(kadın|karı|kız)", "genel"),
    (r"utanmaz|rezil|ahlak", "genel"),
]
SNARKS = {
    "evlilik": [
        "İkisi tiyatro sahnesinde; nikâh memuru yok, senaryo var 🎭",
        "Linç ekibine duyurulur: bu bir skeç, kimse evlenmiyor 🍿",
        "Rejisör arıyor: dedektiflik başvurunuz değerlendirmeye alındı 🔍",
    ],
    "yaş": [
        "Yaş dedektiflerine: sahne dışında tanışmadık bile 🎭",
        "Cast yönetmeni yaş farkını bilerek seçti; şaşırmaya devam 🎬",
    ],
    "genel": [
        "Sakin olun, tiyatro bu — gerçek hayat değil 🎭",
        "Yorum uçtu, senaryo yerinde kaldı 🍿",
    ],
}


# ----------------------------------------------------------------------------
# Havuz (SADECE videonun kendi yorumlari)
# ----------------------------------------------------------------------------
def clean_text(t):
    t = re.sub(r"\s*Beğen\s*$", "", (t or "").strip())
    return re.sub(r"\s+", " ", t).strip()


def usable(c):
    t = clean_text(c.get("text"))
    if not (4 <= len(t) <= MAX_CHARS):
        return None
    if sum(1 for ch in v2.EMOJI_RE.sub("", t) if ch.isalpha()) < 3:
        return None
    return t


def rank(c):
    """Komiklik puani: begeni + mizah isaretleri + uzunluk uyumu."""
    t = clean_text(c["text"]).lower()
    s = 0.0
    try:
        s += float(c.get("likes") or 0) * W_LIKES
    except Exception:
        pass
    for kw in HUMOR:
        if kw in t:
            s += W_HUMOR
    if 8 <= len(clean_text(c["text"])) <= 60:
        s += W_LEN_FIT
    s -= max(0, len(t) - 45) * 0.05
    return s


def detect_lynch(c):
    """Linç/yanlis varsayim yorumu mu? Tema dondurur (None degilse)."""
    t = clean_text(c["text"]).lower()
    if len(t) > 70:
        return None
    for pat, theme in LYNCH_PATTERNS:
        if re.search(pat, t):
            return theme
    return None


def pick_snark(theme, used):
    """Temaya uygun, daha once kullanilmamis kinayeli cevap secer."""
    opts = [s for s in SNARKS.get(theme, SNARKS["genel"]) if s not in used]
    if not opts:
        opts = SNARKS["genel"]
    import random as _r
    s = _r.choice(opts)
    used.add(s)
    return s


def load_pool(vid):
    data = {}
    if POOL_FILE.exists():
        data = json.loads(POOL_FILE.read_text(encoding="utf-8"))
    blob = data.get(str(vid)) or {}
    pool = []
    seen = set()
    for c in blob.get("comments", []):
        t = usable(c)
        if not t:
            continue
        key = (c.get("user"), t)
        if key in seen:
            continue
        seen.add(key)
        pool.append({"user": c["user"], "text": t, "likes": c.get("likes") or "1"})
    if not pool:
        # fallback: eski txt dosyasindan ([mm:ss] @user: text)
        tp = VIDEO_DIR / TXT_TEMPLATE.format(vid=vid)
        if tp.exists():
            for line in tp.read_text(encoding="utf-8").splitlines():
                m = re.match(r"^\[(\d+):(\d+)\]\s*(@[^:\s]+):\s*(.*)$", line.strip())
                if not m:
                    continue
                t = usable(m.group(4))
                if not t:
                    continue
                key = (m.group(2), t)
                if key in seen:
                    continue
                seen.add(key)
                pool.append({"user": m.group(2), "text": t, "likes": "1"})
    pool.sort(key=lambda c: -rank(c))
    return pool


# ----------------------------------------------------------------------------
# Plan: uzunluk -> gereken yorum sayisi -> pozisyonlar -> part dilimleri
# ----------------------------------------------------------------------------
def needed_count(dur):
    """7 sn kadans ile videoyu bastan sona kaplamak icin gereken yorum sayisi."""
    return max(1, math.ceil((dur - FIRST_POS - MIN_PLAY) / CADENCE) + 1)


def plan_positions(dur, n, need):
    """Yorumlarin KAYNAK (oynama) zamani pozisyonlari.
    n >= need VE tam kadans sigiyorsa: 0.5, 7.5, 14.5...
    yoksa: video SONUNA kadar esit yay (son yorum dur-2'de)."""
    if n <= 0:
        return []
    if n == 1:
        return [FIRST_POS]
    full = [FIRST_POS + i * CADENCE for i in range(n)]
    if n >= need and full[-1] <= dur - MIN_PLAY:
        return full
    # esit yay: son yorum video sonundan ~2 sn once
    last = max(FIRST_POS, dur - 2.0)
    step = (last - FIRST_POS) / (n - 1)
    return [FIRST_POS + i * step for i in range(n)]


def plan_parts(dur, positions, tts_durs):
    """Zaman cizgisini part'lara boler (yorum sinirinda keser).
    Bolum butcesi: (pos_i - pos_{i-1}) + tts_i + pad — part acilisi da ayni boslugu oynatir."""
    parts, cur, out_t = [], [], 0.0
    prev_pos = 0.0
    for pos, d in zip(positions, tts_durs):
        add = (pos - prev_pos) + d + TAIL_PAD
        if cur and out_t + add > MAX_PART:
            parts.append(cur)
            cur = []
            out_t = 0.0
        cur.append((pos, d))
        out_t += add
        prev_pos = pos
    if cur:
        parts.append(cur)
    return parts


def render_reply_card(text, output_png, target_width=720):
    """Kinayeli CEVAP karti: kucuk, amber vurgulu, seslendirmesiz.
    Yorum kartinin ALTINDA gorunur; 'CEVAP' etiketiyle net ayristirilir."""
    from PIL import Image, ImageDraw, ImageFilter, ImageFont
    s = max(target_width, 480) / 720.0
    card_w = int(target_width * 0.68)
    pad_x, pad_y = int(24 * s), int(16 * s)
    f_body = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", int(21 * s))
    f_chip = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", int(15 * s))
    try:
        f_emoji = ImageFont.truetype(r"C:\Windows\Fonts\seguiemj.ttf", int(22 * s))
    except Exception:
        f_emoji = f_body

    # olculmus satir sarma
    words = text.split(" ")
    lines, cur, cur_w = [], [], 0.0
    space_w = f_body.getlength(" ")
    max_w = card_w - 2 * pad_x - int(10 * s)
    for w_ in words:
        segs = v2._segments(w_)
        ww = sum(v2._seg_width(sg, f_body, f_emoji) for sg in segs)
        add = ww if not cur else ww + space_w
        if cur and cur_w + add > max_w:
            lines.append(cur)
            cur = segs
            cur_w = ww
        else:
            if cur:
                cur.append(("t", " "))
            cur.extend(segs)
            cur_w += add
    if cur:
        lines.append(cur)

    line_h = int(f_body.size * 1.35)
    chip_h = int(22 * s)
    card_h = pad_y + chip_h + int(8 * s) + len(lines) * line_h + pad_y

    m = int(14 * s)
    img = Image.new("RGBA", (card_w + 2 * m, card_h + 2 * m), (0, 0, 0, 0))
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([(m, m + int(5 * s)), (m + card_w, m + card_h + int(5 * s))],
                                         radius=int(18 * s), fill=(0, 0, 0, 100))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(int(7 * s))))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([(m, m), (m + card_w - 1, m + card_h - 1)],
                        radius=int(18 * s), fill=(28, 24, 16, 242),
                        outline=(245, 180, 60, 90), width=2)
    # CEVAP cipi
    chip_w = int(f_chip.getlength("CEVAP") + 18 * s)
    d.rounded_rectangle([(m + pad_x, m + pad_y), (m + pad_x + chip_w, m + pad_y + chip_h)],
                        radius=int(6 * s), fill=(245, 170, 50, 255))
    d.text((m + pad_x + int(9 * s), m + pad_y + int(3 * s)), "CEVAP", font=f_chip,
           fill=(30, 22, 8, 255))
    # metin
    ty = m + pad_y + chip_h + int(8 * s)
    for ln in lines:
        cx = m + pad_x
        for seg in ln:
            v2._draw_seg(d, (cx, ty), seg, f_body, f_emoji, (255, 232, 190, 255))
            cx += v2._seg_width(seg, f_body, f_emoji)
        ty += line_h
    img.save(output_png, "PNG")
    return img.size


# ----------------------------------------------------------------------------
# Part render (v4 motoru uyarlamasi: pad yok, pozisyon tabanli)
# ----------------------------------------------------------------------------
async def render_part(src, out_path, part_positions_tts, voice, quiet=False):
    """part_positions_tts: [(kaynak_pos, tts_dur), ...] — pozisyonlar KAYNAK zamaninda."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.parent / f"_tmp5_{out_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)

    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height:format=duration", "-of", "json", str(src)],
        capture_output=True, text=True, check=True)
    pj = json.loads(probe.stdout)
    W, H = int(pj["streams"][0]["width"]), int(pj["streams"][0]["height"])
    dur = float(pj.get("format", {}).get("duration") or 0)
    has_a = v2.has_audio(src)

    # segmentler: her yorum icin [oynama] + [freeze]
    segs = []  # (src_start, play_len, tts_dur)
    prev_end = None
    for pos, d in part_positions_tts:
        seg_start = prev_end if prev_end is not None else max(0.0, pos - FIRST_POS)
        # oynama: onceki yorumun pozisyonundan bu yorumun pozisyonuna
        play = pos - seg_start
        if play < 0.05:
            play = 0.05
        segs.append({"src": seg_start, "play": play, "tts": d})
        prev_end = pos

    # cikti zaman cizgisi
    t_cursor = 0.0
    for s in segs:
        s["freeze_start"] = t_cursor + s["play"]
        s["freeze_end"] = s["freeze_start"] + s["tts"]
        s["seg_out_dur"] = s["play"] + s["tts"] + TAIL_PAD
        t_cursor += s["seg_out_dur"]
    part_dur = t_cursor + 0.1

    # kartlar + TTS dosyalari
    cards = []
    for i, s in enumerate(segs):
        c = part_positions_tts[i][2] if len(part_positions_tts[i]) > 2 else None
        cards.append(tmp / f"card_{i}.png")
    # (yorum objeleri ayrica gelsin)
    return W, H, dur, has_a, segs, part_dur, tmp


async def render_part_full(src, out_path, comments, positions, tts_files, voice, quiet=False):
    """comments: [dict], positions: [kaynak_pos], tts_files: [Path], tts_durs: [float]"""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.parent / f"_tmp5_{out_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)

    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height", "-of", "json", str(src)],
        capture_output=True, text=True, check=True)
    pj = json.loads(probe.stdout)
    W, H = int(pj["streams"][0]["width"]), int(pj["streams"][0]["height"])
    has_a = v2.has_audio(src)

    # segmentler
    segs = []
    prev_end = None
    for c, pos, vf, d in zip(comments, positions, tts_files, [None] * len(comments)):
        pass  # tts durations ayrica
    # (asagida duzgun kuruluyor)
    return None


async def render_video(src, vid, pool, voice, quiet=False):
    """Bir videonun tamamini planlar ve part'lari render eder."""
    W0, H0, dur = v2.probe_video(src)
    need = needed_count(dur)
    print(f"\n### Video {vid}: {dur:.1f}s | gereken yorum: {need} | havuz: {len(pool)}")

    # 1) TTS uret (normal hiz, emoji'siz), uzunlari at
    selected, tts_files, tts_durs = [], [], []
    tmp_root = LOCAL_OUT / f"_tts_v{vid}"
    tmp_root.mkdir(parents=True, exist_ok=True)
    for c in pool:
        if len(selected) >= need:
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

    spread = len(selected) < need
    if spread:
        print(f"  [!] havuz yetmedi ({len(selected)}/{need}) — yorumlar esit yayiliyor")

    # linç/yanlis varsayim yorumlarina kinayeli cevap ata (video basina en fazla 2)
    used_snarks = set()
    n_snark = 0
    for c in selected:
        if n_snark >= 2:
            break
        theme = detect_lynch(c)
        if theme:
            c["snark"] = pick_snark(theme, used_snarks)
            n_snark += 1
            if not quiet:
                print(f"  [kinayeli] @{c['user']}: '{c['text'][:35]}...' -> CEVAP: {c['snark'][:40]}")

    # 2) pozisyonlar + part plani
    positions = plan_positions(dur, len(selected), need)
    # txt dosyasi: editten ONCE yaz (kullanici talimati)
    write_txt_files({vid: list(zip(positions, selected))})
    zipped = list(zip(positions, tts_durs))
    parts = plan_parts(dur, [p for p, _ in zipped], [d for _, d in zipped])

    results = []
    idx0 = 0
    src_resume = 0.0
    for pi, part in enumerate(parts, start=1):
        n = len(part)
        p_comments = selected[idx0: idx0 + n]
        p_positions = [p for p, _ in part]
        p_tts_files = tts_files[idx0: idx0 + n]
        p_tts_durs = [d for _, d in part]
        idx0 += n

        name = f"yorumlu_reel_sahenesirasisende_{vid}_part{pi}.mp4"
        local_out = LOCAL_OUT / name
        final_out = OUT_DIR / name
        try:
            checks = await render_one_part(src, local_out, p_comments, p_positions,
                                           p_tts_files, p_tts_durs, W0, H0, None,
                                           voice, src_resume=src_resume)
            src_resume = part[-1][0]
            copy_verified(local_out, final_out)
            d_out = v2.probe_video(final_out)[2]
            results.append({"video": vid, "part": pi, "output": str(final_out),
                            "duration_s": round(d_out, 2), "comments": n,
                            "size_mb": round(os.path.getsize(final_out) / 1024 / 1024, 2),
                            "checks": checks, "verified": True})
            print(f"  ✔ part{pi}: {d_out:.1f}s, {n} yorum")
        except Exception as e:
            print(f"  !!! part{pi} HATA: {e}")
            results.append({"video": vid, "part": pi, "error": str(e)})
    return results, spread


async def render_one_part(src, out_path, comments, positions, tts_files, tts_durs,
                           W, H, _unused, voice, src_resume=0.0, quiet=False):
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.parent / f"_tmp5_{out_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)
    has_a = v2.has_audio(src)

    # segmentler: oynama + freeze — part acilisi onceki partin kaldigi yerden (src_resume)
    segs = []
    prev_end = src_resume
    for pos, d in zip(positions, tts_durs):
        play = max(0.05, pos - prev_end)
        segs.append({"src": prev_end, "play": play, "tts": d})
        prev_end = pos
    # kinayeli cevap varsa freeze uzar (seslendirmesiz)
    for s, c in zip(segs, comments):
        s["reply"] = REPLY_DUR if c.get("snark") else 0.0
    t_cursor = 0.0
    for s in segs:
        s["freeze_start"] = t_cursor + s["play"]
        s["freeze_end"] = s["freeze_start"] + s["tts"]
        s["out_dur"] = s["play"] + s["tts"] + TAIL_PAD + s["reply"]
        t_cursor += s["out_dur"]
    part_dur = t_cursor + 0.1

    # kartlar (+ cevap kartlari)
    cards, card_sizes, replies = [], [], []
    for i, c in enumerate(comments):
        cp = tmp / f"card_{i}.png"
        pw, ph = v2.render_comment_card(c["user"], c["text"], c.get("likes") or "1", cp, target_width=W)
        cards.append(cp)
        card_sizes.append((pw, ph))
        if c.get("snark"):
            rp = tmp / f"reply_{i}.png"
            render_reply_card(c["snark"], rp, target_width=W)
            replies.append((i, rp))
    n_replies = len(replies)

    # ffmpeg
    cmd = ["ffmpeg", "-y", "-v", "error", "-i", str(src)]
    for s, cp in zip(segs, cards):
        w = s["tts"] + s["reply"] + 0.2
        cmd += ["-loop", "1", "-t", f"{w:.2f}", "-i", str(cp)]
    for _, rp in replies:
        cmd += ["-loop", "1", "-t", f"{REPLY_DUR + 0.3:.2f}", "-i", str(rp)]
    for vf in tts_files:
        cmd += ["-i", str(vf)]

    n = len(segs)
    fc = []
    seg_v, seg_a = [], []
    for i, s in enumerate(segs):
        seg_len = s["out_dur"]
        fc.append(f"[0:v]trim=start={s['src']:.3f}:end={s['src'] + s['play']:.3f},"
                  f"setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={s['tts'] + TAIL_PAD + s['reply']:.3f}[sv{i}]")
        if has_a:
            fc.append(f"[0:a]atrim=start={s['src']:.3f}:end={s['src'] + s['play']:.3f},"
                      f"asetpts=PTS-STARTPTS,apad=whole_dur={seg_len:.3f},volume=0.5[sa{i}]")
        else:
            fc.append(f"anullsrc=r=44100:cl=stereo,atrim=0:{seg_len:.3f}[sa{i}]")
        seg_v.append(f"[sv{i}]")
        seg_a.append(f"[sa{i}]")
    fc.append("".join(v + a for v, a in zip(seg_v, seg_a)) + f"concat=n={n}:v=1:a=1[vc][ac]")

    for i in range(n):
        fc.append(f"[{1 + i}:v]setsar=1[pc{i}]")
    for j, (ci, rp) in enumerate(replies):
        fc.append(f"[{1 + n + j}:v]setsar=1[pr{j}]")
    last_v = "vc"
    for i, s in enumerate(segs):
        st = s["freeze_start"]
        et = s["freeze_end"] + (s["reply"] if s["reply"] else 0.05)
        fc.append(f"[{last_v}][pc{i}]overlay=(W-w)/2:main_h*0.56-h/2:"
                  f"enable='between(t,{st:.2f},{et:.2f})'[ov{i}]")
        last_v = f"ov{i}"
    # cevap kartlari: yorum kartinin altinda, sadece cevap penceresinde
    s_scale = max(W, 480) / 720.0
    m_px = int(26 * s_scale)
    for j, (ci, rp) in enumerate(replies):
        s = segs[ci]
        pw, ph = card_sizes[ci]
        ry = int(H * 0.56) + ph // 2 - m_px + int(10 * s_scale)
        fc.append(f"[{last_v}][pr{j}]overlay=(W-w)/2:{ry}:"
                  f"enable='between(t,{s['freeze_end']:.2f},{s['freeze_end'] + s['reply']:.2f})'[rv{j}]")
        last_v = f"rv{j}"

    mix = ["[ac]"]
    for i, s in enumerate(segs):
        idx = 1 + n + n_replies + i
        dms = int(s["freeze_start"] * 1000)
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
        raise RuntimeError("ffmpeg: " + res.stderr[-500:])

    # dogrulama
    checks = []
    s_scale = max(W, 480) / 720.0
    m_px = int(26 * s_scale)
    for i, s in enumerate(segs):
        mid = s["freeze_start"] + min(s["tts"] / 2, 1.2)
        c = comments[i]
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


# ----------------------------------------------------------------------------
# txt dosyalari (kullanici istegi: yorumlar cekilince txt yap, sonra edit)
# ----------------------------------------------------------------------------
def write_txt_files(plans):
    """plans: {vid: [(pos, comment), ...]} — [mm:ss] @user: text formatinda yazar."""
    for vid, items in plans.items():
        lines = []
        for pos, c in items:
            mm, ss = int(pos // 60), int(pos % 60)
            lines.append(f"[{mm:02d}:{ss:02d}] @{c['user'].lstrip('@')}: {c['text']}")
        p = VIDEO_DIR / TXT_TEMPLATE.format(vid=vid)
        p.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"  txt: {p.name} ({len(lines)} yorum)")


async def main():
    only = [int(a) for a in sys.argv[1:] if a.isdigit()]
    vids = only if only else [1, 2, 3, 4, 5]
    LOCAL_OUT.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    all_results, failures, txt_plans = [], [], {}
    for vid in vids:
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
        pool = load_pool(vid)
        if not pool:
            failures.append({"video": vid, "error": "yorum havuzu bos"})
            continue
        voice = "tr-TR-AhmetNeural" if vid % 2 == 0 else "tr-TR-EmelNeural"
        try:
            results, spread = await render_video(src, vid, pool, voice)
            all_results.extend(results)
            # txt icin secilen yorumlarin pozisyonlari (render_video'dan geliyor olmali)
        except Exception as e:
            failures.append({"video": vid, "error": str(e)})

    status = "success" if not failures and all_results else ("partial" if all_results else "failed")
    report = {"status": status, "mode": "v5-freeze-7s",
              "rules": ["yorum sonrasi aninda devam", "start-to-start ~7sn", "part<=20s video sonuna kadar",
                        "tekrarsiz", "sadece kendi yorumlari", "normal TTS hizi"],
              "rendered_parts": len(all_results), "outputs": all_results, "failures": failures}
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n==== RAPOR: {REPORT}")
    print(json.dumps({"status": status, "parts": len(all_results), "failures": failures}, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())
