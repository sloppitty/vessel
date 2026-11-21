#!/usr/bin/env python3
"""PreToolUse guard for coding agents in this repository.

`.claude/settings.json` (Claude Code) and `.codex/hooks.json` (Codex) run it
before every tool call with the call as JSON on stdin. It exits 2 with the
reason on stderr to refuse the call, and 0 to let it through. It refuses what
the repository rules forbid and an accident would make irreversible:

- reading or writing `.env` files (templates such as `.env.example` excepted)
  and other credential files, or printing the environment or a secret variable;
- force pushes without a lease, any lease push to `main`, deleting `main`,
  pushing, moving or deleting release tags, and `git tag -f`;
- publishing: `docker push`, registry pushes from buildx, `imagetools create`,
  `release.py publish` and `gh release` changes;
- migrations, `psql` and test runs aimed at the development database (port
  55432 or database `vessel`), `pnpm db:migrate`, and `dotnet ef database
  update` without an explicit throwaway `--connection`;
- deleting the volumes of the owner's Compose project (`vessel`) or opening a
  shell in its containers, and printing any Compose configuration interpolated
  from a real `.env` file or the environment.

Commands run by wrappers (`sudo`, `env`, `xargs`, ...), by `sh -c`, or fed to a
shell as a here-document, here-string or `echo ... |` are checked as commands,
and the values wrapper options take as arguments. A wrapper option the guard does
not know, and `echo`/`printf` text with escapes or directives piped into a shell
or interpreter, are refused rather than guessed.

Starting the session with `VESSEL_AGENT_RELEASE=1` in the environment lets an
owner-authorized release push one new release tag and publish. Moving or
deleting a tag stays refused. The guard stops accidents, not a determined
author: a script that hides a command from it is not checked. Input that is not
a tool call is let through; an error inside the guard refuses the call.
"""
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import subprocess
import sys

DEV_PORT = re.compile(r'(?<!\d)55432(?!\d)')
LIVE_DATABASE = re.compile(r'(?i)\b(?:database|dbname)\s*=\s*vessel(?![\w-])')
DATABASE_VERBS = re.compile(r'(?i)\bmigrate\b|\bdatabase\s+(?:update|drop)\b|\bpsql\b|\bpg_restore\b|\bdropdb\b')
TEST_DATABASE_VARIABLE = 'vessel_test_postgres'
ENV_FILE = re.compile(r'\.env(?:[.*?\[].*)?|\.envrc')
ENV_TEMPLATES = {'.env.example', '.env.sample', '.env.template'}
CREDENTIAL_FILES = ('.docker/config.json', 'gh/hosts.yml', '.git-credentials', '.pgpass', '.netrc')
SECRET_NAME = re.compile(r'(?i)token|secret|passw|private_?key|api_?key|credential')
OWNER_PROJECT = 'vessel'
OWNER_CONTAINER = re.compile(r'^vessel[-_](?:postgres|rabbitmq)[-_]\d+$')
RELEASE_TAG = re.compile(r'^(?:refs/tags/)?v\d+\.\d+')
PROTECTED_BRANCHES = {'main', 'master'}
OPERATORS = set(';&|()<>\n')
# Commands that only read or print their arguments: a port or a database name in them is text, not a target.
READERS = {'rg', 'grep', 'egrep', 'cat', 'head', 'tail', 'less', 'sed', 'awk', 'wc', 'ls', 'find', 'fzf', 'jq',
           'echo', 'printf', 'git', 'gh', 'diff', 'sort', 'uniq', 'cut', 'tr'}
