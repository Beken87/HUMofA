import os
import sys
import json
import re
import asyncio
import subprocess
import textwrap
import random
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
if hasattr(sys.stderr, 'reconfigure'):
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')

# Curated viral Turkish internet comments (Yorumcan / Shorts style)
DEFAULT_VIRAL_COMMENTS = [
    [
        {"user": "@mehmet_yilmaz", "text": "Annem: Odanı topla!\nBen: Hayatımı toparlayamadım odayı mı toplayacam?", "likes": "24.5B"},
        {"user": "@sude_naz07", "text": "Hoca: Bu konu sınavda çıkacak.\nBeynim: Acaba karıncalar uyur mu?", "likes": "18.2B"},
        {"user": "@bora_k", "text": "Pazartesi sabahı alarm çaldığında tavanla 15 dakika bakışanlar derneği.", "likes": "31.9B"},
        {"user": "@gamze_celik", "text": "Ders çalışırken gelen aniden evi baştan aşağı temizleme isteği şaka mı?", "likes": "14.1B"},
    ],
    [
        {"user": "@emre_can99", "text": "Garson: Afiyet olsun.\nBen: Teşekkürler sana da.\nO sırada beynim: Kendini imha et.", "likes": "42.8B"},
        {"user": "@melis_ak", "text": "Matematik hocası: X'i bulun.\nBen: Ben daha kendimi bulamadım hocam ne X'i?", "likes": "29.4B"},
        {"user": "@berk_yildiz", "text": "Telefonun şarjı %1 olunca evin en uzak köşesindeki prize koşuşum.", "likes": "35.0B"},
        {"user": "@ece_demir", "text": "Gece 03:00'te gelen 'yarın sabah 07:00'de kalkıp hayatımı düzene sokacağım' motivasyonu.", "likes": "50.1B"},
    ],
    [
        {"user": "@ali_ozan", "text": "Doktor: Günde en az 8 saat uyumalısın.\nBen: Haftada 8 saat uyusam şükrederim.", "likes": "19.7B"},
        {"user": "@zeynep_k", "text": "Sınavda 4 şıktan 2'sini eleyip kalan ikisi arasında %100 yanlış olanı seçme yeteneğim.", "likes": "38.6B"},
        {"user": "@furkan_t", "text": "Kendi kendime para biriktiriyorum sonra acıkıyorum ve bütün birikim dürüm oluyor.", "likes": "45.2B"},
        {"user": "@irem_su", "text": "Müzik dinlerken kafamda çektiğim klip Hollywood bütçesinden daha pahalı.", "likes": "27.3B"},
    ],
    [
        {"user": "@caner_b", "text": "Arkadaşım: Bugün erken uyuyalım.\nBiz saat 04:30'da: Piramitleri uzaylılar mı yaptı acaba?", "likes": "33.4B"},
        {"user": "@selin_ay", "text": "Markete sadece sakız almaya girip 400 TL harcayıp çıkma büyüsü.", "likes": "21.9B"},
        {"user": "@oguz_han", "text": "Kamerada kendimi görünce aynadaki kişinin kim olduğunu sorgulamaya başladım.", "likes": "41.5B"},
        {"user": "@tugce_m", "text": "Pazar gecesi yatarken 'keşke yarın kar yağsa da tatil olsa' duası.", "likes": "28.0B"},
    ],
    [
        {"user": "@burak_kral", "text": "Otobüste göz göze geldiğim kişiyle 3 saniyede kurduğum hayali akrabalık bağı.", "likes": "37.2B"},
        {"user": "@ayse_nur", "text": "Kıyafet dolabım tıklım tıklım ama giyecek tek bir şeyim bile yok.", "likes": "48.9B"},
        {"user": "@kerem_v", "text": "Hoca: Sözlüye kaldırıyorum.\nSınıftaki herkesin aniden yerdeki fayansları incelemeye başlaması.", "likes": "52.3B"},
        {"user": "@hazal_s", "text": "Film izlerken 5 dakika telefona bakıp sonra hiçbir şey anlamayınca baştan başlatışım.", "likes": "26.8B"},
    ]
]


