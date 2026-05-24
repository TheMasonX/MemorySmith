#!/usr/bin/env python3

from __future__ import annotations

import argparse
import datetime as dt
import html
from html.parser import HTMLParser
import json
import os
import posixpath
import re
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
import hashlib
from pathlib import Path, PurePosixPath
from urllib.parse import urlparse

import markdown


REPO_ROOT = Path(__file__).resolve().parents[1]
PAGES_DIR = REPO_ROOT / "Data" / "Pages"
MEMORY_CORE_DIR = REPO_ROOT / "Data" / "Memories" / "Core"
OUTPUT_DIR = REPO_ROOT / "docs" / "output" / "wiki"
MERMAID_OUTPUT_DIR = Path("assets") / "mermaid"
MERMAID_BLOCK_PATTERN = re.compile(r"```mermaid\s*\n(.*?)```", flags=re.IGNORECASE | re.DOTALL)

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


def parse_bool_env(name: str) -> bool:
  value = os.environ.get(name, "").strip().lower()
  return value in {"1", "true", "yes", "on"}


def find_mermaid_command() -> list[str] | None:
  mmdc = shutil.which("mmdc")
  if mmdc:
    return [mmdc]

  npx = shutil.which("npx")
  if npx:
    return [npx, "--yes", "@mermaid-js/mermaid-cli@10.9.1"]

  return None


@dataclass
class BuildOptions:
  export_mermaid_svg: bool


@dataclass
class MermaidExportContext:
  options: BuildOptions
  command: list[str] | None
  exported_count: int = 0
  skipped_count: int = 0


def get_mermaid_export_context(options: BuildOptions) -> MermaidExportContext:
  command = find_mermaid_command() if options.export_mermaid_svg else None
  return MermaidExportContext(options=options, command=command)


def export_mermaid_svg(
  context: MermaidExportContext,
  diagram_text: str,
  source_relative_path: Path,
  current_output_relative_path: Path,
  diagram_index: int,
) -> str | None:
  if not context.options.export_mermaid_svg:
    return None

  if context.command is None:
    context.skipped_count += 1
    return None

  source_stem = source_relative_path.with_suffix("")
  source_hash = hashlib.sha1(diagram_text.encode("utf-8")).hexdigest()[:10]
  svg_relative_path = MERMAID_OUTPUT_DIR / source_stem / f"diagram-{diagram_index:02d}-{source_hash}.svg"
  svg_output_path = OUTPUT_DIR / svg_relative_path
  svg_output_path.parent.mkdir(parents=True, exist_ok=True)

  with tempfile.NamedTemporaryFile(mode="w", suffix=".mmd", encoding="utf-8", delete=False) as input_file:
    input_file.write(diagram_text)
    input_path = Path(input_file.name)

  with tempfile.NamedTemporaryFile(mode="w", suffix=".json", encoding="utf-8", delete=False) as config_file:
    config_file.write('{"args":["--no-sandbox","--disable-setuid-sandbox"]}')
    config_path = Path(config_file.name)

  try:
    command = [
      *context.command,
      "-i",
      str(input_path),
      "-o",
      str(svg_output_path),
      "-p",
      str(config_path),
      "--quiet",
    ]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    if completed.returncode != 0:
      context.skipped_count += 1
      stderr = (completed.stderr or "").strip()
      if stderr:
        print(f"Warning: Mermaid export failed for {source_relative_path.as_posix()} diagram {diagram_index}: {stderr}")
      return None
  finally:
    input_path.unlink(missing_ok=True)
    config_path.unlink(missing_ok=True)

  context.exported_count += 1
  return compute_relative_href(current_output_relative_path, svg_relative_path)


def to_output_relative_path(source_relative_path: Path) -> Path:
  return Path("pages") / source_relative_path.with_suffix(".html")


def compute_relative_href(from_output_relative_path: Path, to_output_relative_path: Path) -> str:
  return posixpath.relpath(to_output_relative_path.as_posix(), from_output_relative_path.parent.as_posix())


