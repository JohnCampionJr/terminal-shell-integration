# Terminal.ShellIntegration

Makes a shell tell the terminal what it is doing.

A terminal can only know where a prompt began, when a command started, and what it exited with if
the shell says so — that is OSC 133. Stock bash and zsh say nothing, so a host is left guessing.
Every terminal that wants those features injects integration into the shell it spawns; this is that
injection, and nothing else.

```csharp
var result = ShellIntegration.Prepare(
    shellPath: "/bin/zsh",
    args: [],
    environment: currentEnvironment,
    resourcesDirectory: "/path/to/shell-integration");

if (result.Injected)
    Spawn(shellPath, result.Args, result.Environment);
else
    Console.WriteLine($"left alone: {result.Skipped}");
```

That is the whole API. Arguments and environment in, arguments and environment out.

## What the shell then reports

| | |
|---|---|
| `OSC 133;A` | a prompt begins |
| `OSC 133;B` | the prompt ends and typing begins |
| `OSC 133;C` | a command starts, output follows |
| `OSC 133;D;<status>` | the command finished, with its exit status |
| `OSC 7` | the working directory |

Which buys jump-to-previous-prompt, click-to-select a command's output, exit status in the margin,
a new tab that opens where the old one was, and knowing whether the shell is busy without guessing.

## How each shell is reached

| shell | mechanism |
|---|---|
| **bash** | `--posix` plus `ENV` |
| **zsh** | `ZDOTDIR`, with all four startup files forwarded |
| **fish** | `XDG_DATA_DIRS` plus `vendor_conf.d`, which fish loads on its own |

The bash one is the only surprising one. `--init-file` looks like the obvious hook and is silently
ignored for a login shell, so it never runs for a great many people. POSIX mode sources `$ENV`
whatever kind of shell it is — the price being that the script must then replay bash's own startup
sequence, since bash skipped it.

The mechanism follows [Ghostty's](https://github.com/ghostty-org/ghostty), which is MIT licensed and
had already solved problems worth not rediscovering.

## Design

**No dependencies.** The library references nothing outside the BCL; the scripts use shell builtins
only, and fork no processes. There is no UI, no PTY and no terminal emulator here — the transform is
a pure function over strings, which is what makes the shell-specific quirks testable without
spawning anything.

**The host keeps two decisions**: whether to inject at all, since a user must be able to turn it
off, and where the scripts live, since only the host knows where its own files are. The scripts have
to be real files on disk, because a shell sources a path.

**Shells are left alone** when they are not recognised, when they were asked to run a command and
exit, when integration is already present — a shell spawned from an instrumented one would emit
every mark twice — and when the scripts are missing. Each case says which, because a shell that
reports nothing looks exactly like a shell that was never instrumented.

## Testing

```
dotnet test                                    # the transform: arguments and environment
dotnet build src/Terminal.ShellIntegration -c Release
python3 tools/check-shell-integration.py zsh   # a real shell, through a pty
python3 tools/check-shell-integration.py bash /opt/homebrew/bin/bash
python3 tools/check-shell-integration.py fish
```

Both halves are needed, and the second is not optional. Four bugs were found by running real shells
while the unit tests stayed green throughout — the C# side had the right answer every time:

- **zsh**: `local status=$?` silently breaks a function, because `status` is a special parameter in
  zsh aliased to `$?`. The prompt still drew; only the `A` and `D` marks went missing.
- **bash**: `PS0` reaches its hook through a command substitution, so the flag saying a command was
  running died with the subshell. `C` survived by accident — its output was captured into `PS0`'s
  value and printed as part of the prompt.
- **fish**: it searches each data directory for `fish/vendor_conf.d`, so naming the `fish` folder
  itself made it look for `fish/fish/vendor_conf.d` and load nothing, quietly.
- **OSC 7**: the path is unescaped by whoever reads it, so a directory named `a%2Fb` arrived as
  `a/b`. Only a well-formed escape corrupts — spaces and non-ASCII survive — so only the percent is
  escaped.

The harness answers terminal queries as it goes. Modern shells interrogate the terminal before
drawing anything — fish asks for the kitty keyboard flags, the terminal version, the background
colour, terminfo capabilities and the device attributes — and then wait. Silence leaves them blocked
before the first prompt, which again looks exactly like integration that never loaded.

Verified against zsh 5.9, bash 5.3.15 and fish 4.8.1. macOS ships bash 3.2 from 2007, which does not
source `$ENV` in POSIX mode at all; Ghostty documents the same limitation for the same bash.
