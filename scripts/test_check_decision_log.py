import contextlib
import io
import subprocess
import tempfile
import unittest
from pathlib import Path

import check_decision_log as check

OLD = """# Vessel

First rule, on one line.

October 1, 2026: a rule
wrapped over two lines.

## Section

- a list
- of links
"""


class ParagraphsTests(unittest.TestCase):
    def test_headings_are_not_paragraphs(self):
        self.assertEqual(
            check.paragraphs(OLD),
            ["First rule, on one line.", "October 1, 2026: a rule\nwrapped over two lines.", "- a list\n- of links"],
        )

    def test_trailing_spaces_and_crlf_are_ignored(self):
        self.assertEqual(check.paragraphs("a  \r\nb\r\n\r\nc\t\n"), ["a\nb", "c"])


class MissingTests(unittest.TestCase):
    def test_log_with_every_paragraph_passes(self):
        log = "# Log\n\n## D2\n\n- a list\n- of links\n\n## D1\n\nOctober 1, 2026: a rule\nwrapped over two lines.\n\nFirst rule, on one line.\n"
        self.assertEqual(check.missing(OLD, log), [])

    def test_a_dropped_paragraph_is_reported(self):
        log = "First rule, on one line.\n\n- a list\n- of links\n"
        self.assertEqual(check.missing(OLD, log), ["October 1, 2026: a rule\nwrapped over two lines."])

    def test_an_edited_or_rewrapped_paragraph_is_reported(self):
        log = "First rule, on one line!\n\nOctober 1, 2026: a rule wrapped over two lines.\n\n- a list\n- of links\n"
        self.assertEqual(len(check.missing(OLD, log)), 2)


class MainTests(unittest.TestCase):
    def run_main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            code = check.main(list(argv))
        return code, out.getvalue()

    def test_exit_codes(self):
        with tempfile.TemporaryDirectory() as directory:
            old, log = Path(directory, "old.md"), Path(directory, "log.md")
            old.write_text(OLD, encoding="utf-8")
            log.write_text("First rule, on one line.\n", encoding="utf-8")
            code, output = self.run_main("--old", str(old), "--log", str(log))
            self.assertEqual(code, 1)
            self.assertIn("2 of 3 paragraphs", output)
            log.write_text(OLD, encoding="utf-8")
            self.assertEqual(self.run_main("--old", str(old), "--log", str(log))[0], 0)

    def test_unknown_revision_is_an_error(self):
        self.assertEqual(self.run_main("--ref", "0000000000000000000000000000000000000000")[0], 2)

    @unittest.skipUnless(
        subprocess.run(["git", "cat-file", "-e", f"{check.DEFAULT_REF}:AGENTS.md"], cwd=check.ROOT,
                       capture_output=True, check=False).returncode == 0,
        "the pre-move AGENTS.md commit is not in this clone",
    )
    def test_repository_decision_log_holds_the_former_agents_file(self):
        code, output = self.run_main()
        self.assertEqual(code, 0, output)


if __name__ == "__main__":
    unittest.main()
