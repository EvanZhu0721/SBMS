"""Regenerate the README chart: python scripts/generate-code-history.py [ref]."""

from html import escape
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "assets/readme/code-history.svg"
EXTENSIONS = {".rs", ".c", ".cpp", ".h", ".hpp", ".cs", ".slint", ".hlsl",
              ".wgsl", ".vert", ".frag", ".ps1", ".py", ".js", ".ts", ".tsx", ".iss"}


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode("utf-8")


def is_code(path):
    return (Path(path).suffix.lower() in EXTENSIONS
            and "vendor" not in Path(path).parts
            and path != "scripts/generate-code-history.py")


ref = git("rev-parse", sys.argv[1] if len(sys.argv) > 1 else "HEAD").strip()
history = git("log", "--first-parent", "--reverse", "--format=%H|%cs|%s", ref)
commits = []
balance = 0
previous = None
for line in history.splitlines():
    sha, date, subject = line.split("|", 2)
    # Compare each mainline tree to its predecessor, including merge results once.
    args = ("diff", previous, sha) if previous else ("diff-tree", "--root", "--no-commit-id", "-r", sha)
    stats = git(*args, "--numstat", "--no-renames")
    added = deleted = 0
    for entry in stats.splitlines():
        a, d, path = entry.split("\t", 2)
        if a != "-" and is_code(path):
            added += int(a)
            deleted += int(d)
    commits.append((sha, date, subject, balance, added, deleted))
    balance += added - deleted
    previous = sha

W, H = 1920, 1080
LEFT, RIGHT, TOP, BOTTOM = 48, 1872, 128, 1032
GREEN, RED = "#32d49b", "#ff667d"
peak = max(start + added for _, _, _, start, added, _ in commits)
ceiling = ((peak // 10000) + 1) * 10000
step = (RIGHT - LEFT) / len(commits)
body_width = step * .33


def y(value):
    return BOTTOM - value / ceiling * (BOTTOM - TOP)


svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img" aria-labelledby="title desc">',
       '<title id="title">SBMS code history</title>',
       f'<desc id="desc">Mainline source-code changes, {commits[0][1]} to {commits[-1][1]}. Each commit adds green upward candles then red downward candles. Linear cumulative net lines; equal spacing per commit. Excludes vendor code, non-code files and this chart generator. Snapshot {ref}.</desc>',
       f'<rect width="{W}" height="{H}" rx="24" fill="#10161e"/>',
       '<g font-family="Segoe UI,Arial,sans-serif">']


def text(x, yy, content, size=16, fill="#8897a8", extra=""):
    svg.append(f'<text x="{x}" y="{yy}" font-size="{size}" fill="{fill}" {extra}>{escape(str(content))}</text>')


total_added = sum(c[4] for c in commits)
total_deleted = sum(c[5] for c in commits)
text(48, 76, f"+{total_added:,}", 40, GREEN, 'font-weight="600"')
text(315, 76, f"−{total_deleted:,}", 40, RED, 'font-weight="600"')

for i in range(5):
    value = ceiling * i / 4
    yy = y(value)
    svg.append(f'<path d="M{LEFT} {yy:.2f}H{RIGHT}" stroke="#26313e" stroke-dasharray="3 6"/>')

for i, (sha, date, subject, start, added, deleted) in enumerate(commits):
    x = LEFT + (i + .5) * step
    end = start + added - deleted
    svg.append('<g>')
    if i:
        svg.append(f'<path d="M{x-step+step*.19:.2f} {y(start):.2f}H{x-step*.19:.2f}" stroke="#536272" stroke-width=".8"/>')
    for xx, low, high, color, amount in (
        (x - step * .19, start, start + added, GREEN, added),
        (x + step * .19, end, start + added, RED, deleted),
    ):
        if amount:
            # Bodies encode actual line counts; no invented high/low wicks.
            height = y(low) - y(high)
            svg.append(f'<rect x="{xx-body_width/2:.2f}" y="{y(high):.2f}" width="{body_width:.2f}" height="{height:.3f}" fill="{color}"/>')
    svg.append('</g>')

svg.extend(['</g>', '</svg>'])
OUTPUT.write_text("\n".join(svg) + "\n", encoding="utf-8")
print(f"{OUTPUT}: {len(commits)} commits, +{total_added:,} / -{total_deleted:,}, net {balance:,}")
