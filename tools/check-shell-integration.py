#!/usr/bin/env python3
"""Run a real shell with integration injected and report the marks it emitted.

    python3 tools/check-shell-integration.py zsh
    python3 tools/check-shell-integration.py bash /opt/homebrew/bin/bash
    python3 tools/check-shell-integration.py bash
    python3 tools/check-shell-integration.py fish
    python3 tools/check-shell-integration.py pwsh

Build the library first, so the scripts are in its output directory:

    dotnet build src/Terminal.ShellIntegration -c Release

Why this exists rather than another unit test. The C# side is pure and covered by tests that assert
arguments and environment without spawning anything. What those cannot see is whether the SCRIPTS
work, and the two real bugs found while writing them were both invisible to everything else:

  * zsh: `local status=$?` silently breaks a function, because `status` is a special parameter in
    zsh aliased to `$?`. The prompt still drew, and only the A and D marks went missing.
  * bash: macOS ships 3.2 from 2007, which does not source $ENV in POSIX mode at all. No error, no
    marks — the same symptom as a script that was never written.

A shell that emits nothing looks exactly like a shell that was never instrumented, so this prints
what arrived rather than passing or failing.
"""
import os
import pty
import re
import select
import sys
import time

SHELL = sys.argv[1] if len(sys.argv) > 1 else "zsh"

# An explicit binary, for testing a shell the system does not ship as its default —
# macOS bash is 3.2 from 2007, so a modern one has to be named.
BINARY = sys.argv[2] if len(sys.argv) > 2 else SHELL

here = os.path.dirname(os.path.abspath(__file__))
resources = os.path.join(
    here, "..", "src", "Terminal.ShellIntegration", "bin", "Release", "net10.0", "shell-integration"
)
resources = os.path.normpath(resources)

if not os.path.isdir(resources):
    sys.exit(f"scripts not found at {resources}\nbuild first: dotnet build src/Terminal.ShellIntegration -c Release")

env = dict(os.environ)
env["TERMINAL_SHELL_RESOURCES"] = resources
env["PS1"] = "$ "
env["TERMINAL_SHELL_INTEGRATION"] = "1"

if SHELL == "bash":
    env["TERMINAL_SHELL_BASH_INJECT"] = ""
    env["ENV"] = os.path.join(resources, "bash", "integration.bash")
    argv = [BINARY, "--posix", "-i"]
elif SHELL == "zsh":
    env["TERMINAL_SHELL_ZDOTDIR"] = env.get("HOME", "")
    env["ZDOTDIR"] = os.path.join(resources, "zsh")
    argv = [BINARY, "-i"]
elif SHELL == "fish":
    # The parent of fish/, because fish searches each data directory for fish/vendor_conf.d.
    existing = env.get("XDG_DATA_DIRS", "")
    env["XDG_DATA_DIRS"] = resources + (os.pathsep + existing if existing else "")
    argv = [BINARY, "-i"]
elif SHELL == "pwsh":
    # -Command runs after the profiles, so the script wraps whatever prompt they installed.
    # -NoLogo keeps the banner out of the capture; -NoExit is what makes it interactive after.
    script = os.path.join(resources, "pwsh", "integration.ps1")
    argv = [BINARY, "-NoLogo", "-NoExit", "-Command", f". '{script}'"]
else:
    sys.exit(f"unknown shell {SHELL}")

pid, fd = pty.fork()
if pid == 0:
    os.execvpe(argv[0], argv, env)

# A real terminal always tells the pty its size; a 0x0 window makes some line editors refuse to
# initialise, and PSReadLine is one of them.
import fcntl, struct, termios
fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 120, 0, 0))

# Modern shells interrogate the terminal before drawing anything — fish asks for the kitty keyboard
# flags, the terminal version, the background colour, terminfo capabilities and the device
# attributes, and then WAITS. A harness that stays silent leaves it blocked before its first prompt,
# which looks identical to integration that never loaded. So this answers, minimally but plausibly.
QUERIES = [
    # Answer each query ONCE. PowerShell's console asks where the cursor is (DSR 6, below) on
    # every prompt, and .NET consumes exactly one reply; a second one arrives when nobody is
    # waiting and PSReadLine reads it as keystrokes -- "1;1R" typed in front of every command, so
    # every command is a parse error and even `exit` fails. That was a duplicated entry in this
    # list, and it cost an afternoon, because the symptom looks like the shell being broken.
    (re.compile(rb"\x1b\[\?u"), b"\x1b[?0u"),                              # kitty keyboard flags
    (re.compile(rb"\x1b\[>0?q"), b"\x1bP>|check-shell-integration\x1b\\"),  # XTVERSION
    (re.compile(rb"\x1b\]11;\?(?:\x07|\x1b\\)"),
     b"\x1b]11;rgb:0000/0000/0000\x1b\\"),                                  # background colour
    (re.compile(rb"\x1bP\+q[0-9a-fA-F]+(?:;[0-9a-fA-F]+)*\x1b\\"),
     b"\x1bP0+q\x1b\\"),                                                   # XTGETTCAP: not supported
    (re.compile(rb"\x1b\[c|\x1b\[0c"), b"\x1b[?6c"),                        # primary device attributes
    (re.compile(rb"\x1b\[5n"), b"\x1b[0n"),                                 # device status
    (re.compile(rb"\x1b\[6n"), b"\x1b[1;1R"),                               # cursor position
]


