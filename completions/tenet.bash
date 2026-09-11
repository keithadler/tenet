# bash completion for tenet. Install: source this file, or drop it in /etc/bash_completion.d/
_tenet() {
  local cur prev cmds
  cur="${COMP_WORDS[COMP_CWORD]}"
  prev="${COMP_WORDS[COMP_CWORD-1]}"
  cmds="check axioms why audit statement compare crosscheck show info version help"

  if [ "$COMP_CWORD" -eq 1 ]; then
    COMPREPLY=( $(compgen -W "$cmds" -- "$cur") )
    return
  fi

  case "${COMP_WORDS[1]}" in
    check)
      COMPREPLY=( $(compgen -W "--all --only --jobs --fail-on-axiom --report --stats --slow --verbose --low-memory --fail-fast --quiet --timing --no-compare --lib --stack-mb --help" -- "$cur") )
      ;;
    audit)      COMPREPLY=( $(compgen -W "--limit --json --help" -- "$cur") ) ;;
    axioms|why|statement) COMPREPLY=( $(compgen -W "--json --help" -- "$cur") ) ;;
    compare)    COMPREPLY=( $(compgen -W "--show-types --json --help" -- "$cur") ) ;;
    crosscheck) COMPREPLY=( $(compgen -W "--show --names-out --json --help" -- "$cur") ) ;;
    *)          COMPREPLY=() ;;
  esac
  # always offer files too, since every command takes one
  COMPREPLY+=( $(compgen -f -X '!*.olean' -- "$cur") $(compgen -f -X '!*.ndjson' -- "$cur") $(compgen -d -- "$cur") )
}
complete -F _tenet tenet