def probe_video(video_path):
    cmd = [
        "ffprobe", "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "stream=width,height,duration:format=duration",
        "-of", "json",
        str(video_path)
    ]
    res = subprocess.run(cmd, capture_output=True, text=True, check=True)
    data = json.loads(res.stdout)
    w = int(data["streams"][0]["width"])
    h = int(data["streams"][0]["height"])
    dur = float(data["streams"][0].get("duration") or data.get("format", {}).get("duration", 30.0))
    return w, h, dur


def get_audio_duration(audio_path):
    cmd = [
        "ffprobe", "-v", "error",
        "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1",
        str(audio_path)
    ]
    res = subprocess.run(cmd, capture_output=True, text=True, check=True)
    return float(res.stdout.strip())


async def generate_speech(text, output_mp3, voice="tr-TR-AhmetNeural"):
    import edge_tts
    # Clean text for speech
    speech_text = text.replace("Annem:", "Annem diyor ki: ").replace("Ben:", "Ben de dedim ki: ").replace("Hoca:", "Hoca diyor ki: ")
    communicate = edge_tts.Communicate(speech_text, voice)
    await communicate.save(output_mp3)


def get_system_font(size):
    font_candidates = [
        "C:\\Windows\\Fonts\\segoeui.ttf",
        "C:\\Windows\\Fonts\\segoeuib.ttf",
        "C:\\Windows\\Fonts\\arial.ttf",
        "C:\\Windows\\Fonts\\arialbd.ttf",
        "segoeui.ttf",
        "arial.ttf"
    ]
    for p in font_candidates:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