COMMON_FLAGS = {'--help', '--version'}
# Commands that run the command after them: (short flags, short options taking a value, short options whose
# value can only be attached, long flags, long options taking a value, operands before the command).
WRAPPERS = {
    'sudo': ('AbBEeHiKklnPSsVv', 'ugpChDRrtTU', '',
             {'--askpass', '--background', '--bell', '--preserve-env', '--edit', '--set-home', '--login',
              '--remove-timestamp', '--reset-timestamp', '--non-interactive', '--preserve-groups', '--stdin',
              '--shell', '--validate', '--list'},
             {'--user', '--group', '--prompt', '--close-from', '--chdir', '--chroot', '--role', '--type',
              '--command-timeout', '--other-user', '--host'}, 0),
    'command': ('pvV', '', '', set(), set(), 0),
    'exec': ('cl', 'a', '', set(), set(), 0),
    'nohup': ('', '', '', set(), set(), 0),
    'time': ('apqv', 'fo', '', {'--append', '--portability', '--quiet', '--verbose'}, {'--format', '--output'}, 0),
    'nice': ('', 'n', '', set(), {'--adjustment'}, 0),
    'timeout': ('v', 'sk', '', {'--preserve-status', '--foreground', '--verbose'}, {'--signal', '--kill-after'}, 1),
    'xargs': ('0prtx', 'aIdELnPs', 'iel',
              {'--null', '--interactive', '--no-run-if-empty', '--verbose', '--exit', '--open-tty', '--show-limits'},
              {'--arg-file', '--delimiter', '--eof', '--replace', '--max-lines', '--max-args', '--max-procs',
               '--max-chars', '--process-slot-var'}, 0),
    'env': ('i0v', 'uCS', '', {'--ignore-environment', '--null', '--debug'}, {'--unset', '--chdir', '--split-string'},
            0),
    'stdbuf': ('', 'ioe', '', set(), {'--input', '--output', '--error'}, 0),
}
SHELLS = {'sh', 'bash', 'zsh', 'dash', 'ksh'}
INTERPRETERS = {'node', 'perl', 'ruby', 'php', 'deno', 'bun', 'lua'}
ASSIGNMENT = re.compile(r'^[A-Za-z_]\w*=')
HEREDOC_OPERATOR = re.compile(r"<<(-?)[ \t]*(?:'([^'\n]+)'|\"([^\"\n]+)\"|\\(\w+)|([A-Za-z_]\w*))")
MARKER = re.compile(r'__agent_guard_document_(\d+)__')
COMPOSE_FILES = ('compose.yaml', 'compose.yml', 'docker-compose.yaml', 'docker-compose.yml')


class Refused(Exception):
    pass


def release_allowed():
    return os.environ.get('VESSEL_AGENT_RELEASE') == '1'


# Paths

def is_secret_path(text):
    """True when a token names a .env file or a credential file, also as part of key=path or src:dst."""
    if text.startswith('/proc/') and text.endswith('/environ'):
        return True
    for piece in re.split(r'[=:,]', text):
        piece = piece.strip().strip('`"\'()$;').rstrip('/')
        name = piece.rsplit('/', 1)[-1]
        if ENV_FILE.fullmatch(name) and name not in ENV_TEMPLATES:
            return True
        if any(piece == file or piece.endswith('/' + file) for file in CREDENTIAL_FILES):
            return True
    return False


def check_path(path):
    if isinstance(path, str) and is_secret_path(path):
        raise Refused(f'{path} holds secrets. Agents never read or write .env or credential files; '
                      'use the .env.example template or ask the owner for the non-secret value you need.')


# Shell parsing

def extract_documents(command, documents):
    """Replace each here-document with a marker word naming its entry in `documents`, an (expands, body) pair.

    Quotes, escapes and comments are followed so that `<<` inside a string is not taken for a here-document.
    A body runs to its delimiter line, or to the end when there is none, as in the shell."""
    out, pending, quote, index, size = [], [], None, 0, len(command)
    while index < size:
        char = command[index]
        if quote == "'":
            quote = None if char == "'" else quote
        elif char == '\\':
            out.append(command[index:index + 2])
            index += 2
            continue
        elif quote == '"':
            quote = None if char == '"' else quote
        elif char in '\'"':
            quote = char
        elif char == '#' and (index == 0 or command[index - 1] in ' \t\n;&|()'):
            end = command.find('\n', index)
            end = size if end < 0 else end
            out.append(command[index:end])
            index = end
            continue
        elif command.startswith('<<<', index):
            out.append('<<<')
            index += 3
            continue
        elif command.startswith('<<', index) and (found := HEREDOC_OPERATOR.match(command, index)):
            delimiter = found[2] or found[3] or found[4] or found[5]
            pending.append((len(documents), delimiter, found[1] == '-'))
            documents.append((found[5] is not None, ''))
            out.append(f' __agent_guard_document_{len(documents) - 1}__ ')
            index = found.end()
            continue
        elif char == '\n' and pending:
            index += 1
            out.append('\n')
            for number, delimiter, tabs in pending:
                start = index
                while index < size:
                    line_start, end = index, command.find('\n', index)
                    end = size if end < 0 else end
                    line = command[line_start:end]
                    index = min(end + 1, size)
                    if (line.lstrip('\t') if tabs else line) == delimiter:
                        documents[number] = (documents[number][0], command[start:max(start, line_start - 1)])
                        break
                else:
                    documents[number] = (documents[number][0], command[start:])
            pending = []
            continue
        out.append(char)
        index += 1
    return ''.join(out)


