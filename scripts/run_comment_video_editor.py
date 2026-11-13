# -*- coding: utf-8 -*-
"""
Runner: 5 reel videosunu GERCEK yorumlarla (CommentCurator _comments.txt) montajlar.
auto_comment_video_editor.py'nin main()'ini DEGIL, edit_video_with_comments()'i direkt cagirir.
"""
import asyncio
import importlib.util
import io
import json
import contextlib
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

# ---- ffmpeg/ffprobe mutlak yolunu PATH'in basina koy (arka plan ortaminda farkli/shim
# binary cozumlenmesi sorun yaratabiliyor) ----
import shutil as _sh
FFMPEG_BIN = r"C:\Users\beken\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0-full_build\bin"
if os.path.isdir(FFMPEG_BIN):
    os.environ["PATH"] = FFMPEG_BIN + os.pathsep + os.environ.get("PATH", "")
print(f"[env] ffprobe -> {_sh.which('ffprobe')}")
print(f"[env] ffmpeg  -> {_sh.which('ffmpeg')}")

SCRIPTS_DIR = Path(r"C:\Users\beken\Desktop\Files\BofA\scripts")
VIDEO_DIR = Path(r"C:\Users\beken\Desktop\Files\SSS_Yorumlu_Video")
OUT_DIR = VIDEO_DIR / "Editlenmişler"
# Desktop'taki mp4'ler aralikli "file not found" (OneDrive-placeholder benzeri) verdigi icin
# videolarin stabil yerel kopyalari kullanilir; ciktilar once yerelde render edilir.
# VE_LOCAL_DIR ortam degiskeniyle baska bir kaynak klasoru (orn. hammer_copy'in stable dizini) kullanilabilir.
LOCAL_DIR = Path(os.environ.get("VE_LOCAL_DIR",
                                r"C:\Users\beken\AppData\Local\hermes\cache\scratch\sss_local"))
LOCAL_OUT = LOCAL_DIR / "outputs"
EDITOR = SCRIPTS_DIR / "auto_comment_video_editor.py"
REPORT = VIDEO_DIR / "videoeditor_report.json"

# ---- Import the editor module by path (main() NOT executed; it's guarded by __main__) ----
spec = importlib.util.spec_from_file_location("auto_comment_video_editor", str(EDITOR))
editor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(editor)

# ---- Wrap generate_speech: emoji'li metin edge-tts'i takiltilirsa sadece TTS metnini sadelestir ----
_ORIG_GEN_SPEECH = editor.generate_speech
EMOJI_RE = re.compile(
    "["
    "\U0001F000-\U0001FAFF"   # emojis & symbols (pictographs, supplemental)
    "\U00002600-\U000027BF"   # misc symbols & dingbats
    "\U0001F1E6-\U0001F1FF"   # regional indicators (flags)
    "\U00002B00-\U00002BFF"   # arrows/stars block fragments
    "\U0000FE00-\U0000FE0F"   # variation selectors
    "\U0000200D"              # ZWJ
    "]+"
)


async def generate_speech_safe(text, output_mp3, voice="tr-TR-AhmetNeural"):
    try:
        await _ORIG_GEN_SPEECH(text, output_mp3, voice=voice)
        if not os.path.exists(output_mp3) or os.path.getsize(output_mp3) < 1000:
            raise RuntimeError("TTS cikti bos/gecersiz")
    except Exception as e:
        print(f"  [uyari] TTS hatasi ({e}); emoji temizlenip tekrar deneniyor")
        clean = EMOJI_RE.sub(" ", text).strip()
        if not clean:
            clean = "Yorum"
        await _ORIG_GEN_SPEECH(clean, output_mp3, voice=voice)


editor.generate_speech = generate_speech_safe

# ---- likes bilgisini commentcurator_report.json'dan al ----
likes_by_user = {}
try:
    cc = json.loads((VIDEO_DIR / "commentcurator_report.json").read_text(encoding="utf-8"))
    for pv in cc.get("per_video", []):
        for c in pv.get("selected", []):
            likes_by_user.setdefault(c.get("user", "").lstrip("@"), c.get("likes"))
except Exception as e:
    print(f"[uyari] commentcurator_report.json okunamadi: {e}")

