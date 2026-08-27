# Shell integration for fish: OSC 133 prompt marks and OSC 7 working directory.
#
# Reached by adding this tree to XDG_DATA_DIRS. fish sources vendor_conf.d from every data directory
# on its own, so nothing of the user's is displaced and nothing has to be replayed — which makes
# this the pleasant one of the three.
#
# The mechanism follows Ghostty's, which is MIT licensed.

status is-interactive; or exit 0

# fish gives these as real events, so there is no prompt string to splice and no width to get wrong.

function __terminal_report_cwd --on-variable PWD --description 'report the working directory'
    # $hostname, not (hostname): the variable is built in, while the command forks a process —
    # and this runs on every directory change, in a shell whose whole appeal is being quick.
    # Only the percent needs escaping — see the note in the bash script. string replace is a
    # builtin, so this still forks nothing.
    printf '\033]7;file://%s%s\007' "$hostname" (string replace --all '%' '%25' -- "$PWD")
end

function __terminal_prompt_start --on-event fish_prompt --description 'mark the prompt'
    # D carries the status of the command that just finished. Nothing has run before the first
    # prompt, so there is nothing to report then.
    if set -q __terminal_command_running
        printf '\033]133;D;%s\007' $__terminal_last_status
        set -e __terminal_command_running
    end

    __terminal_report_cwd
    printf '\033]133;A\007'
end

function __terminal_preexec --on-event fish_preexec --description 'mark where output begins'
    set -g __terminal_command_running 1
    printf '\033]133;C\007'
end

function __terminal_postexec --on-event fish_postexec --description 'remember the exit status'
    set -g __terminal_last_status $status
end

# B marks the end of the prompt and the start of typing. Wrapping the user's fish_prompt rather than
# appending to a string, because fish builds its prompt from a function.
if not functions -q __terminal_original_fish_prompt
    functions -c fish_prompt __terminal_original_fish_prompt

    function fish_prompt --description 'fish_prompt, with the end of it marked'
        __terminal_original_fish_prompt
        printf '\033]133;B\007'
    end
end

# The first prompt has no preceding command, and the report above needs somewhere to read from.
set -g __terminal_last_status 0