def tokens(command):
    lexer = shlex.shlex(command, posix=True, punctuation_chars=';&|()<>\n')
    lexer.whitespace = ' \t\r'
    lexer.whitespace_split = True
    lexer.commenters = ''
    try:
        return list(lexer)
    except ValueError:  # Unbalanced quotes: fall back to splitting on blanks and operators.
        return [part for part in re.split(r"([;&|()<>\n])|[\s'\"]+", command) if part]


class Words(list):
    """The words of one simple command, and the words of the command piped into it."""
    piped_from = None


def segments(command, documents):
    """Each simple command as a list of words, comments removed, operators dropped. A here-string becomes a
    document marker in the command it feeds."""
    current, found, comment, herestring, previous = Words(), [], False, False, None
    for token in tokens(command):
        if token == '\n':
            comment = False
        if comment:
            continue
        if token.startswith('#'):
            comment = True
            continue
        if herestring and not set(token) <= OPERATORS:
            documents.append((True, token))
            current.append(f'__agent_guard_document_{len(documents) - 1}__')
            herestring = False
            continue
        if token == '<<<':
            herestring = True
            continue
        if set(token) <= OPERATORS:
            if current:
                found.append(current)
            pipe = token in ('|', '|&')
            previous = current if pipe and current else None if not pipe else previous
            current = Words()
            current.piped_from = previous
        else:
            current.append(token)
    if current:
        found.append(current)
    return found


def substitutions(word):
    return re.findall(r'\$\(([^()]*)\)', word) + re.findall(r'`([^`]*)`', word)


def wrapper_parts(name, args):
    """A wrapper's options as (option, value) pairs and the words of the command it runs. Refuses options the
    guard does not know, since it cannot tell whether they take a value."""
    flags, valued, attached, long_flags, long_valued, operands = WRAPPERS[name]
    options, index = [], 0
    while index < len(args):
        arg = args[index]
        if arg == '--':
            index += 1
            break
        if name == 'env' and arg == '-' or name == 'nice' and re.fullmatch(r'-\d+', arg):
            options.append((arg, None))
        elif arg.startswith('--'):
            option, equals, value = arg.partition('=')
            if option in long_valued and not equals:
                value = args[index + 1] if index + 1 < len(args) else ''
                index += 1
            elif option not in long_valued | long_flags | COMMON_FLAGS:
                raise Refused(f'{name} {option} is a wrapper option the guard does not know. Run the command '
                              'without it, or spell the command out directly.')
            options.append((option, value))
        elif arg.startswith('-') and len(arg) > 1:
            letters = arg[1:]
            for at, letter in enumerate(letters):
                if letter in valued or letter in attached:
                    value = letters[at + 1:]
                    if not value and letter in valued:
                        value = args[index + 1] if index + 1 < len(args) else ''
                        index += 1
                    options.append(('-' + letter, value))
                    break
                if letter not in flags:
                    raise Refused(f'{name} -{letter} is a wrapper option the guard does not know. Run the command '
                                  'without it, or spell the command out directly.')
                options.append(('-' + letter, None))
        else:
            break
        index += 1
    return options, args[index + operands:]


