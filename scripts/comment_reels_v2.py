# -*- coding: utf-8 -*-
"""
comment_reels_v2.py — Iyilestirilmis Instagram yorum karti + montaj motoru.

- Olculmus metin sarma (font.getlength) -> metin ASLA kart disina tasamaz, satirlar tam gorunur
- Renkli emoji destegi (seguiemj.ttf, embedded_color; hata olursa emoji'siz fallback)
- Instagram tarzi kart: kalin kullanici adi + mavi dogrulanma rozeti, avatar, kalp + begeni,
  'Yanitla', yumusak golgeli koyu cam kart
- Fontlar video genisligine gore olceklenir (720 ve 1080 videolarda ayni gorunum)
- ffmpeg: kartlar -loop 1 ile beslenir (tek kare sorunu yok), alt-merkez konum
- Her yorum penceresi icin kare cekip KART+METIN piksel dogrulamasi yapar
"""
import asyncio
import json
import os
import re
import shutil
import subprocess
import sys
import textwrap
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

from PIL import Image, ImageDraw, ImageFilter, ImageFont

# ----------------------------------------------------------------------------
# Yollar
# ----------------------------------------------------------------------------
SCRIPTS_DIR = Path(r"C:\Users\beken\Desktop\Files\BofA\scripts")
VIDEO_DIR = Path(r"C:\Users\beken\Desktop\Files\SSS_Yorumlu_Video")
OUT_DIR = VIDEO_DIR / "Editlenmişler"
LOCAL_DIR = Path(os.environ.get(
    "VE_LOCAL_DIR", r"C:\Users\beken\AppData\Local\hermes\cache\scratch\sss_local"))
LOCAL_OUT = LOCAL_DIR / "outputs_v2"
REPORT = VIDEO_DIR / "videoeditor_report.json"

FONT_TEXT = r"C:\Windows\Fonts\segoeui.ttf"
FONT_BOLD = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_EMOJI = r"C:\Windows\Fonts\seguiemj.ttf"

EMOJI_RE = re.compile(
    "["
    "\U0001F000-\U0001FAFF"
    "\U00002600-\U000027BF"
    "\U0001F1E6-\U0001F1FF"
    "\U00002B00-\U00002BFF"
    "\U0000FE00-\U0000FE0F"
    "\U0000200D"
    "\U00002764-\U000027BF"
    "]"
)

# ----------------------------------------------------------------------------
# Kart render
# ----------------------------------------------------------------------------
AVATAR_COLORS = [
    (99, 102, 241), (236, 72, 153), (16, 185, 129),
    (245, 158, 11), (239, 68, 68), (59, 130, 246),
]


def _f(path, size):
    return ImageFont.truetype(path, int(size))


def _segments(text):
    """Metni (tip, icerik) bolumlerine ayir: 't' metin, 'e' emoji."""
    segs = []
    buf = ""
    for ch in text:
        if EMOJI_RE.match(ch):
            if buf:
                segs.append(("t", buf))
                buf = ""
            segs.append(("e", ch))
        else:
            buf += ch
    if buf:
        segs.append(("t", buf))
    return segs


def _seg_width(seg, f_text, f_emoji):
    kind, s = seg
    try:
        return (f_emoji if kind == "e" else f_text).getlength(s)
    except Exception:
        return f_text.getlength(s.replace("", " ")[:-1]) or f_text.size * len(s)


def _draw_seg(draw, xy, seg, f_text, f_emoji, fill):
    kind, s = seg
    if kind == "e":
        try:
            draw.text(xy, s, font=f_emoji, embedded_color=True)
            return
        except Exception:
            pass
    draw.text(xy, s, font=f_text, fill=fill)


def _wrap_segments(text, f_text, f_emoji, max_w):
    """Kelime bazli, olculmus satir sarma. Her satir (seg,) listesi."""
    lines = []
    for raw in text.split("\n"):
        words = raw.split(" ")
        cur = []
        cur_w = 0.0
        space_w = f_text.getlength(" ")
        for word in words:
            wsegs = _segments(word)
            ww = sum(_seg_width(s, f_text, f_emoji) for s in wsegs)
            add = ww if not cur else ww + space_w
            if cur and cur_w + add > max_w:
                lines.append(cur)
                cur = wsegs
                cur_w = ww
            else:
                if cur:
                    cur.append(("t", " "))
                cur.extend(wsegs)
                cur_w += add
        if cur or not lines:
            lines.append(cur if cur else [("t", "")])
    # ardarda bos segleri birlestir
    merged = []
    for ln in lines:
        out = []
        for k, s in ln:
            if out and out[-1][0] == k == "t":
                out[-1] = ("t", out[-1][1] + s)
            else:
                out.append((k, s))
        merged.append(out)
    return merged