def create_comment_card(username, text, likes_str, output_png, target_width=720):
    # Card width proportionally fits target video (e.g. 88% of width)
    card_width = int(target_width * 0.88)
    padding_x = 36
    padding_y = 28
    avatar_size = 52

    font_title = get_system_font(24)
    font_body = get_system_font(26)
    font_footer = get_system_font(20)

    # Word wrap comment text
    content_width = card_width - (padding_x * 2)
    wrapper = textwrap.TextWrapper(width=34)
    lines = []
    for raw_line in text.split("\n"):
        wrapped = wrapper.wrap(raw_line)
        if wrapped:
            lines.extend(wrapped)
        else:
            lines.append("")

    line_height = 36
    text_height = len(lines) * line_height
    card_height = padding_y + avatar_size + 16 + text_height + 40 + padding_y

    # Render translucent rounded rectangle card
    img = Image.new("RGBA", (card_width, card_height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Dark translucent card body
    corner_radius = 24
    draw.rounded_rectangle(
        [(0, 0), (card_width - 1, card_height - 1)],
        radius=corner_radius,
        fill=(18, 22, 32, 235),
        outline=(255, 255, 255, 50),
        width=2
    )

    # User Avatar (Circle with initial)
    avatar_colors = [(239, 68, 68), (59, 130, 246), (16, 185, 129), (245, 158, 11), (139, 92, 246)]
    avatar_color = random.choice(avatar_colors)
    draw.ellipse(
        [(padding_x, padding_y), (padding_x + avatar_size, padding_y + avatar_size)],
        fill=avatar_color
    )
    initial = (username.replace("@", "")[:1] or "K").upper()
    font_avatar = get_system_font(28)
    draw.text((padding_x + 16, padding_y + 8), initial, font=font_avatar, fill=(255, 255, 255, 255))

    # Header: Username + verified badge + time
    draw.text((padding_x + avatar_size + 16, padding_y + 6), username, font=font_title, fill=(255, 255, 255, 255))
    draw.text((padding_x + avatar_size + 16, padding_y + 32), "2 sa. önce", font=font_footer, fill=(160, 170, 185, 255))

    # Body: Comment text
    curr_y = padding_y + avatar_size + 18
    for line in lines:
        draw.text((padding_x, curr_y), line, font=font_body, fill=(245, 247, 250, 255))
        curr_y += line_height

    # Footer: Thumbs up like count + comment icon
    footer_y = curr_y + 10
    draw.text((padding_x, footer_y), f"👍 {likes_str}", font=font_footer, fill=(180, 190, 205, 255))
    draw.text((padding_x + 130, footer_y), "💬 Yanıtla", font=font_footer, fill=(140, 150, 170, 255))

    img.save(output_png, "PNG")
    return card_width, card_height


def fetch_comments_from_youtube(url, max_comments=5):
    try:
        import yt_dlp
        ydl_opts = {
            'getcomments': True,
            'max_comments': max_comments * 3,
            'skip_download': True,
            'extract_flat': True,
            'quiet': True
        }
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=False)
            raw_comments = info.get('comments', [])
            results = []
            for c in raw_comments:
                txt = c.get('text', '').strip()
                if 15 <= len(txt) <= 120 and '\n' not in txt:
                    author = c.get('author', '@kullanici')
                    if not author.startswith('@'): author = f"@{author}"
                    likes = c.get('like_count', random.randint(100, 5000))
                    like_str = f"{(likes/1000):.1f}B" if likes >= 1000 else str(likes)
                    results.append({"user": author[:15], "text": txt, "likes": like_str})
                if len(results) >= max_comments:
                    break
            if len(results) >= 2:
                return results
    except Exception as e:
        print(f"[Uyarı] yt-dlp yorum çekme hatası ({e}); küratörlü komik yorumlar kullanılıyor.")
    return None


async def edit_video_with_comments(video_path, output_path, comments=None, voice="tr-TR-AhmetNeural"):
    video_path = Path(video_path)
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    print(f"\n=======================================================")
    print(f"🎬 Video İşleniyor: {video_path.name}")
    print(f"=======================================================")

    width, height, video_duration = probe_video(video_path)
    print(f"  • Çözünürlük: {width}x{height} | Orijinal Süre: {video_duration:.1f}s")

    if not comments:
        comments = random.choice(DEFAULT_VIRAL_COMMENTS)

    temp_dir = output_path.parent / f"_temp_{video_path.stem}"
    temp_dir.mkdir(parents=True, exist_ok=True)

    audio_files = []
    card_files = []
    card_timings = []  # (start_time, end_time, card_path, audio_path)

    current_start = 0.6  # Start first comment 0.6s after video begins

    for i, c in enumerate(comments):
        # Kuratorlu dosyadan gelen zaman damgasina saygi duy
        ts = c.get("ts")
        if ts is not None:
            current_start = max(current_start, float(ts))
        card_png = temp_dir / f"card_{i}.png"
        voice_mp3 = temp_dir / f"voice_{i}.mp3"

        # 1. Render modern comment card
        create_comment_card(c["user"], c["text"], c["likes"], card_png, target_width=width)
        card_files.append(card_png)

        # 2. Synthesize Turkish speech
        await generate_speech(c["text"], voice_mp3, voice=voice)
        audio_files.append(voice_mp3)

        dur = get_audio_duration(voice_mp3)
        end_time = current_start + dur + 0.3
        card_timings.append((current_start, end_time, card_png, voice_mp3, dur))
        print(f"  • Yorum {i+1} [{c['user']}]: {dur:.1f}s (Zamanlama: {current_start:.1f}s -> {end_time:.1f}s)")
        current_start = end_time + 0.35

        # Stop if we exceed available video length
        if current_start >= (video_duration - 1.0):
            break

    total_target_duration = min(video_duration, current_start + 0.8)
    print(f"  • Toplam Yorumlu Video Süresi: {total_target_duration:.1f}s")

    # 3. Build FFmpeg command with filter_complex
    # Inputs: 0 = background video, 1..N = voice tracks, N+1..2N = card images
    cmd = ["ffmpeg", "-y", "-i", str(video_path)]

    for _, _, _, audio_p, _ in card_timings:
        cmd.extend(["-i", str(audio_p)])

    for _, _, card_p, _, _ in card_timings:
        cmd.extend(["-i", str(card_p)])

    num_comments = len(card_timings)
    filter_complex = []

    # Video overlay filters
    # Cards are positioned centered horizontally, lower-middle vertically (y = H/2 - card_h/2)
    last_v = "0:v"
    for i, (st, et, _, _, _) in enumerate(card_timings):
        card_input_idx = 1 + num_comments + i
        next_v = f"v{i+1}"
        filter_complex.append(
            f"[{last_v}][{card_input_idx}:v]overlay=(W-w)/2:(H-h)/2:enable='between(t,{st:.2f},{et:.2f})'[{next_v}]"
        )
        last_v = next_v

    # Audio mixing filters: background music ducked to 15%, each TTS track delayed and merged
    # Background audio
    filter_complex.append("[0:a]volume=0.15[bg_audio]")
    delayed_audios = ["[bg_audio]"]

    for i, (st, _, _, _, _) in enumerate(card_timings):
        audio_input_idx = 1 + i
        delay_ms = int(st * 1000)
        out_a = f"a{i+1}"
        filter_complex.append(f"[{audio_input_idx}:a]adelay={delay_ms}|{delay_ms},volume=1.35[{out_a}]")
        delayed_audios.append(f"[{out_a}]")

    all_inputs_str = "".join(delayed_audios)
    filter_complex.append(f"{all_inputs_str}amix=inputs={len(delayed_audios)}:duration=longest[final_audio]")

    filter_str = ";".join(filter_complex)

    cmd.extend([
        "-filter_complex", filter_str,
        "-map", f"[{last_v}]",
        "-map", "[final_audio]",
        "-t", f"{total_target_duration:.2f}",
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "21",
        "-c:a", "aac",
        "-b:a", "192k",
        "-movflags", "+faststart",
        str(output_path)
    ])

    print("  • FFmpeg montaj ve render işlemi başlatılıyor...")
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        print(f"❌ FFmpeg Hatası:\n{res.stderr[-800:]}")
        raise RuntimeError("FFmpeg render failed.")

    # Clean temporary files
    try:
        for f in temp_dir.glob("*"):
            f.unlink()
        temp_dir.rmdir()
    except Exception:
        pass

    print(f"✔ Başarıyla Tamamlandı: {output_path.name}")
    print(f"  Boyut: {(os.path.getsize(output_path) / 1024 / 1024):.2f} MB")
    return output_path


async def main():
    folder = Path(r"C:\Users\beken\Desktop\Files\SSS_Yorumlu_Video")
    output_folder = folder / "Editlenmişler"
    output_folder.mkdir(parents=True, exist_ok=True)

    video_files = [f for f in folder.glob("*.mp4") if f.is_file()]
    if not video_files:
        print(f"'{folder}' klasöründe MP4 video bulunamadı!")
        return

    print(f"🎬 Toplam {len(video_files)} video bulundu. Otomatik montaj başlatılıyor...")

    # Optional youtube url passed as argument
    youtube_url = sys.argv[1] if len(sys.argv) > 1 else None
    fetched_comments = None
    if youtube_url:
        print(f"🔗 YouTube adresinden yorumlar çekiliyor: {youtube_url}")
        fetched_comments = fetch_comments_from_youtube(youtube_url)

    for i, vid in enumerate(video_files):
        out_file = output_folder / f"yorumlu_{vid.name}"
        # Rotate comment sets or use fetched comments
        cmts = fetched_comments if fetched_comments else DEFAULT_VIRAL_COMMENTS[i % len(DEFAULT_VIRAL_COMMENTS)]
        # Alternate male/female voices for variety
        voice = "tr-TR-AhmetNeural" if i % 2 == 0 else "tr-TR-EmelNeural"
        await edit_video_with_comments(vid, out_file, comments=cmts, voice=voice)

    print("\n🎉 BÜTÜN VİDEOLAR BAŞARIYLA EDİTLENDİ VE HAZIRLANDI!")
    print(f"📁 Çıktı Klasörü: {output_folder}")


if __name__ == "__main__":
    asyncio.run(main())