def unwrap(words, consumed=None):
    """Leading VAR=value assignments and the command after wrappers such as sudo, env or xargs. The values and
    operands the wrappers consume are added to `consumed`, to be checked like any other argument."""
    assignments, consumed = [], [] if consumed is None else consumed
    while words:
        word, name = words[0], PurePosixPath(words[0]).name
        if ASSIGNMENT.match(word):
            assignments.append(word)
            words = words[1:]
        elif name in WRAPPERS:
            options, rest = wrapper_parts(name, words[1:])
            consumed.extend(value for _, value in options if value)
            consumed.extend(words[1:len(words) - len(rest)])
            words = rest
            if name == 'command' and {'-v', '-V'} & {option for option, _ in options}:
                return assignments, []  # command -v only looks the command up.
            if name == 'env':
                split = [value for option, value in options if option in ('-S', '--split-string')]
                words = [word for value in split for word in tokens(value)] + list(words)
                while words and ASSIGNMENT.match(words[0]):
                    assignments.append(words[0])
                    words = words[1:]
                if not words:
                    raise Refused('env without a command prints the whole environment, which can hold secrets. '
                                  'Print the variable you need by name.')
        else:
            break
    return assignments, words


def shell_input(args):
    """Where a shell reads its commands: ('command', text) for -c, ('stdin', None), or ('script', path)."""
    index, command, stdin = 0, False, False
    while index < len(args):
        arg = args[index]
        if arg in ('--', '-'):
            index += 1
            break
        if arg.startswith('--'):
            index += 2 if arg in ('--rcfile', '--init-file') else 1
        elif re.fullmatch(r'[-+][A-Za-z]+', arg):
            letters = arg[1:]
            command |= arg[0] == '-' and 'c' in letters
            stdin |= arg[0] == '-' and 's' in letters
            index += 1 + letters.count('o') + letters.count('O')
        else:
            break
    rest = args[index:]
    if command:
        return 'command', rest[0] if rest else ''
    return ('stdin', None) if stdin or not rest else ('script', rest[0])


def check_expansions(text, cwd, depth, documents):
    """Command substitutions and secret-looking variables expanded inside a here-document or here-string."""
    for inner in substitutions(text):
        check_command(inner, cwd, depth + 1, documents)
    if any(SECRET_NAME.search(name) for name in re.findall(r'\$\{?(\w+)', text)):
        raise Refused('That expands a variable that looks like a secret. Never print secrets.')


def check_program(text):
    """Code for another interpreter: refuse when it names a .env or credential file."""
    for word in re.findall(r"[^\s'\"`()\[\]{};]+", text):
        if is_secret_path(word):
            raise Refused(f'That code names {word}, which holds secrets. Agents never read or write .env or '
                          'credential files. If the code only edits text that mentions it, use the Edit tool or '
                          'apply_patch instead.')


def check_documents(name, args, documents_here, piped_from, cwd, depth, documents):
    """Here-documents and here-strings, and what is piped into a shell: inert text, shell input or code."""
    reads_stdin = name in SHELLS and shell_input(args)[0] == 'stdin'
    for expands, body in documents_here:
        if reads_stdin:
            check_command(body, cwd, depth + 1, documents)
        elif name.startswith('python') or name in INTERPRETERS:
            check_program(body)
        if expands:
            check_expansions(body, cwd, depth, documents)
    if piped_from and not documents_here and (reads_stdin or reads_program(name, args)):
        producer = unwrap(piped_from)[1]
        if producer[:1] not in (['echo'], ['printf']):
            if reads_stdin:
                raise Refused('Commands piped into a shell cannot be checked. Run them directly.')
            return
        text = ' '.join(producer[1:])
        if producer[0] == 'echo' and producer[1:2] and producer[1].startswith('-'):
            raise Refused(f'echo with options piped into {name} runs text the guard cannot read as written. '
                          'Run the commands directly.')
        if '\\' in text or producer[0] == 'printf' and '%' in text:
            raise Refused(f'{producer[0]} with escapes or format directives piped into {name} runs text the guard '
                          'cannot read as written. Run the commands directly.')
        if reads_stdin:
            check_command(text, cwd, depth + 1, documents)
        else:
            check_program(text)


def reads_program(name, args):
    """True when an interpreter takes its program from standard input: no script, no inline code."""
    if not (name.startswith('python') or name in INTERPRETERS):
        return False
    return all(arg.startswith('-') for arg in args) and not {'-c', '-e', '-m', '-p', '-E'} & set(args)


def option_value(args, *names):
    """The value of the first of these options, given as `--name value` or `--name=value`."""
    for index, arg in enumerate(args):
        for name in names:
            if arg == name and index + 1 < len(args):
                return args[index + 1]
            if name.startswith('--') and arg.startswith(name + '='):
                return arg[len(name) + 1:]
    return None