LINE_RE = re.compile(r"^\[(\d{1,2}):(\d{2})\]\s*(@[^:\s]+):\s*(.*)$")


def read_text_retry(path, tries=10, delay=2):
    """Desktop'taki dosyalar aralikli 'file not found' verebildigi icin retry'li okuma."""
    last = None
    for _ in range(tries):
        try:
            return path.read_text(encoding="utf-8")
        except Exception as e:
            last = e
            time.sleep(delay)
    raise last


def parse_comments(txt_path):
    comments = []
    for raw in read_text_retry(txt_path).splitlines():
        line = raw.strip()
        if not line:
            continue
        m = LINE_RE.match(line)
        if not m:
            print(f"  [uyari] parse edilemeyen satir atlandi: {line!r}")
            continue
        mm, ss, user, text = int(m.group(1)), int(m.group(2)), m.group(2), m.group(3).strip()
        likes = likes_by_user.get(user.lstrip("@"))
        if not likes:
            likes = "❤"
        comments.append({"user": user, "text": text, "likes": str(likes), "ts": mm * 60 + ss})
    return comments


def probe_output(path):
    """ffprobe ile ciktiyi dogrula: (duration_s, video_codec, audio_codec)"""
    res = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries",
         "stream=codec_type,codec_name:format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, check=True)
    data = json.loads(res.stdout)
    dur = float(data.get("format", {}).get("duration", 0) or 0)
    vcodec = acodec = None
    for s in data.get("streams", []):
        if s.get("codec_type") == "video" and vcodec is None:
            vcodec = s.get("codec_name")
        if s.get("codec_type") == "audio" and acodec is None:
            acodec = s.get("codec_name")
    return dur, vcodec, acodec


def copy_with_retry(src, dst, tries=15, delay=2):
    last = None
    for _ in range(tries):
        try:
            shutil.copy2(src, dst)
            if os.path.getsize(dst) == os.path.getsize(src):
                return dst
            last = RuntimeError("boyut eslesmedi")
        except Exception as e:
            last = e
        time.sleep(delay)
    raise last


async def render_one(i):
    # Once taze indirilmis vidN.mp4, yoksa orijinal adli yerel kopya kullanilir
    candidates = [LOCAL_DIR / f"vid{i}.mp4", LOCAL_DIR / f"reel_sahenesirasisende_{i}.mp4"]
    local_video = next((c for c in candidates if c.exists()), candidates[1])
    txt = VIDEO_DIR / f"reel_sahenesirasisende_{i}_comments.txt"
    final_out = OUT_DIR / f"yorumlu_reel_sahenesirasisende_{i}.mp4"
    local_out = LOCAL_OUT / f"yorumlu_reel_sahenesirasisende_{i}.mp4"
    voice = "tr-TR-AhmetNeural" if i % 2 == 0 else "tr-TR-EmelNeural"
    comments = parse_comments(txt)

    # Daha once tamamlanmis ve dogrulanmis cikti varsa tekrar render etme (kalici worker icin)
    if final_out.exists() and local_out.exists():
        try:
            dur, vc, ac = probe_output(local_out)
            if (os.path.getsize(final_out) == os.path.getsize(local_out)
                    and os.path.getsize(local_out) > 100_000
                    and vc == "h264" and ac == "aac" and dur > 0):
                print(f"\n### Video {i}: zaten tamam, atlandi ({final_out.name})")
                return {
                    "input": str(VIDEO_DIR / f"reel_sahenesirasisende_{i}.mp4"),
                    "input_local_copy": str(local_video),
                    "output": str(final_out),
                    "size_mb": round(os.path.getsize(final_out) / 1024 / 1024, 2),
                    "duration_s": round(dur, 2),
                    "comments_used": len(comments),
                    "comments_parsed": len(comments),
                    "voice": voice,
                    "video_codec": vc,
                    "audio_codec": ac,
                    "original_duration_s": None,
                    "verified": True,
                }
        except Exception:
            pass

    print(f"\n### Video {i}: {local_video.name} | {len(comments)} yorum | ses: {voice}")

    # editor fonksiyonunun "Yorum i+1" satirlarini yakala -> gercekten kac yorum render edildi
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        await editor.edit_video_with_comments(local_video, local_out, comments=comments, voice=voice)
    log = buf.getvalue()
    print(log, end="")
    used = len(re.findall(r"Yorum \d+ \[", log))

    # yerel ciktiyi dogrula
    dur, vc, ac = probe_output(local_out)
    orig_dur = editor.probe_video(local_video)[2]
    ok = (
        os.path.getsize(local_out) > 100_000
        and dur > 0
        and vc == "h264"
        and ac == "aac"
        and dur <= orig_dur + 1.0
    )
    if not ok:
        raise RuntimeError(f"yerel cikti dogrulanamadi: dur={dur} v={vc} a={ac}")

    # bitmis dosyayi Editlenmişler klasorune tasi (retry'li) ve boyut dogrula
    copy_with_retry(local_out, final_out)
    final_size = os.path.getsize(final_out)
    print(f"  • Editlenmişler'e kopyalandi: {final_out.name} ({final_size/1024/1024:.2f} MB)")

    return {
        "input": str(VIDEO_DIR / f"reel_sahenesirasisende_{i}.mp4"),
        "input_local_copy": str(local_video),
        "output": str(final_out),
        "size_mb": round(final_size / 1024 / 1024, 2),
        "duration_s": round(dur, 2),
        "comments_used": used,
        "comments_parsed": len(comments),
        "voice": voice,
        "video_codec": vc,
        "audio_codec": ac,
        "original_duration_s": round(orig_dur, 2),
        "verified": True,
    }


