# Shell integration for zsh: OSC 133 prompt marks and OSC 7 working directory.
#
# Reached by pointing ZDOTDIR here. zsh reads four files from ZDOTDIR, so all four exist beside this
# one and forward to the user's — providing only .zshrc would silently drop the rest of their setup.
#
# The mechanism follows Ghostty's, which is MIT licensed.

# Put ZDOTDIR back FIRST. Anything the user's own files spawn must see their directory, not this
# one, or a nested zsh reads this instead of their configuration.
if [[ -n "$TERMINAL_SHELL_ZDOTDIR" ]]; then
  ZDOTDIR="$TERMINAL_SHELL_ZDOTDIR"
else
  ZDOTDIR="$HOME"
fi

[[ -f "$ZDOTDIR/.zshrc" ]] && builtin source "$ZDOTDIR/.zshrc"

# ---- the integration itself ------------------------------------------------------------------

autoload -Uz add-zsh-hook

__terminal_report_cwd() {
  # Only the percent needs escaping. The consumer unescapes what arrives, so a directory whose
  # name contains a valid-looking sequence -- a%2Fb -- would come back as a/b, silently wrong.
  # Everything else survives unescaping untouched, so encoding it would be work for nothing.
  builtin printf '\033]7;file://%s%s\007' "${HOST:-}" "${PWD//\%/%25}"
}

# Before each prompt: the status of the command that just finished, then the new prompt.
__terminal_precmd() {
  # NOT named "status": in zsh that is a special parameter aliased to $?, and declaring it local
  # makes this function fail silently — the prompt still appeared and only the A and D marks went
  # missing, which is a hard symptom to read backwards.
  local __terminal_status=$?

  if [[ -n "$__terminal_command_running" ]]; then
    builtin printf '\033]133;D;%s\007' "$__terminal_status"
    unset __terminal_command_running
  fi

  __terminal_report_cwd
  builtin printf '\033]133;A\007'
}

# Before a command runs: the prompt is over and output begins.
__terminal_preexec() {
  __terminal_command_running=1
  builtin printf '\033]133;C\007'
}

# add-zsh-hook appends, so the user's own precmd and preexec keep running.
add-zsh-hook precmd __terminal_precmd
add-zsh-hook preexec __terminal_preexec

# B marks where the prompt ends and typing begins. %{ %} tells zsh the sequence occupies no columns;
# without it the prompt's measured width is wrong and line wrapping goes with it.
if [[ "$PS1" != *'133;B'* ]]; then
  PS1="$PS1"$'%{\033]133;B\007%}'
fi