def positionals(args, valued=()):
    found, skip = [], False
    for arg in args:
        if skip:
            skip = False
        elif arg in valued:
            skip = True
        elif not arg.startswith('-'):
            found.append(arg)
    return found


def has_short(args, letter):
    return any(re.fullmatch(rf'-[A-Za-z]*{letter}[A-Za-z]*', arg) for arg in args)


# Commands

def check_git(args, cwd):
    while args and args[0].startswith('-'):
        args = args[2:] if args[0] in ('-C', '-c') else args[1:]
    if not args:
        return
    command, rest = args[0], args[1:]
    if command == 'push':
        check_push(rest, cwd)
    elif command == 'tag':
        if '--force' in rest or has_short(rest, 'f'):
            raise Refused('git tag -f moves a tag. Release tags are never moved; fix forward with the next patch.')
        if ('--delete' in rest or has_short(rest, 'd')) and any(RELEASE_TAG.match(arg) for arg in rest):
            raise Refused('Release tags are never deleted; fix forward with the next patch version.')
    elif command == 'update-ref' and any(arg.startswith('refs/tags/') for arg in rest):
        raise Refused('git update-ref on refs/tags/ moves or deletes a tag. Release tags are never moved.')


def check_push(args, cwd):
    lease = any(arg.startswith('--force-with-lease') for arg in args)
    force = '--force' in args or has_short(args, 'f')
    refs = positionals(args, valued=('-o', '--push-option', '--repo', '--receive-pack', '--exec'))[1:]
    if 'tag' in refs:
        refs = [f'refs/tags/{ref}' for ref in refs if ref != 'tag']
    if force or '--mirror' in args or any(ref.startswith('+') for ref in refs):
        raise Refused('Force pushes rewrite remote history. Use git push --force-with-lease on your own '
                      'feature branch.')
    deleting = '--delete' in args or has_short(args, 'd')
    targets = [(ref.split(':', 1)[1] if ':' in ref else ref).removeprefix('refs/heads/') for ref in refs]
    if any(t in PROTECTED_BRANCHES for t in targets) and (lease or deleting) or \
            any(ref.startswith(':') and ref[1:].removeprefix('refs/heads/') in PROTECTED_BRANCHES for ref in refs):
        raise Refused('main is never force-pushed or deleted.')
    if lease and not refs and current_branch(cwd) in PROTECTED_BRANCHES:
        raise Refused('main is never force-pushed.')
    tags = [t for t in targets if t.startswith('refs/tags/') or RELEASE_TAG.match(t)]
    if '--tags' in args or '--follow-tags' in args:
        raise Refused('git push --tags pushes every local tag. Push one release tag by name, and only for a release.')
    if tags and (deleting or lease or any(ref.startswith(':') for ref in refs)):
        raise Refused('Release tags are never moved or deleted; fix forward with the next patch version.')
    if tags and not release_allowed():
        raise Refused('Pushing a release tag publishes a release. Ask the owner to push it, or to start the '
                      'release session with VESSEL_AGENT_RELEASE=1.')


def current_branch(cwd):
    result = subprocess.run(['git', 'rev-parse', '--abbrev-ref', 'HEAD'], cwd=cwd or None, capture_output=True,
                            text=True)
    return result.stdout.strip() if result.returncode == 0 else None


def publishing(what):
    if not release_allowed():
        raise Refused(f'{what} publishes outside this machine. Releases publish from the tag-triggered workflow; '
                      'the owner starts a manual release session with VESSEL_AGENT_RELEASE=1.')


def local_registry(ref):
    return ref.startswith(('localhost:', 'localhost/', '127.0.0.1:'))


