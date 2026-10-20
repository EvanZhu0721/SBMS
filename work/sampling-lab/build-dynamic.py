from pathlib import Path
import base64
import json
import mimetypes


root = Path(__file__).resolve().parent
page = (root / "dynamic.template.html").read_text(encoding="utf-8")
page = page.replace(
    "__FILTER_JS__", (root / "dynamic-filter.js").read_text(encoding="utf-8")
)
page = page.replace("__APP_JS__", (root / "dynamic.js").read_text(encoding="utf-8"))
page = page.replace(
    "__LIBPLACEBO_LICENSE__",
    (root / "LICENSE-libplacebo.txt").read_text(encoding="utf-8"),
)
output = root / "dynamic.html"
manifest = json.loads((root / "assets/corpus/manifest.json").read_text(encoding="utf-8-sig"))
corpus = manifest["assets"]
for asset in corpus:
    asset_path = root / "assets/corpus" / asset["file"]
    mime = mimetypes.guess_type(asset_path.name)[0]
    asset["dataUrl"] = f"data:{mime};base64," + base64.b64encode(asset_path.read_bytes()).decode("ascii")
corpus.append({
    "id": "tiktok", "name": "TikTok", "kind": "icon",
    "sizes": [16, 24, 32, 48, 64, 128], "backgrounds": ["#181818", "#ffffff"],
    "sourceUrl": "https://developers.tiktok.com/docs/en/getting-started-design-guidelines",
    "dataUrl": "data:image/png;base64," + base64.b64encode(
        (root / "assets/TikTok_Icon_Black_Square.png").read_bytes()
    ).decode("ascii"),
})
captures = json.loads((root / "assets/text-capture/manifest.json").read_text(encoding="utf-8-sig"))
for asset in captures["assets"]:
    asset_path = root / "assets/text-capture" / asset["file"]
    asset["dataUrl"] = "data:image/png;base64," + base64.b64encode(asset_path.read_bytes()).decode("ascii")
    corpus.append(asset)
adaptive = json.loads((root / "assets/text-capture/adaptive/manifest.json").read_text(encoding="utf-8-sig"))
for mode, name in [("cleartype", "Windows 文字"), ("grayscale", "Windows 灰度")]:
    variants = [asset for asset in adaptive["assets"] if asset["id"].endswith("-" + mode)]
    for asset in variants:
        asset_path = root / "assets/text-capture/adaptive" / asset["file"]
        asset["dataUrl"] = "data:image/png;base64," + base64.b64encode(asset_path.read_bytes()).decode("ascii")
    corpus.insert(0 if mode == "cleartype" else 1, {
        "id": f"windows-gdi-adaptive-{mode}", "name": name, "kind": "capture",
        "sizes": [asset["width"] for asset in variants], "variants": variants,
    })
dynamic = page.replace("__CORPUS_DATA__", json.dumps(corpus, ensure_ascii=False).replace("</", "<\\/"))
output.write_text(dynamic, encoding="utf-8")
print(f"Built {output} ({output.stat().st_size:,} bytes)")
page = page.replace("__CORPUS_DATA__", "[]")
attack = page.replace(
    '<body data-ready="false">',
    '<body data-ready="false" data-content="attack">',
).replace(
    "<title>自适应去彩边 · 动态对比</title>",
    "<title>真彩色误判 · 攻击样本</title>",
)
attack_output = root / "attack.html"
attack_output.write_text(attack, encoding="utf-8")
print(f"Built {attack_output} ({attack_output.stat().st_size:,} bytes)")

