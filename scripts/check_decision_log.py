#!/usr/bin/env python3
"""Check that every paragraph of the former top-level AGENTS.md is in docs/decisions.md.

Issue #102 moved the dated guidance out of AGENTS.md into the decision log, verbatim.
This reads the old file from git (default: commit e1b2ea40, the last one before the
move) and reports any paragraph that is not present, unchanged, in the log. Headings
are not paragraphs. Trailing spaces at line ends are ignored.

    python3 scripts/check_decision_log.py [--ref REV | --old FILE] [--log FILE]
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_REF = "e1b2ea40"
DEFAULT_LOG = ROOT / "docs" / "decisions.md"


def _normalize(text: str) -> str:
    return "\n".join(line.rstrip() for line in text.replace("\r\n", "\n").split("\n"))


def paragraphs(text: str) -> list[str]:
    """Blank-line separated blocks, without heading-only blocks."""
    blocks = [block.strip("\n") for block in re.split(r"\n[ \t]*\n", _normalize(text))]
    return [
        block for block in blocks
        if block.strip() and not all(line.lstrip().startswith("#") for line in block.splitlines())
    ]


def missing(old: str, log: str) -> list[str]:
    """Paragraphs of `old` that do not appear verbatim in `log`."""
    haystack = _normalize(log)
    return [paragraph for paragraph in paragraphs(old) if paragraph not in haystack]


def read_old(ref: str) -> str:
    result = subprocess.run(
        ["git", "show", f"{ref}:AGENTS.md"], cwd=ROOT, capture_output=True, text=True, check=False
    )
    if result.returncode != 0:
        raise RuntimeError(f"cannot read AGENTS.md at {ref}: {result.stderr.strip()}")
    return result.stdout


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--ref", default=DEFAULT_REF, help=f"git revision of the old AGENTS.md (default {DEFAULT_REF})")
    source.add_argument("--old", type=Path, help="read the old AGENTS.md from this file instead of git")
    parser.add_argument("--log", type=Path, default=DEFAULT_LOG, help="decision log (default docs/decisions.md)")
    args = parser.parse_args(argv)

    try:
        old = args.old.read_text(encoding="utf-8") if args.old else read_old(args.ref)
    except (OSError, RuntimeError) as error:
        print(f"check_decision_log: {error}", file=sys.stderr)
        return 2
    log = args.log.read_text(encoding="utf-8")
    source_name = str(args.old) if args.old else f"AGENTS.md at {args.ref}"
    log_name = args.log.relative_to(ROOT) if args.log.is_relative_to(ROOT) else args.log

    total = len(paragraphs(old))
    absent = missing(old, log)
    if absent:
        print(f"{len(absent)} of {total} paragraphs of {source_name} are missing from {log_name}:")
        for paragraph in absent:
            print(f"  - {paragraph.splitlines()[0][:100]}")
        return 1
    print(f"OK: all {total} paragraphs of {source_name} appear verbatim in {log_name}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