def check_docker(args, assignments, cwd):
    while args and args[0].startswith('-'):
        args = args[2:] if args[0] in ('-H', '--host', '-c', '--context', '--config', '-l', '--log-level') \
            else args[1:]
    if not args:
        return
    command, rest = args[0], args[1:]
    if command in ('image', 'container', 'volume', 'manifest', 'system', 'buildx') and rest:
        command, rest = f'{command} {rest[0]}', rest[1:]
    if command in ('push', 'image push', 'manifest push'):
        if not all(local_registry(ref) for ref in positionals(rest)):
            publishing('docker push')
    elif command in ('build', 'buildx build', 'buildx bake'):
        output = ' '.join(rest)
        if '--push' in rest or 'type=registry' in output or 'push=true' in output:
            publishing('A build with --push')
    elif command == 'buildx imagetools' and rest[:1] == ['create'] and '--dry-run' not in rest:
        publishing('docker buildx imagetools create')
    elif command in ('volume rm', 'volume remove') and \
            any(arg.startswith(OWNER_PROJECT + '_') for arg in positionals(rest)):
        raise Refused("Those are the owner's development volumes (Compose project vessel). Never delete them; "
                      'throwaway projects use their own -p name.')
    elif command == 'volume prune' or (command == 'system prune' and '--volumes' in rest):
        raise Refused("Pruning volumes can delete the owner's development database. Remove your own throwaway "
                      'volumes by name.')
    elif command in ('exec', 'cp', 'inspect', 'container exec', 'container cp', 'container inspect') and \
            any(OWNER_CONTAINER.match(arg.split(':', 1)[0]) for arg in rest):
        raise Refused("That is the owner's development database or broker container. Use a throwaway test "
                      'database (scripts/test-db.sh) instead.')
    elif command == 'compose':
        check_compose(rest, assignments, cwd)


def compose_files(files, cwd):
    """The Compose files a command reads: its -f files, else the first compose.yaml found upwards."""
    base = Path(cwd or os.getcwd())
    named = [base / file for file in files if file not in (None, '-')]
    if named:
        return named
    found = next((folder / name for folder in (base, *base.parents) for name in COMPOSE_FILES
                  if (folder / name).is_file()), None)
    return [found] if found else []


def compose_project(files, directory, assignments, cwd):
    """The project name Compose would use: COMPOSE_PROJECT_NAME, the file's `name:`, else its directory's name."""
    for assignment in assignments:
        if assignment.startswith('COMPOSE_PROJECT_NAME='):
            return assignment.partition('=')[2]
    if os.environ.get('COMPOSE_PROJECT_NAME'):
        return os.environ['COMPOSE_PROJECT_NAME']
    found = next(iter(compose_files(files, cwd)), None)
    try:
        named = re.search(r'^name:\s*["\']?([\w.-]+)', found.read_text(), re.MULTILINE) if found else None
    except OSError:
        named = None
    if named:
        return named[1]
    folder = Path(directory) if directory else found.parent if found else None
    return re.sub(r'[^a-z0-9_-]', '', folder.resolve().name.lower()) if folder else None


def check_compose(args, assignments, cwd):
    valued = ('-f', '--file', '-p', '--project-name', '--project-directory', '--env-file', '--profile', '--ansi',
              '--progress', '--parallel')
    files, project, index = [], None, 0
    while index < len(args) and args[index].startswith('-'):
        arg = args[index]
        name, _, inline = arg.partition('=')
        value = inline if inline else (args[index + 1] if name in valued and index + 1 < len(args) else None)
        if name in ('-f', '--file'):
            files.append(value)
        elif name in ('-p', '--project-name'):
            project = value
        index += 1 if inline or name not in valued else 2
    if index < len(args) and args[index] in ('config', 'convert'):
        check_compose_config(args[index + 1:], files, cwd)
    if project is None:
        project = compose_project(files, option_value(args[:index], '--project-directory'), assignments, cwd)
    if project != OWNER_PROJECT or index >= len(args):
        return
    command, rest = args[index], args[index + 1:]
    if command == 'down' and ('--volumes' in rest or has_short(rest, 'v')):
        raise Refused("docker compose down -v deletes the owner's development database. Use -p <throwaway> for "
                      'your own project.')
    if command in ('exec', 'run', 'cp'):
        raise Refused("That runs inside the owner's development services. Use a throwaway test database "
                      '(scripts/test-db.sh) or your own -p project.')
    if command in ('run', 'up', 'create') and 'migrate' in rest:
        raise Refused('Migrations run only against throwaway test databases.')