def render_comment_card(username, text, likes_str, output_png, target_width=720):
    """Iyilestirilmis Instagram yorum karti. Olcek: target_width/720."""
    s = max(target_width, 480) / 720.0
    card_w = int(target_width * 0.90)
    pad_x, pad_y = int(34 * s), int(26 * s)
    avatar = int(54 * s)

    f_user = _f(FONT_BOLD, int(26 * s))
    f_time = _f(FONT_TEXT, int(19 * s))
    f_body = _f(FONT_TEXT, int(27 * s))
    f_foot = _f(FONT_TEXT, int(19 * s))
    f_av = _f(FONT_BOLD, int(28 * s))
    try:
        f_emoji = _f(FONT_EMOJI, int(30 * s))
        emoji_ok = True
    except Exception:
        f_emoji = f_body
        emoji_ok = False

    max_text_w = card_w - 2 * pad_x
    clean_text = text if emoji_ok else EMOJI_RE.sub("", text).strip()
    lines = _wrap_segments(clean_text, f_body, f_emoji, max_text_w)
    line_h = int(f_body.size * 1.42)

    head_h = avatar + int(16 * s)
    text_h = len(lines) * line_h
    foot_h = int(42 * s)
    card_h = pad_y + head_h + int(10 * s) + text_h + foot_h + pad_y

    shadow_m = int(26 * s)
    img = Image.new("RGBA", (card_w + 2 * shadow_m, card_h + 2 * shadow_m), (0, 0, 0, 0))

    # yumusak golge
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle(
        [(shadow_m, shadow_m + int(8 * s)), (shadow_m + card_w, shadow_m + card_h + int(8 * s))],
        radius=int(26 * s), fill=(0, 0, 0, 120))
    sh = sh.filter(ImageFilter.GaussianBlur(int(10 * s)))
    img.alpha_composite(sh)

    d = ImageDraw.Draw(img)
    x0, y0 = shadow_m, shadow_m

    # kart govdesi: koyu cam
    d.rounded_rectangle(
        [(x0, y0), (x0 + card_w - 1, y0 + card_h - 1)],
        radius=int(26 * s), fill=(13, 17, 26, 242),
        outline=(255, 255, 255, 38), width=2)
    # ust kenar vurgusu
    d.rounded_rectangle(
        [(x0, y0), (x0 + card_w - 1, y0 + card_h - 1)],
        radius=int(26 * s), outline=(120, 160, 255, 26), width=1)

    # avatar
    import random as _r
    color = _r.choice(AVATAR_COLORS)
    ax, ay = x0 + pad_x, y0 + pad_y
    d.ellipse([(ax, ay), (ax + avatar, ay + avatar)], fill=color + (255,))
    init = (username.replace("@", "")[:1] or "K").upper()
    d.text((ax + avatar * 0.32, ay + avatar * 0.22), init, font=f_av, fill=(255, 255, 255, 255))

    # kullanici adi (kalin) + dogrulanma rozeti
    ux = ax + avatar + int(16 * s)
    uy = ay + int(2 * s)
    d.text((ux, uy), username, font=f_user, fill=(240, 244, 250, 255))
    uw = f_user.getlength(username)
    bs = int(17 * s)
    bx, by = ux + uw + int(8 * s), uy + int(6 * s)
    d.ellipse([(bx, by), (bx + bs, by + bs)], fill=(29, 155, 240, 255))
    cw = max(2, int(2 * s))
    d.line([(bx + bs * 0.26, by + bs * 0.52), (bx + bs * 0.44, by + bs * 0.70),
            (bx + bs * 0.78, by + bs * 0.30)], fill=(255, 255, 255, 255), width=cw)
    # '2 sa' gri
    d.text((ux, uy + int(30 * s)), "2 sa", font=f_time, fill=(150, 160, 175, 255))

    # yorum metni (olculmus, emoji destekli)
    ty = y0 + pad_y + head_h + int(10 * s)
    for ln in lines:
        cx = x0 + pad_x
        for seg in ln:
            _draw_seg(d, (cx, ty), seg, f_body, f_emoji, (245, 247, 252, 255))
            cx += _seg_width(seg, f_body, f_emoji)
        ty += line_h

    # alt bar: kalp + begeni + Yanitla
    fy = y0 + card_h - pad_y - int(26 * s)
    hx, hy = x0 + pad_x, fy + int(4 * s)
    hs = int(19 * s)
    r = hs * 0.26
    d.ellipse([(hx, hy), (hx + 2 * r, hy + 2 * r)], fill=(237, 73, 86, 255))
    d.ellipse([(hx + hs - 2 * r, hy), (hx + hs, hy + 2 * r)], fill=(237, 73, 86, 255))
    d.polygon([(hx, hy + r), (hx + hs, hy + r), (hx + hs / 2, hy + hs * 0.92)], fill=(237, 73, 86, 255))
    d.rectangle([(hx + r * 0.3, hy + r), (hx + hs - r * 0.3, hy + hs * 0.55)], fill=(237, 73, 86, 255))
    d.text((hx + hs + int(10 * s), fy), str(likes_str), font=f_foot, fill=(225, 230, 240, 255))
    lw = f_foot.getlength(str(likes_str))
    d.text((hx + hs + int(10 * s) + lw + int(26 * s), fy), "Yanıtla",
           font=f_foot, fill=(140, 152, 170, 255))

    img.save(output_png, "PNG")
    return img.size  # (w, h) golge dahil