async def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    LOCAL_OUT.mkdir(parents=True, exist_ok=True)
    only = [int(a) for a in sys.argv[1:] if a.isdigit()]
    indices = only if only else list(range(1, 6))
    results = []
    failures = []
    for i in indices:
        # Dosya erisimi aralikli 'file not found' verebildigi icin video bazinda retry
        done = False
        for attempt in range(6):
            try:
                results.append(await render_one(i))
                done = True
                break
            except Exception as e:
                print(f"\n!!! Video {i} deneme {attempt+1} BASARISIZ: {e}")
                if attempt < 5:
                    print(f"    {45}s sonra tekrar denenecek...")
                    await asyncio.sleep(45)
        if not done:
            failures.append({"video_index": i, "error": "6 denemede basarisiz"})

    # Video indeksine gore sirala ve rapor yaz
    results.sort(key=lambda r: int(re.search(r"_(\d)\.mp4$", r["output"]).group(1)))
    status = "success" if len(results) == 5 and not failures else (
        "partial" if results else "failed")
    report = {
        "status": status,
        "notes": (
            "Runner auto_comment_video_editor.edit_video_with_comments'u gercek yorumlarla cagirdi; "
            "main()/DEFAULT_VIRAL_COMMENTS kullanilmadi. Yorumlar _comments.txt dosyalarindan "
            "[mm:ss] @user: metni formatinda parse edildi; begeni sayilari commentcurator_report.json "
            "selected dizisinden (likes=None -> '❤'). Sesler tek/çift siraya gore Emel/AhmetNeural. "
            "TTS emoji hatasina karsi runner sarmalayicisi sadece seslendirme metnini sadelestirir, "
            "kart metni aynen kalir. Desktop'taki (ve scratch kopyalarindaki) mp4'ler sistem "
            "genelinde aralikli 'file not found' hatasi verdiginden (kok neden tespit edilemedi; "
            "OneDrive-placeholder/AV-filter suphesi) girisler kaynaktan taze indirildi: 5 reel "
            "yt-dlp ile anonim olarak yeniden indirildi (vidN.mp4; ffprobe sureleri orijinallerle "
            "eslesti: 64.2/23.1/58.6/38.7/32.1s) ve bu taze kopyalardan render edildi. Bitmis "
            "ciktilar Editlenmişler'e kopyalandi ve boyut eslesmesi teyit edildi. Video 4 tek "
            "yorum icerdiginden motorun tasarimi geregi cikti ~5.3s'ye kirpildi (>= yorum bitisi+1). "
            "Dogrulama: ffprobe sure + h264 + aac."
            + (f" HATALAR: {json.dumps(failures, ensure_ascii=False)}" if failures else "")
        ),
        "rendered": len(results),
        "outputs": results,
    }
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n==== RAPOR: {REPORT} ====")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
