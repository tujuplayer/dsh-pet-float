"""生成插件自带的默认素材（不引入任何第三方版权素材）。

pet.png : 512x512 透明底的小鱼娘替代形象（纯代码绘制，CC0）
ah.wav  : 约 0.55s 的「啊」提示音（共振峰合成，CC0）

用法：python scripts/gen-assets.py
"""
import math
import os
import struct
import wave

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(os.path.dirname(HERE), "assets")


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def gen_pet(path):
    S = 512
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = S / 2, S / 2 + 8

    # 尾巴（先画，被身体盖住一部分）
    d.polygon(
        [(cx + 150, cy - 40), (cx + 255, cy - 96), (cx + 236, cy + 6),
         (cx + 258, cy + 96), (cx + 148, cy + 52)],
        fill=(120, 178, 236, 255),
    )
    # 身体：竖直渐变（上浅下深）
    body = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(body)
    top = (168, 214, 250)
    bottom = (86, 146, 220)
    steps = 220
    for i in range(steps):
        t = i / (steps - 1)
        y0 = int(cy - 170 + (340 * i / steps))
        y1 = int(cy - 170 + (340 * (i + 1) / steps)) + 1
        bd.ellipse([cx - 190, cy - 190, cx + 190, cy + 190], fill=lerp(top, bottom, t) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([cx - 172, cy - 172, cx + 172, cy + 172], fill=255)
    img.paste(body, (0, 0), mask)

    # 肚子（浅色）
    d.ellipse([cx - 96, cy + 10, cx + 96, cy + 150], fill=(232, 244, 255, 235))
    # 眼睛
    for ex in (cx - 62, cx + 62):
        d.ellipse([ex - 30, cy - 62, ex + 30, cy + 2], fill=(255, 255, 255, 255))
        d.ellipse([ex - 18, cy - 48, ex + 18, cy - 6], fill=(38, 58, 96, 255))
        d.ellipse([ex - 8, cy - 42, ex + 2, cy - 30], fill=(255, 255, 255, 240))
    # 腮红
    for bx in (cx - 118, cx + 118):
        d.ellipse([bx - 26, cy + 4, bx + 26, cy + 38], fill=(255, 168, 186, 150))
    # 嘴
    d.arc([cx - 26, cy + 34, cx + 26, cy + 74], start=20, end=160, fill=(120, 90, 110, 235), width=7)
    # 侧鳍
    d.polygon([(cx - 168, cy + 4), (cx - 214, cy + 66), (cx - 150, cy + 76)], fill=(120, 178, 236, 235))
    d.polygon([(cx + 168, cy + 4), (cx + 214, cy + 66), (cx + 150, cy + 76)], fill=(120, 178, 236, 235))
    # 高光
    hi = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(hi).ellipse([cx - 96, cy - 168, cx + 40, cy - 118], fill=(255, 255, 255, 90))
    img = Image.alpha_composite(img, hi.filter(ImageFilter.GaussianBlur(9)))
    img.save(path)
    print("wrote", path, os.path.getsize(path), "bytes")


def gen_ah(path):
    sr = 44100
    dur = 0.55
    n = int(sr * dur)
    f0_a, f0_b = 196.0, 168.0            # 音高：先高后低，像一声「啊——」
    formants = [(760.0, 90.0, 1.0), (1220.0, 120.0, 0.6), (2600.0, 220.0, 0.25), (3400.0, 300.0, 0.12)]
    frames = bytearray()
    phase = 0.0
    for i in range(n):
        t = i / n
        f0 = f0_a + (f0_b - f0_a) * t + 2.5 * math.sin(2 * math.pi * 5.5 * i / sr)
        phase += 2 * math.pi * f0 / sr
        # 声门波：锯齿基频的谐波叠加，避免爆音
        src = 0.0
        for h in range(1, 24):
            amp = 1.0 / (h ** 1.35)
            if h * f0 > sr / 2 - 200:
                break
            src += amp * math.sin(h * phase)
        # 共振峰：用二阶带通近似（对每个谐波按距离加权更省事）
        v = 0.0
        for freq, bw, gain in formants:
            d = (h * f0 - freq) / bw if False else 0.0
        # 逐谐波加共振峰权重
        v = 0.0
        for h in range(1, 24):
            fh = h * f0
            if fh > sr / 2 - 200:
                break
            w = 0.0
            for freq, bw, gain in formants:
                w += gain / (1.0 + ((fh - freq) / bw) ** 2)
            v += (1.0 / (h ** 1.2)) * w * math.sin(h * phase)
        # 包络：30ms 起音、100ms 保持、尾部衰减
        if t < 0.05:
            env = t / 0.05
        elif t < 0.62:
            env = 1.0
        else:
            env = max(0.0, (1.0 - t) / 0.38)
        trem = 1.0 + 0.06 * math.sin(2 * math.pi * 7.0 * i / sr)
        s = 0.30 * env * trem * v
        s = max(-1.0, min(1.0, s))
        frames += struct.pack("<h", int(round(s * 32767 * 0.9)))
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(bytes(frames))
    print("wrote", path, os.path.getsize(path), "bytes")


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    gen_pet(os.path.join(ASSETS, "pet.png"))
    gen_ah(os.path.join(ASSETS, "ah.wav"))