def rewrite_markdown_links(
  markdown_text: str,
  current_source_relative_path: Path,
  source_to_output_paths: dict[str, Path],
  current_output_relative_path: Path,
) -> str:
  markdown_link_pattern = re.compile(r"\(([^)\s]+)\)")

  def replace_link(match: re.Match[str]) -> str:
    target = match.group(1)
    if target.startswith(("http://", "https://", "mailto:", "data:", "#")):
      return f"({target})"

    parsed_target = urlparse(target)
    path_part = parsed_target.path
    if not path_part.lower().endswith(".md"):
      return f"({target})"

    current_source_posix = PurePosixPath(current_source_relative_path.as_posix())

    if path_part.startswith("/"):
      source_candidate = PurePosixPath(path_part.lstrip("/"))
      parts = list(source_candidate.parts)
      if len(parts) >= 2 and parts[0].lower() == "data" and parts[1].lower() == "pages":
        source_candidate = PurePosixPath(*parts[2:])
    else:
      source_candidate = current_source_posix.parent / PurePosixPath(path_part)

    normalized_source = PurePosixPath(source_candidate).as_posix()
    mapped_output_path = source_to_output_paths.get(normalized_source)
    if mapped_output_path is None:
      fallback_path = f"{path_part[:-3]}.html"
      rebuilt = parsed_target._replace(path=fallback_path).geturl()
      return f"({rebuilt})"

    rewritten_path = compute_relative_href(current_output_relative_path, mapped_output_path)
    rebuilt = parsed_target._replace(path=rewritten_path).geturl()
    return f"({rebuilt})"

  return markdown_link_pattern.sub(replace_link, markdown_text)


def rewrite_mermaid_blocks(
  markdown_text: str,
  context: MermaidExportContext,
  source_relative_path: Path,
  current_output_relative_path: Path,
) -> str:
  diagram_counter = 0

  def replace_mermaid(match: re.Match[str]) -> str:
    nonlocal diagram_counter
    diagram_counter += 1
    diagram_source = match.group(1).strip()
    exported_href = export_mermaid_svg(
      context,
      diagram_source,
      source_relative_path,
      current_output_relative_path,
      diagram_counter,
    )
    if exported_href is None:
      return match.group(0)

    return f"![Mermaid diagram {diagram_counter}]({exported_href})"

  return MERMAID_BLOCK_PATTERN.sub(replace_mermaid, markdown_text)


def render_html(
  title: str,
  markdown_text: str,
  current_source_relative_path: Path,
  source_to_output_paths: dict[str, Path],
  current_output_relative_path: Path,
  mermaid_context: MermaidExportContext,
) -> str:
    markdown_with_mermaid = rewrite_mermaid_blocks(
        markdown_text,
        mermaid_context,
        current_source_relative_path,
        current_output_relative_path,
    )
    html_body = sanitize_html(markdown.markdown(
    rewrite_markdown_links(
      markdown_with_mermaid,
      current_source_relative_path,
      source_to_output_paths,
      current_output_relative_path,
    ),
        extensions=["extra", "tables", "fenced_code", "toc", "sane_lists"],
        output_format="html5",
    ))
    generated_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    safe_title = html.escape(title, quote=True)
    home_href = compute_relative_href(current_output_relative_path, Path("index.html"))
    pages_href = compute_relative_href(current_output_relative_path, Path("pages.html"))
    memories_href = compute_relative_href(current_output_relative_path, Path("structured-memories.html"))
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
    <a href="{home_href}">Home (README)</a>
    <a href="{pages_href}">Markdown Wiki Pages</a>
    <a href="{memories_href}">Structured Wiki Index</a>
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


def build_pages_index(page_outputs: list[tuple[Path, str, Path]]) -> str:
    lines = [
        "# Markdown Wiki Pages",
        "",
        "These pages are sourced from `Data/Pages`.",
        "",
        "The list below reflects the directory tree.",
        "",
    ]

    previous_directories: tuple[str, ...] = ()
    for source_relative_path, title, output_relative_path in page_outputs:
        source_parts = source_relative_path.parts
        directory_parts = source_parts[:-1]

        common_prefix_length = 0
        while (
            common_prefix_length < len(previous_directories)
            and common_prefix_length < len(directory_parts)
            and previous_directories[common_prefix_length] == directory_parts[common_prefix_length]
        ):
            common_prefix_length += 1

        for index in range(common_prefix_length, len(directory_parts)):
            indent = "  " * index
            lines.append(f"{indent}- **{directory_parts[index]}**")

        page_indent = "  " * len(directory_parts)
        lines.append(f"{page_indent}- [{title}]({output_relative_path.as_posix()})")

        previous_directories = tuple(directory_parts)

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