logo = root / "assets" / "TikTok_Icon_Black_Square.png"
real = page.replace(
    '<body data-ready="false">',
    '<body data-ready="false" data-content="real">'
    '<img id="real-logo" hidden alt="" src="data:image/png;base64,'
    + base64.b64encode(logo.read_bytes()).decode("ascii") + '">',
).replace(
    "<title>自适应去彩边 · 动态对比</title>",
    "<title>TikTok · 真实图标攻击样本</title>",
).replace(
    "</style>",
    '[data-content="real"] #motion,[data-content="real"] #speed,'
    '[data-content="real"] #dpi,[data-content="real"] label[for="dpi"]{display:none}'
    '[data-content="real"] .divider-handle{top:88%}'
    '#logo-size{width:80px}</style>',
).replace(
    '<select id="motion"',
    '<select id="logo-size" aria-label="源图标尺寸" title="源图标实际像素尺寸">'
    + "".join(f'<option value="{size}"{" selected" if size == 64 else ""}>{size} px</option>'
              for size in [32, 48, 64, 96, 128])
    + '</select><select id="motion"',
)
start = real.index('<p id="info">')
end = real.index("</p>", start) + len("</p>")
real = real[:start] + (
    '<p id="info">TikTok 官方 1200×1200 PNG，保留原有黑底和红青装饰。'
    '浏览器等比绘制为所选尺寸，两路使用完全相同的源像素。'
    '<span>左：新方案；右：相同方案＋原参数去彩边。8× 特写仅作像素放大。'
    '拖动分隔线查看颜色损失。</span>'
    '<span><a href="https://developers.tiktok.com/docs/en/getting-started-design-guidelines" '
    'target="_blank" rel="noopener">官方素材来源</a> · '
    '<a href="https://sf16-va.tiktokcdn.com/obj/eden-va2/uvzhqeh7nuhd/tt4d/logo-pack.zip" '
    'target="_blank" rel="noopener">原始素材包</a></span></p>'
) + real[end:]
real_output = root / "real-attack.html"
real_output.write_text(real, encoding="utf-8")
print(f"Built {real_output} ({real_output.stat().st_size:,} bytes)")

review = real.replace(
    'data-content="real"', 'data-content="review"'
).replace(
    "<title>TikTok · 真实图标攻击样本</title>",
    "<title>去彩边改进 · 文字与图标</title>",
).replace(
    "</style>",
    '[data-content="review"] #motion,[data-content="review"] #speed,'
    '[data-content="review"] #dpi,[data-content="review"] label[for="dpi"]{display:none}'
    '[data-content="review"] .divider-handle{top:clamp(24px,96%,calc(100% - 25px))}'
    '[data-content="review"] #logo-size{width:67px}'
    '#sample{width:100px}#reference{flex:1}'
    '@media(max-width:440px){#sample{width:83px;font-size:10px}'
    '[data-content="review"] #scenario{width:99px}}'
    '@media(max-width:374px){#sample{width:78px}'
    '[data-content="review"] #scenario{width:84px}'
    '[data-content="review"] #logo-size{width:57px}}</style>',
).replace(
    '<select id="logo-size"',
    '<select id="sample" aria-label="测试内容" title="测试内容">'
    '<option value="both">文字＋图标</option>'
    '<option value="positive">原正面样本</option>'
    '<option value="logo">TikTok</option></select><select id="logo-size"',
).replace(
    '<option value="64" selected>64 px</option>', '<option value="64">64 px</option>'
).replace(
    '<option value="128">128 px</option>', '<option value="128" selected>128 px</option>'
).replace(
    '<div class="settings-row">',
    '<div class="settings-row"><select id="reference" aria-label="对照算法">'
    '<option value="legacy">旧去彩边 ↔ 改进去彩边</option>'
    '<option value="raw">未去彩边 ↔ 改进去彩边</option></select>',
).replace(
    'aria-label="左侧新方案，右侧相同方案加自适应去彩边"',
    'aria-label="左侧对照算法，右侧改进去彩边；左右缩放核相同"',
)
start = review.index('<p id="info">')
end = review.index("</p>", start) + len("</p>")
review = review[:start] + (
    '<p id="info">左旧版，右改进版；设置中可切换未去彩边对照。'
    '<span>新增通道顺序与相邻色带检查。RGB 文字沿用原合成压力样本；'
    'TikTok 使用官方 PNG，相同源像素分别处理。特写仅放大输出像素。</span>'
    '<span>仅靠像素无法区分所有真实细线与文字彩边。静态预处理已缓存，'
    '预览 FPS 不代表实时去彩边成本。</span>'
    '<span><a href="https://developers.tiktok.com/docs/en/getting-started-design-guidelines" '
    'target="_blank" rel="noopener">图标来源</a> · '
    '<a href="real-attack.html">旧版图标测试</a> · '
    '<a href="dynamic.html">动态对比</a></span></p>'
) + review[end:]
review_output = root / "refined.html"
review_output.write_text(review, encoding="utf-8")
print(f"Built {review_output} ({review_output.stat().st_size:,} bytes)")