def check_compose_config(rest, files, cwd):
    """`docker compose config` prints values from the project's .env file, the environment and service env_file
    files, whatever the project name and --env-file. Allow listings and uninterpolated output only."""
    if '--environment' in rest:
        raise Refused('docker compose config --environment prints the environment used for interpolation, which '
                      'can hold secrets. Use --services, --quiet or --no-interpolate instead.')
    if {'--services', '--volumes', '--profiles', '--images', '--networks', '--models', '-q', '--quiet'} & set(rest):
        return
    if '--no-interpolate' not in rest:
        raise Refused('docker compose config interpolates the .env file next to the Compose file (the owner\'s '
                      'secrets) and the session environment, whatever -p or --env-file say. Use --services or '
                      '--quiet, or --no-interpolate.')
    if '--no-env-resolution' not in rest:
        for file in compose_files(files, cwd):
            try:
                if re.search(r'^\s*env_file\s*:', file.read_text(), re.MULTILINE):
                    raise Refused(f'{file.name} reads env_file files, which docker compose config prints. Add '
                                  '--no-env-resolution.')
            except OSError:
                pass


def check_secret_printing(name, args):
    dump = (name in ('env', 'printenv') and not positionals(args)) or (name == 'set' and not args) or \
        (name in ('export', 'declare') and not positionals(args) and (not args or {'-p', '-x'} & set(args)))
    if dump:
        raise Refused(f'{name} prints the whole environment, which can hold secrets. Print the variable you need '
                      'by name.')
    if name == 'printenv' and any(SECRET_NAME.search(arg) for arg in args):
        raise Refused('That variable looks like a secret. Never print secrets.')
    if name in ('echo', 'printf') and any(SECRET_NAME.search(v) for arg in args
                                          for v in re.findall(r'\$\{?(\w+)', arg)):
        raise Refused('That prints a variable that looks like a secret. Never print secrets.')


def check_gh(args):
    if args[:2] == ['auth', 'token'] or (args[:2] == ['auth', 'status'] and
                                         ('--show-token' in args or has_short(args[2:], 't'))):
        raise Refused('That prints the GitHub token. Never print secrets.')
    if args[:1] == ['release'] and len(args) > 1 and args[1] in ('create', 'edit', 'upload', 'delete',
                                                                  'delete-asset'):
        publishing(f'gh release {args[1]}')


def check_database(words, assignments):
    """Migrations, psql or test runs aimed at the development database."""
    for assignment in assignments:
        name, _, value = assignment.partition('=')
        if name.lower() == TEST_DATABASE_VARIABLE and (DEV_PORT.search(value) or LIVE_DATABASE.search(value)):
            raise Refused('Vessel_TEST_POSTGRES names the development database. Tests drop what they create; '
                          'point it at a throwaway database from scripts/test-db.sh.')
    if not words:
        return
    name, args = PurePosixPath(words[0]).name, words[1:]
    if name == 'pnpm' and any(arg in ('db:migrate',) for arg in args) or \
            name.startswith('python') and any(arg.endswith('dev.py') for arg in args) and 'migrate' in args:
        raise Refused('pnpm db:migrate migrates the development database. Apply migrations only through the tests '
                      "against a throwaway database; the owner migrates their own database.")
    if name == 'dotnet' and args[:1] == ['ef'] and 'database' in args and \
            any(arg in ('update', 'drop') for arg in args):
        connection = option_value(args, '--connection')
        if not connection or DEV_PORT.search(connection) or LIVE_DATABASE.search(connection):
            raise Refused('dotnet ef database update/drop without --connection to a throwaway database targets '
                          'the development database.')
    if name == 'dotnet' and args[:1] == ['test']:
        value = os.environ.get('Vessel_TEST_POSTGRES', '')
        if DEV_PORT.search(value) or LIVE_DATABASE.search(value):
            raise Refused('This session sets Vessel_TEST_POSTGRES to the development database. Point it at a '
                          'throwaway database from scripts/test-db.sh.')


