#!/usr/bin/env python3

from __future__ import annotations

import datetime as dt
import html
from html.parser import HTMLParser
import json
import re
import shutil
from urllib.parse import urlparse
from pathlib import Path

import markdown


REPO_ROOT = Path(__file__).resolve().parents[1]
PAGES_DIR = REPO_ROOT / "Data" / "Pages"
MEMORY_CORE_DIR = REPO_ROOT / "Data" / "Memories" / "Core"
OUTPUT_DIR = REPO_ROOT / "docs" / "output" / "wiki"

ALLOWED_TAGS = {
  "a",
  "blockquote",
  "br",
  "code",
  "dd",
  "del",
  "dl",
  "dt",
  "em",
  "h1",
  "h2",
  "h3",
  "h4",
  "h5",
  "h6",
  "hr",
  "img",
  "li",
  "ol",
  "p",
  "pre",
  "strong",
  "table",
  "tbody",
  "td",
  "th",
  "thead",
  "tr",
  "ul",
}
ALLOWED_ATTRIBUTES = {
  "a": {"href", "title"},
  "code": {"class"},
  "h1": {"id"},
  "h2": {"id"},
  "h3": {"id"},
  "h4": {"id"},
  "h5": {"id"},
  "h6": {"id"},
  "img": {"alt", "src", "title"},
  "td": {"align", "colspan", "rowspan"},
  "th": {"align", "colspan", "rowspan"},
}
URL_ATTRIBUTES = {"href", "src"}
SAFE_URL_SCHEMES = {"", "http", "https", "mailto"}
SAFE_DATA_IMAGE_PREFIXES = (
  "data:image/gif;base64,",
  "data:image/jpeg;base64,",
  "data:image/png;base64,",
  "data:image/webp;base64,",
)


class SafeHtmlSanitizer(HTMLParser):
  def __init__(self) -> None:
    super().__init__(convert_charrefs=True)
    self._parts: list[str] = []

  def get_html(self) -> str:
    return "".join(self._parts)

  def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
    self._append_tag(tag, attrs, self_closing=False)

  def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
    self._append_tag(tag, attrs, self_closing=True)

  def handle_endtag(self, tag: str) -> None:
    tag = tag.lower()
    if tag in ALLOWED_TAGS:
      self._parts.append(f"</{tag}>")

  def handle_data(self, data: str) -> None:
    self._parts.append(html.escape(data, quote=False))

  def handle_entityref(self, name: str) -> None:
    self._parts.append(f"&{name};")

  def handle_charref(self, name: str) -> None:
    self._parts.append(f"&#{name};")

  def _append_tag(self, tag: str, attrs: list[tuple[str, str | None]], self_closing: bool) -> None:
    tag = tag.lower()
    if tag not in ALLOWED_TAGS:
      return

    rendered_attrs = []
    for name, value in attrs:
      safe = sanitize_attribute(tag, name, value)
      if safe is not None:
        rendered_attrs.append(safe)

    attr_text = f" {' '.join(rendered_attrs)}" if rendered_attrs else ""
    suffix = " /" if self_closing else ""
    self._parts.append(f"<{tag}{attr_text}{suffix}>")


def sanitize_attribute(tag: str, name: str, value: str | None) -> str | None:
  name = name.lower()
  allowed = ALLOWED_ATTRIBUTES.get(tag, set())
  if name not in allowed or value is None:
    return None

  value = value.strip()
  if name in URL_ATTRIBUTES and not is_safe_url(value):
    return None

  return f'{name}="{html.escape(value, quote=True)}"'


def is_safe_url(value: str) -> bool:
  lowered = value.lower()
  if lowered.startswith(SAFE_DATA_IMAGE_PREFIXES):
    return True

  scheme = urlparse(value).scheme.lower()
  return scheme in SAFE_URL_SCHEMES and not lowered.startswith(("javascript:", "vbscript:"))


def sanitize_html(html_body: str) -> str:
  sanitizer = SafeHtmlSanitizer()
  sanitizer.feed(html_body)
  sanitizer.close()
  return sanitizer.get_html()


def to_html_filename(stem: str) -> str:
    safe = re.sub(r"[^a-zA-Z0-9_-]+", "-", stem).strip("-").lower()
    return f"{safe or 'page'}.html"


def rewrite_markdown_links(markdown_text: str) -> str:
    text = re.sub(r"\(([^)]+)\.md\)", r"(\1.html)", markdown_text, flags=re.IGNORECASE)
    return text


