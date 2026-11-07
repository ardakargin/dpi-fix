# DPI Fix — Yapay Zekâ Görsel Promptları

Site artık **Stitch ile üretilmiş** tasarımı kullanıyor (`index.html`). Bu tasarımın tüm görselleri
CSS ve Material Symbols ikonlarıyla çizildi — yani fotoğraf/illüstrasyon gerekmiyor.
Gerçekten dosya olarak gereken yalnızca ikisi:

| Dosya | Boyut / oran | Durum |
|---|---|---|
| `favicon.png` | 512×512, koyu zemin | **Gerekli** — `<link rel="icon">` |
| `og.png` | 1200×630 (1.91:1) | **Gerekli** — sosyal medya paylaşım kartı |
| `logo.png` | 512×512, şeffaf | Opsiyonel — menüdeki gömülü ikonu değiştirmek istersen |
| `hero.png` | 1600×1200 (4:3) | Opsiyonel — hero'daki canlı arayüz kartını görselle değiştirmek istersen |
| `shot.png` | 1600×1000 (16:10) | Opsiyonel — "Nasıl çalışır" altına ekran görüntüsü eklemek istersen |

Dosyaları **bu isimlerle** `site/assets/` içine atın. Eksikse site bozulmaz.

**Marka paleti:** arka plan `#080b10`, yüzey `#111a24`, çizgi `#1e2a38`, ana renk turkuaz
`#2dd4bf`, ikincil gök mavisi `#38bdf8`, uyarı amber `#f59e0b`. Tüm görsellerde bu paleti koruyun.

---

## 0. Önce: logo (opsiyonel, marka için)

```
Minimal geometric app icon, 512x512, transparent background. A stylized shield made of
two overlapping angular planes in teal (#2dd4bf) and sky blue (#38bdf8), with a single
amber (#f59e0b) arrow piercing horizontally through the shield and emerging clean on the
other side, leaving a thin gap where it passes. Flat vector style, subtle inner gradient,
2px darker outline for definition at small sizes, no text, no letters, centered, generous
padding (12% margin), crisp edges, high contrast on dark backgrounds, app store icon quality.
```

**Negatif prompt:** `text, letters, words, watermark, photo, 3d render, glossy ball, drop shadow on background, gradient background, complex detail, thin hairlines, low contrast`

> İpucu: Sadece 3 kelimeyle anlatılan bir metafor (kalkan + ok) küçük boyutta okunur kalır.
> Detay eklerseniz 32px'te kaybolur.

---

## 2. Favicon

Logo ile aynı görseli kullanın — sadece 512×512'ye indirip kenarlardan %10 kırpın.
Ayırt edilebilirlik için arka planı şeffaf değil **koyu** yapmak daha iyi sonuç verir:

```
Same shield-and-arrow mark as the app icon, 512x512, but on a solid dark rounded-square
background (#080b10) with 22% corner radius. Mark fills 68% of the canvas, centered.
Flat vector, no text.
```

---

## 3. Hero görseli (ana sayfa)

```
Isometric tech illustration, 4:3, dark navy background (#080b10). A translucent glass
pipeline runs horizontally across the scene carrying glowing teal data packets; in the
middle a grey inspection gate ("DPI scanner") tries to read them, but the packets split
into two thinner parallel streams to slip past it and rejoin cleanly on the other side.
Soft sky-blue rim light, amber accent glow where the split happens, subtle grid floor,
volumetric haze, floating thin circuit traces in the background. Clean modern vector-
isometric style, limited palette (teal, sky blue, amber, dark grey), no text, no logos,
no human figures, high detail but readable at 800px wide.
```

**Negatif:** `text, letters, UI screenshots, photorealistic people, clutter, rainbow colors, neon pink, cartoon mascot`

---

## 4. Uygulama ekran görüntüsü mockup

```
Clean product mockup, 16:10, dark theme desktop application window floating over a very
dark background (#05070a). The window is a simple utility panel: a bold title, a green
"running" status line, four flat buttons in one row, three small checkboxes, and a large
monospace console-style log area with a few teal log lines and one amber warning line.
Rounded 12px corners, 1px subtle border, soft ambient shadow, slight 8-degree perspective
tilt, generous negative space around the window. Flat UI, Segoe UI style typography placeholders
as abstract grey bars (do not render real words).
```

**Negatif:** `readable text, real words, gibberish letters, light theme, macOS window, phone, multiple windows, reflections, busy background`

> Not: Metin üretmeyin — modeller yazıyı bozar. Gerçek ekran görüntüsünü uygulamadan almak
> her zaman daha iyi: `Win+Shift+S` ile `DpiFix.exe` penceresini kesip `shot.png` yapın.

---

## 5. Sosyal medya kartı (OG)

```
Wide social share card, 1200x630, dark background (#080b10) with a soft radial teal glow
in the lower-left. Left half: large abstract headline bars (one long teal bar, two shorter
grey bars) representing text, with a small amber underscore accent. Right half: the
shield-and-arrow mark from the app icon, large, floating with a subtle glow. Thin dotted
grid lines in the background, generous margins, flat modern tech-branding style, no
readable text.
```

**Negatif:** `readable text, lorem ipsum, clause, stock photo, gradient mesh rainbow, drop shadow text`

---

## Ortak ayarlar

- **Model:** Midjourney v6+ / DALL·E 3 / Flux / Ideogram (logo için Flux veya Ideogram en iyi çıkar)
- **Oran:** Midjourney'de `--ar 4:3` (hero), `--ar 16:10` (shot), `--ar 1.91:1` (og), `--ar 1:1` (logo)
- **Stil kilidi:** Midjourney'de `--style raw --s 150` ile fazla süslemeyi kısın
- **Şeffaflık:** Midjourney şeffaf PNG vermez. Logo için `--ar 1:1` üretip
  [remove.bg](https://remove.bg) veya Photoshop ile arka planı silin.
- **Tutarlılık:** Önce logoyu üretin, sonra diğer promptlarda aynı palet kodlarını koruyun.
- **Yazı eklemeyin:** Hiçbir promptta metin istemeyin; başlıklar HTML'de zaten var.

## Üretim sonrası kontrol listesi

1. Logo 32×32'ye küçültüldüğünde hâlâ okunuyor mu?
2. Görsel koyu arka planda (`#080b10`) kenarları "kesilmiş" görünmüyor mu?
3. Hero'da turkuaz dışında baskın renk var mı? (olmamalı)
4. Dosya boyutları: hero &lt; 400 KB, diğerleri &lt; 200 KB (WebP'ye çevirmek en iyisi)