# ----------------------------------------------------------------------------
# probe / TTS
# ----------------------------------------------------------------------------
def probe_video(path):
    res = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height:format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, check=True)
    data = json.loads(res.stdout)
    w = int(data["streams"][0]["width"])
    h = int(data["streams"][0]["height"])
    dur = float(data.get("format", {}).get("duration") or 0)
    return w, h, dur


def probe_audio(path):
    res = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
        capture_output=True, text=True, check=True)
    return float(res.stdout.strip())


def has_audio(path):
    res = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries",
         "stream=index", "-of", "csv=p=0", str(path)],
        capture_output=True, text=True, check=True)
    return bool(res.stdout.strip())


async def tts_speak(text, out_mp3, voice, rate=None):
    """edge-tts; emoji takilirse temizleyip tekrar dener. rate: '+15%' gibi."""
    import edge_tts

    async def go(t):
        if rate:
            c = edge_tts.Communicate(t, voice, rate=rate)
        else:
            c = edge_tts.Communicate(t, voice)
        await c.save(out_mp3)

    await go(text)
    if not os.path.exists(out_mp3) or os.path.getsize(out_mp3) < 1000:
        clean = EMOJI_RE.sub(" ", text).strip() or "Yorum"
        await go(clean)
    if not os.path.exists(out_mp3) or os.path.getsize(out_mp3) < 1000:
        raise RuntimeError("TTS bos cikti")


