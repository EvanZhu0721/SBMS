# Real-image corpus

Downloaded on 2026-10-20 for the adaptive-defringe evaluation. These are pre-existing published assets, not adversarial drawings made for this experiment. The downloaded files are unmodified. Test-time resizing and ordinary light/dark background compositing are recorded separately by the evaluation harness.

`manifest.json` contains each original download URL, source page, attribution, background, and requested long-edge size. The corpus has 9 icons and 3 photographs.

| File | Source and attribution | Rights / provenance |
|---|---|---|
| `firefox.png` | [Mozilla Firefox official source tree](https://github.com/mozilla-firefox/firefox/blob/main/browser/branding/official/content/about-logo.png), 192 × 192 RGBA | Official Firefox Browser artwork. Firefox is a trademark of the Mozilla Foundation in the US and other countries; [Mozilla Trademark Guidelines](https://www.mozilla.org/en-US/foundation/trademarks/policy/). |
| `vlc.svg` | [VLC official website](https://www.videolan.org/vlc/), [original SVG](https://www.videolan.org/images/icons-VLC/vlc.svg), 680 × 770 | Official website asset. The [VideoLAN legal page](https://www.videolan.org/legal.html) credits Richard Øiestad for the cone and identifies it as a trademark. Its footer says some icons use [CC BY-SA 3.0+](https://creativecommons.org/licenses/by-sa/3.0/us/), but does not individually assign a license to this file; do not infer the software GPL applies to this logo. |
| `git.svg` | [Git official logo downloads](https://git-scm.com/community/logos), orange logomark, viewBox 78 × 78 | Jason Long; [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). |
| `vscode.png` | [Visual Studio Code official brand page](https://code.visualstudio.com/brand), [original PNG](https://code.visualstudio.com/assets/branding/code-stable.png), 1024 × 1024 RGBA | Microsoft trademark, all rights reserved; official brand guidelines apply. |
| `flag-us.svg` | [Twemoji v14.0.2, US flag](https://github.com/twitter/twemoji/blob/v14.0.2/assets/svg/1f1fa-1f1f8.svg) | Twitter, Inc and other contributors; CC BY 4.0. |
| `flag-gb.svg` | [Twemoji v14.0.2, UK flag](https://github.com/twitter/twemoji/blob/v14.0.2/assets/svg/1f1ec-1f1e7.svg) | Twitter, Inc and other contributors; CC BY 4.0. |
| `rainbow-flag.svg` | [Twemoji v14.0.2, rainbow flag](https://github.com/twitter/twemoji/blob/v14.0.2/assets/svg/1f3f3-fe0f-200d-1f308.svg) | Twitter, Inc and other contributors; CC BY 4.0. |
| `traffic-light.svg` | [Twemoji v14.0.2, vertical traffic light](https://github.com/twitter/twemoji/blob/v14.0.2/assets/svg/1f6a6.svg) | Twitter, Inc and other contributors; CC BY 4.0. |
| `pizza.svg` | [Twemoji v14.0.2, pizza](https://github.com/twitter/twemoji/blob/v14.0.2/assets/svg/1f355.svg) | Twitter, Inc and other contributors; CC BY 4.0. |
| `astronaut.png` | [scikit-image v0.18.3 original asset](https://github.com/scikit-image/scikit-image/blob/v0.18.3/skimage/data/astronaut.png), 512 × 512 RGB | NASA photograph of Eileen Collins; public domain per the [scikit-image data documentation](https://scikit-image.org/docs/stable/api/skimage.data.html#skimage.data.astronaut). |
| `coffee.png` | [scikit-image v0.18.3 original asset](https://github.com/scikit-image/scikit-image/blob/v0.18.3/skimage/data/coffee.png), 600 × 400 RGB | Rachel Michetti, courtesy of Pikolo Espresso Bar; CC0 per the [scikit-image data documentation](https://scikit-image.org/docs/stable/api/skimage.data.html#skimage.data.coffee). |
| `chelsea.png` | [scikit-image v0.18.3 original asset](https://github.com/scikit-image/scikit-image/blob/v0.18.3/skimage/data/chelsea.png), 451 × 300 RGB | Stefan van der Walt; CC0 per the [scikit-image data documentation](https://scikit-image.org/docs/stable/api/skimage.data.html#skimage.data.chelsea). |

Twemoji graphics use [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); see the [v14.0.2 graphics license](https://github.com/twitter/twemoji/blob/v14.0.2/LICENSE-GRAPHICS). The flag drawings are Twemoji's published emoji designs, not official government flag artwork.

The four product icons identify their respective products as test samples. This experiment is not affiliated with or endorsed by those projects.

PNG decoding was checked; all SVGs parsed as XML and contain no external `href` dependencies. Transparency is preserved in the original files. The photos are opaque, so each uses only one background in the manifest.