def check_words(words, cwd, depth, documents):
    documents_here = [documents[int(found[1])] for word in words if (found := MARKER.fullmatch(word))]
    piped_from = words.piped_from if isinstance(words, Words) else None
    consumed = []
    assignments, words = unwrap([word for word in words if not MARKER.fullmatch(word)], consumed)
    exported = [word for word in words[1:] if '=' in word] if words[:1] in (['export'], ['declare']) else []
    check_database(words, assignments + exported)
    for word in consumed + assignments + words:
        for inner in substitutions(word):
            check_command(inner, cwd, depth + 1, documents)
    for word in consumed:
        check_path(word)
    if not words:
        for expands, body in documents_here:
            if expands:
                check_expansions(body, cwd, depth, documents)
        return
    name, args = PurePosixPath(words[0]).name, words[1:]
    # The search pattern of rg or grep is text, not a file.
    pattern = None
    if name in ('rg', 'grep', 'egrep') and not option_value(args, '-e', '--regexp'):
        found = positionals(args, valued=('-g', '--glob', '-t', '--type', '-T', '--type-not', '-A', '-B', '-C',
                                          '-m', '--max-count', '-M', '-j', '--max-depth', '-d', '--iglob'))
        pattern = found[0] if found else None
    for word in assignments + words:
        if word is not pattern:
            check_path(word)
    check_documents(name, args, documents_here, piped_from, cwd, depth, documents)
    if name in SHELLS:
        mode, text = shell_input(args)
        if mode == 'command':
            check_command(text, cwd, depth + 1, documents)
    elif name == 'eval':
        check_command(' '.join(args), cwd, depth + 1, documents)
    elif name == 'git':
        check_git(args, cwd)
    elif name == 'docker':
        check_docker(args, assignments, cwd)
    elif name == 'docker-compose':
        check_compose(args, assignments, cwd)
    elif name == 'gh':
        check_gh(args)
    elif name.startswith('python') and any(arg.endswith('release.py') for arg in args) and 'publish' in args:
        publishing('release.py publish')
    check_secret_printing(name, args)


def command_name(words):
    command = unwrap([word for word in words if not MARKER.fullmatch(word)])[1]
    return PurePosixPath(command[0]).name if command else ''


def check_command(command, cwd=None, depth=0, documents=None):
    if depth > 5:
        raise Refused('The command nests too deeply to check.')
    documents = [] if documents is None else documents
    found = segments(extract_documents(command, documents), documents)
    for words in found:
        if words[0] == 'cd' and len(words) > 1:
            cwd = str(Path(cwd or os.getcwd(), os.path.expanduser(words[1])))
        check_words(words, cwd, depth, documents)
    # The development database port or name and a database verb, anywhere outside text-only commands.
    acting = ' '.join(word for words in found if command_name(words) not in READERS for word in words)
    if (DEV_PORT.search(acting) or LIVE_DATABASE.search(acting)) and DATABASE_VERBS.search(acting):
        raise Refused('That combines the development database (port 55432 or database vessel) with a migration '
                      'or psql. Use a throwaway database from scripts/test-db.sh.')


def check_patch(patch):
    """A Codex apply_patch body: only the files it adds, updates, deletes or moves to are checked."""
    for path in re.findall(r'^\*\*\* (?:Add File|Update File|Delete File|Move to): (.+?)\s*$', patch, re.MULTILINE):
        check_path(path)


def check_call(call):
    tool_input = call.get('tool_input')
    if not isinstance(tool_input, dict):
        return
    command = tool_input.get('command')
    if isinstance(command, list) and all(isinstance(word, str) for word in command):
        # Codex can pass an argument vector, and apply_patch as ["apply_patch", "<patch>"].
        command = command[1] if command[:1] == ['apply_patch'] and len(command) == 2 else shlex.join(command)
    if isinstance(command, str):
        if call.get('tool_name') == 'apply_patch' or command.lstrip().startswith('*** Begin Patch'):
            check_patch(command)
        else:
            check_command(command, call.get('cwd'))
    for key in ('file_path', 'path', 'notebook_path'):
        check_path(tool_input.get(key))
    if isinstance(tool_input.get('patch'), str):
        check_patch(tool_input['patch'])


def main(stdin=sys.stdin, stderr=sys.stderr):
    try:
        call = json.load(stdin)
    except ValueError:
        return 0  # Not a tool call: nothing to judge.
    if not isinstance(call, dict):
        return 0
    try:
        check_call(call)
    except Refused as refusal:
        print(f'Refused by scripts/agent_guard.py: {refusal}', file=stderr)
        return 2
    except Exception as error:  # A guard that cannot judge refuses rather than letting the call through.
        print(f'Refused: scripts/agent_guard.py failed ({type(error).__name__}: {error}). Report it.', file=stderr)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