# ----------------------------------------------------------------------------
# Montaj
# ----------------------------------------------------------------------------
async def edit_v2(video_path, output_path, comments, voice="tr-TR-AhmetNeural",
                  verify=True, quiet=False):
    video_path, output_path = Path(video_path), Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    W, H, dur = probe_video(video_path)
    if not quiet:
        print(f"  • {video_path.name}: {W}x{H}, {dur:.1f}s, {len(comments)} yorum")

    tmp = output_path.parent / f"_tmp_v2_{video_path.stem}"
    tmp.mkdir(parents=True, exist_ok=True)

    # ---- TTS + zamanlama: tum yorumlar sigana kadar hiz artir ----
    audios, cards, windows = None, None, None
    for rate in (None, "+15%", "+30%", "+45%"):
        audios, cards, windows = [], [], []
        t_start = 0.6
        fit_all = True
        for i, c in enumerate(comments):
            ts = float(c.get("ts") or 0)
            st = max(t_start, ts if ts > 0 else t_start)
            if st >= dur - 0.8:
                fit_all = False
                break
            card_png = tmp / f"card_{i}.png"
            voice_mp3 = tmp / f"voice_{i}.mp3"
            cw, ch = render_comment_card(c["user"], c["text"], c.get("likes", "❤"), card_png, target_width=W)
            await tts_speak(c["text"], voice_mp3, voice, rate=rate)
            adur = probe_audio(voice_mp3)
            et = min(st + adur + 0.45, dur)
            audios.append(voice_mp3)
            cards.append(card_png)
            windows.append((st, et, adur))
            t_start = et + 0.45
        if fit_all or len(windows) >= len(comments):
            if rate:
                print(f"    [hiz] TTS hizi {rate} ile tum yorumlar sigdi")
            break
        print(f"    [hiz] {len(windows)}/{len(comments)} yorum sigdi, TTS hizi artiriliyor...")

    if not windows:
        raise RuntimeError("hicbir yorum video süresine sigmadi")

    total = min(dur, windows[-1][1] + 0.8)

    cmd = ["ffmpeg", "-y", "-v", "error", "-i", str(video_path)]
    for (st, et, _), cp in zip(windows, cards):
        cmd += ["-loop", "1", "-t", f"{et - st + 0.3:.2f}", "-i", str(cp)]
    for ap in audios:
        cmd += ["-i", str(ap)]

    n = len(windows)
    fc = []
    last_v = "0:v"
    for i, ((st, et, _), cp) in enumerate(zip(windows, cards)):
        fc.append(f"[{1+i}:v]setsar=1[c{i}]")
    for i, ((st, et, _), cp) in enumerate(zip(windows, cards)):
        nxt = f"v{i+1}"
        fc.append(
            f"[{last_v}][c{i}]overlay=(W-w)/2:main_h*0.56-h/2:"
            f"enable='between(t,{st:.2f},{et:.2f})'[{nxt}]")
        last_v = nxt

    if has_audio(video_path):
        fc.append("[0:a]volume=0.15[bg]")
        mix = ["[bg]"]
    else:
        mix = []
    for i, (st, _, _) in enumerate(windows):
        idx = 1 + n + i
        dms = int(st * 1000)
        fc.append(f"[{idx}:a]adelay={dms}|{dms},volume=1.35[a{i}]")
        mix.append(f"[a{i}]")
    fc.append(f"{''.join(mix)}amix=inputs={len(mix)}:duration=longest[fa]")

    cmd += ["-filter_complex", ";".join(fc),
            "-map", f"[{last_v}]", "-map", "[fa]",
            "-t", f"{total:.2f}",
            "-c:v", "libx264", "-preset", "fast", "-crf", "21",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
            str(output_path)]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError("ffmpeg hatasi: " + res.stderr[-600:])

    # ---- dogrulama: her yorum penceresinin ortasinda kare cek, kart+metin kontrol ----
    checks = []
    if verify:
        from PIL import Image as _I
        for i, (st, et, _) in enumerate(windows):
            mid = (st + et) / 2
            fp = tmp / f"chk_{i}.png"
            subprocess.run(["ffmpeg", "-y", "-v", "error", "-ss", f"{mid:.2f}",
                            "-i", str(output_path), "-frames:v", "1", str(fp)], check=True)
            im = _I.open(fp).convert("RGB")
            w_, h_ = im.size
            px = im.load()
            light = sum(1 for y in range(0, h_, 2) for x in range(0, w_, 2)
                        if sum(px[x, y]) / 3 > 170)
            dark_run_best = 0
            for y in range(0, h_, 6):
                cur = best = 0
                for x in range(w_):
                    if sum(px[x, y]) / 3 < 65:
                        cur += 1
                        best = max(best, cur)
                    else:
                        cur = 0
                dark_run_best = max(dark_run_best, best)
            ok = light > 400 and dark_run_best > w_ * 0.5
            checks.append({"comment": i + 1, "t": round(mid, 1),
                           "light_px": light, "card_run": dark_run_best, "ok": ok})
            if not quiet:
                print(f"    dogrulama y{i+1} @{mid:.1f}s: light={light} kart_genislik={dark_run_best} -> {'OK' if ok else 'HATA'}")
        if not all(c_["ok"] for c_ in checks):
            raise RuntimeError(f"kart dogrulamasi basarisiz: {checks}")

    # temp temizle
    for f in tmp.glob("*"):
        try:
            f.unlink()
        except Exception:
            pass
    try:
        tmp.rmdir()
    except Exception:
        pass
    return output_path, checks


# ----------------------------------------------------------------------------
# Runner
# ----------------------------------------------------------------------------
LINE_RE = re.compile(r"^\[(\d{1,2}):(\d{2})\]\s*(@[^:\s]+):\s*(.*)$")