def copy_pages_assets() -> None:
  for source_path in PAGES_DIR.rglob("*"):
    if not source_path.is_file() or source_path.suffix.lower() == ".md":
      continue

    source_relative_path = source_path.relative_to(PAGES_DIR)
    destination_path = OUTPUT_DIR / "pages" / source_relative_path
    destination_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source_path, destination_path)


def parse_args() -> BuildOptions:
  parser = argparse.ArgumentParser(description="Build static wiki pages for GitHub Pages.")
  parser.add_argument(
    "--export-mermaid-svg",
    action="store_true",
    help="Export Mermaid fenced code blocks to SVG files and replace them with image links.",
  )
  args = parser.parse_args()
  export_mermaid_svg = args.export_mermaid_svg or parse_bool_env("MEMORYSMITH_EXPORT_MERMAID_SVG")
  return BuildOptions(export_mermaid_svg=export_mermaid_svg)


def main() -> None:
    options = parse_args()
    mermaid_context = get_mermaid_export_context(options)

    if OUTPUT_DIR.exists():
        shutil.rmtree(OUTPUT_DIR)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    page_files = sorted(PAGES_DIR.rglob("*.md"))
    source_to_output_paths = {
        page_file.relative_to(PAGES_DIR).as_posix(): to_output_relative_path(page_file.relative_to(PAGES_DIR))
        for page_file in page_files
    }

    readme_text = (REPO_ROOT / "README.md").read_text(encoding="utf-8")
    (OUTPUT_DIR / "index.html").write_text(
        render_html(
            "MemorySmith Wiki",
            readme_text,
            Path("README.md"),
            source_to_output_paths,
            Path("index.html"),
            mermaid_context,
        ),
        encoding="utf-8",
    )

    page_outputs: list[tuple[Path, str, Path]] = []
    for page_file in page_files:
        source_relative_path = page_file.relative_to(PAGES_DIR)
        content = page_file.read_text(encoding="utf-8")
        title = page_file.stem
        first_heading = next((line.strip("# ").strip() for line in content.splitlines() if line.startswith("# ")), "")
        if first_heading:
            title = first_heading

        output_relative_path = source_to_output_paths[source_relative_path.as_posix()]
        output_path = OUTPUT_DIR / output_relative_path
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(
            render_html(
                title,
                content,
                source_relative_path,
                source_to_output_paths,
                output_relative_path,
                mermaid_context,
            ),
            encoding="utf-8",
        )
        page_outputs.append((source_relative_path, title, output_relative_path))

    pages_index_md = build_pages_index(page_outputs)
    (OUTPUT_DIR / "pages.html").write_text(
        render_html(
            "Markdown Wiki Pages",
            pages_index_md,
            Path("pages.md"),
            source_to_output_paths,
            Path("pages.html"),
            mermaid_context,
        ),
        encoding="utf-8",
    )

    structured_index_md = build_structured_memories_markdown()
    (OUTPUT_DIR / "structured-memories.html").write_text(
        render_html(
            "Structured Wiki Index",
            structured_index_md,
            Path("structured-memories.md"),
            source_to_output_paths,
            Path("structured-memories.html"),
            mermaid_context,
        ),
        encoding="utf-8",
    )

    copy_pages_assets()

    if options.export_mermaid_svg and mermaid_context.command is None:
      print("Mermaid SVG export requested, but no Mermaid CLI was found. Kept Mermaid fenced blocks unchanged.")
    elif options.export_mermaid_svg:
      print(f"Mermaid SVG export complete: {mermaid_context.exported_count} exported, {mermaid_context.skipped_count} skipped.")


if __name__ == "__main__":
    main()