def pump(fd, seconds, sink):
    """Read for a while, answering anything the shell asks."""
    end = time.time() + seconds
    while time.time() < end:
        # A real terminal answers a query in well under a millisecond. .NET's console reads the
        # reply to its cursor query with a short window, and a reply arriving after it lands in
        # the INPUT buffer as keystrokes -- "1;1R" typed into the command line. So the loop spins
        # tight rather than sleeping.
        ready, _, _ = select.select([fd], [], [], 0.005)
        if not ready:
            continue
        try:
            chunk = os.read(fd, 65536)
        except OSError:
            return False
        if not chunk:
            return False

        sink.append(chunk)
        for pattern, reply in QUERIES:
            if pattern.search(chunk):
                os.write(fd, reply)
    return True


chunks = []

PROMPT_END = re.compile(rb"\x1b\]133;B")


def pump_until_prompt(fd, seconds, sink, prompts_seen):
    """Pump until one more prompt-end mark (OSC 133;B) has arrived, or the ceiling passes."""
    end = time.time() + seconds
    while time.time() < end:
        if not pump(fd, 0.05, sink):
            return prompts_seen
        seen = sum(len(PROMPT_END.findall(c)) for c in sink)
        if seen > prompts_seen:
            return seen
    return prompts_seen


# Type only once the shell is at a prompt. A fixed startup wait typed the first command into a
# cold pwsh before PSReadLine was reading -- the first prompt drew and nothing ran after it,
# which is exactly what a broken script looks like. Pacing on the B mark instead is both robust
# on a slow machine and quick on a fast one; the ceiling is for a shell that never gets there.
seen = pump_until_prompt(fd, 30.0, chunks, 0)
pump(fd, 0.3, chunks)   # let the line editor finish initialising behind the prompt

# Two commands with different exit statuses, so D can be checked for carrying the right one.
# CR, not LF: Enter is 0x0D on the wire. The line discipline maps it for bash, zsh and fish, but
# PSReadLine puts the tty in raw mode and reads keys itself, and LF is Ctrl+J to it -- three
# commands sent with LF became one line that never ran.
os.write(fd, b"echo hello\r")
seen = pump_until_prompt(fd, 10.0, chunks, seen)
os.write(fd, b"false\r")
seen = pump_until_prompt(fd, 10.0, chunks, seen)
os.write(fd, b"exit\r")
pump(fd, 1.5, chunks)

out = b"".join(chunks)
text = out.decode("utf-8", "replace")

if os.environ.get("RAW"):
    # The stream itself, escapes made visible -- what to read when the marks are wrong.
    print(text.replace("\x1b", "<ESC>").replace("\x07", "<BEL>").replace("\r", "<CR>"))
    print("-" * 60)
marks = re.findall(r"\x1b\]133;([A-D])(?:;(\d+))?\x07", text)
cwd = re.findall(r"\x1b\](?:7|9;9);([^\x07]*)\x07", text)

print(f"shell    : {SHELL}  ({BINARY})")
print("133 marks:", " ".join(m[0] + (f"({m[1]})" if m[1] else "") for m in marks) or "NONE")
print("osc 7    :", cwd[0] if cwd else "NONE")
print()

kinds = {m[0] for m in marks}
missing = {"A", "B", "C", "D"} - kinds

if missing:
    print(f"missing: {' '.join(sorted(missing))}")
    print("A = prompt start, B = prompt end, C = command start, D = command finished")
    sys.exit(1)

statuses = [m[1] for m in marks if m[0] == "D"]
print(f"all four marks present; exit statuses reported: {', '.join(statuses)}")
print("expected 0 then 1, since the second command was `false`")
# The verdict CI runs on: every mark, the two statuses in order, and a directory report.
if statuses != ["0", "1"] or not cwd:
    print("FAIL: statuses or working directory not as expected")
    sys.exit(1)