def parse_comments(txt_path, likes_by_user=None):
    likes_by_user = likes_by_user or {}
    out = []
    for raw in txt_path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line:
            continue
        m = LINE_RE.match(line)
        if not m:
            print(f"  [uyari] satir atlandi: {line[:50]!r}")
            continue
        mm, ss, user, text = int(m.group(1)), int(m.group(2)), m.group(2), m.group(3).strip()
        likes = likes_by_user.get(user.lstrip("@")) or "❤"
        out.append({"user": user, "text": text, "likes": str(likes), "ts": mm * 60 + ss})
    return out


def load_likes():
    mapping = {}
    try:
        cc = json.loads((VIDEO_DIR / "commentcurator_report.json").read_text(encoding="utf-8"))
        for pv in cc.get("per_video", []):
            for c in pv.get("selected", []):
                u = (c.get("user") or "").lstrip("@")
                lk = c.get("likes")
                if u and lk:
                    mapping.setdefault(u, lk)
    except Exception as e:
        print(f"[uyari] likes yuklenemedi: {e}")
    return mapping


async def main():
    only = [int(a) for a in sys.argv[1:] if a.isdigit()]
    indices = only if only else list(range(1, 6))
    likes = load_likes()
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    LOCAL_OUT.mkdir(parents=True, exist_ok=True)
    results, failures = [], []

    for i in indices:
        src_candidates = [LOCAL_DIR / f"vid{i}.mp4", LOCAL_DIR / f"reel_sahnesirasisende_{i}.mp4",
                          VIDEO_DIR / f"reel_sahenesirasisende_{i}.mp4", VIDEO_DIR / f"reel_sahnesirasisende_{i}.mp4"]
        src = next((c for c in src_candidates if c.exists() and c.stat().st_size > 100_000), None)
        txt = VIDEO_DIR / f"reel_sahenesirasisende_{i}_comments.txt"
        if not src or not txt.exists():
            failures.append({"video": i, "error": f"kaynak yok: src={src} txt={txt.exists()}"})
            continue
        comments = parse_comments(txt, likes)
        if not comments:
            failures.append({"video": i, "error": "yorum dosyasi bos"})
            continue
        voice = "tr-TR-AhmetNeural" if i % 2 == 0 else "tr-TR-EmelNeural"
        local_out = LOCAL_OUT / f"yorumlu_reel_sahenesirasisende_{i}.mp4"
        final_out = OUT_DIR / f"yorumlu_reel_sahenesirasisende_{i}.mp4"
        print(f"\n### Video {i}: {src.name} | {len(comments)} yorum | {voice}")
        try:
            _, checks = await edit_v2(src, local_out, comments, voice=voice)
            # Desktop'a retry'li kopyala
            last = None
            for _ in range(10):
                try:
                    shutil.copy2(local_out, final_out)
                    if os.path.getsize(final_out) == os.path.getsize(local_out):
                        break
                except Exception as e:
                    last = e
                import time
                time.sleep(2)
            else:
                raise last or RuntimeError("kopyalama basarisiz")
            dur, = [probe_video(final_out)[2]]
            results.append({
                "input": str(src), "output": str(final_out),
                "size_mb": round(os.path.getsize(final_out) / 1024 / 1024, 2),
                "duration_s": round(dur, 2), "comments_used": len(comments),
                "voice": voice, "card_checks": checks, "verified": True,
            })
            print(f"  ✔ {final_out.name} ({results[-1]['size_mb']} MB, {dur:.1f}s)")
        except Exception as e:
            print(f"  !!! Video {i} HATA: {e}")
            failures.append({"video": i, "error": str(e)})

    status = "success" if len(results) == len(indices) and not failures else ("partial" if results else "failed")
    report = {"status": status, "rendered": len(results), "outputs": results, "failures": failures,
              "notes": "v2 motor: olculmus metin sarma, renkli emoji, kalin kullanici adi + rozet, "
                       "golgeli cam kart, -loop 1 kart girisi, alt-merkez konum. Her yorum penceresi "
                       "icin kare piksel dogrulamasi (kart genisligi + metin pikselleri) yapildi."}
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n==== RAPOR: {REPORT} ====")
    print(json.dumps({"status": status, "rendered": len(results), "failures": failures},
                     ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
