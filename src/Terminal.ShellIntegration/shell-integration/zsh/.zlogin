# Forwards to the user's own .zlogin.
#
# zsh reads all four of its startup files from ZDOTDIR, and ZDOTDIR points here so the integration
# in .zshrc can be reached. Providing only .zshrc would mean this one silently stopped being read,
# which is how injecting integration ends up looking like it broke someone's setup.
#
# ZDOTDIR is restored in .zshrc rather than here, because zsh reads .zshenv before .zshrc and
# changing it too early would send zsh looking for the remaining files in the wrong place.

if [[ -n "$TERMINAL_SHELL_ZDOTDIR" ]]; then
  __terminal_user_zdotdir="$TERMINAL_SHELL_ZDOTDIR"
else
  __terminal_user_zdotdir="$HOME"
fi

[[ -f "$__terminal_user_zdotdir/.zlogin" ]] && builtin source "$__terminal_user_zdotdir/.zlogin"
unset __terminal_user_zdotdir