def render_html(title: str, markdown_text: str) -> str:
    html_body = sanitize_html(markdown.markdown(
        rewrite_markdown_links(markdown_text),
        extensions=["extra", "tables", "fenced_code", "toc", "sane_lists"],
        output_format="html5",
    ))
    generated_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    safe_title = html.escape(title, quote=True)
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'" />
  <title>{safe_title}</title>
  <style>
    :root {{
      color-scheme: light dark;
      --bg: #0b1020;
      --card: #111933;
      --text: #e8ecf6;
      --link: #8cb4ff;
      --muted: #b8c0d9;
      --border: #2b365e;
    }}
    @media (prefers-color-scheme: light) {{
      :root {{
        --bg: #f7f9ff;
        --card: #ffffff;
        --text: #1b2440;
        --link: #2f5fe9;
        --muted: #5e6787;
        --border: #d8dff7;
      }}
    }}
    body {{
      margin: 0;
      font-family: Inter, Segoe UI, Arial, sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.55;
    }}
    nav {{
      padding: 0.9rem 1.2rem;
      border-bottom: 1px solid var(--border);
      background: var(--card);
      position: sticky;
      top: 0;
    }}
    nav a {{
      margin-right: 1rem;
      color: var(--link);
      text-decoration: none;
      font-weight: 600;
    }}
    main {{
      max-width: 1040px;
      margin: 1.2rem auto 2rem;
      padding: 0 1rem;
    }}
    article {{
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 1.2rem;
    }}
    a {{ color: var(--link); }}
    code {{ padding: 0.1rem 0.3rem; border-radius: 4px; background: rgba(127, 127, 127, 0.2); }}
    pre code {{ display: block; overflow-x: auto; padding: 0.9rem; }}
    table {{ border-collapse: collapse; width: 100%; }}
    th, td {{ border: 1px solid var(--border); padding: 0.45rem; vertical-align: top; }}
    footer {{
      max-width: 1040px;
      margin: 0 auto 2rem;
      padding: 0 1rem;
      color: var(--muted);
      font-size: 0.9rem;
    }}
  </style>
</head>
<body>
  <nav>
    <a href="index.html">Home (README)</a>
    <a href="pages.html">Markdown Wiki Pages</a>
    <a href="structured-memories.html">Structured Wiki Index</a>
  </nav>
  <main>
    <article>
      {html_body}
    </article>
  </main>
  <footer>Generated from repository content on {generated_at}</footer>
</body>
</html>
"""


def build_pages_index(page_outputs: list[tuple[str, str]]) -> str:
    lines = [
        "# Markdown Wiki Pages",
        "",
        "These pages are sourced from `Data/Pages`.",
        "",
    ]
    for title, filename in page_outputs:
        lines.append(f"- [{title}]({filename})")
    return "\n".join(lines)


def build_structured_memories_markdown() -> str:
    rows: list[tuple[str, str, str, str]] = []
    for file_path in sorted(MEMORY_CORE_DIR.glob("*.json")):
        data = json.loads(file_path.read_text(encoding="utf-8"))
        record_id = str(data.get("Id", file_path.stem))
        title = str(data.get("Title", "(untitled)"))
        tags = ", ".join(data.get("Tags", []))
        updated = str(data.get("LastUpdated", ""))
        rows.append((record_id, title, tags, updated))

    lines = [
        "# Structured Wiki Index (`Data/Memories/Core`)",
        "",
        "This index summarizes structured project wiki records used by the app and tests.",
        "",
        "| ID | Title | Tags | Last Updated |",
        "|---|---|---|---|",
    ]
    for record_id, title, tags, updated in rows:
        escaped = [value.replace("|", "\\|") for value in (record_id, title, tags, updated)]
        lines.append(f"| {escaped[0]} | {escaped[1]} | {escaped[2]} | {escaped[3]} |")
    return "\n".join(lines)


def main() -> None:
    if OUTPUT_DIR.exists():
        shutil.rmtree(OUTPUT_DIR)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    readme_text = (REPO_ROOT / "README.md").read_text(encoding="utf-8")
    (OUTPUT_DIR / "index.html").write_text(render_html("MemorySmith Wiki", readme_text), encoding="utf-8")

    page_outputs: list[tuple[str, str]] = []
    for page_file in sorted(PAGES_DIR.glob("*.md")):
        content = page_file.read_text(encoding="utf-8")
        title = page_file.stem
        first_heading = next((line.strip("# ").strip() for line in content.splitlines() if line.startswith("# ")), "")
        if first_heading:
            title = first_heading
        output_name = to_html_filename(page_file.stem)
        (OUTPUT_DIR / output_name).write_text(render_html(title, content), encoding="utf-8")
        page_outputs.append((title, output_name))

    pages_index_md = build_pages_index(page_outputs)
    (OUTPUT_DIR / "pages.html").write_text(render_html("Markdown Wiki Pages", pages_index_md), encoding="utf-8")

    structured_index_md = build_structured_memories_markdown()
    (OUTPUT_DIR / "structured-memories.html").write_text(
        render_html("Structured Wiki Index", structured_index_md),
        encoding="utf-8",
    )

    assets_src = PAGES_DIR / "assets"
    if assets_src.exists():
        shutil.copytree(assets_src, OUTPUT_DIR / "assets", dirs_exist_ok=True)


if __name__ == "__main__":
    main()
