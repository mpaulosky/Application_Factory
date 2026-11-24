#!/usr/bin/env bash
# How a run's stderr is held to a case's expected.stderr: one ERE per line, in order, where a line that is exactly ... stands for any
# number of lines (none included). Sourced by the parity runners (tests/parity.test.sh, tests/rollout-parity.test.sh); needs bash 4.4,
# grep and cat.

# stderr_fits <patterns file> <stderr file>: whether stderr fits patterns that hold a ... line, which matches any number of lines
# (none included). Glob matching with one backtrack point: a pattern line matches one line, and on a mismatch the last ...
# takes one more line.
stderr_fits() {
  local -a patterns=() lines=()
  local p=0 l=0 star=-1 mark=0
  mapfile -t patterns < "$1"
  mapfile -t lines < "$2"
  while ((l < ${#lines[@]})); do
    if ((p < ${#patterns[@]})) && [[ "${patterns[p]}" == ... ]]; then
      star=$p mark=$l p=$((p + 1))
    elif ((p < ${#patterns[@]})) && grep -qE -- "${patterns[p]}" <<< "${lines[l]}"; then
      p=$((p + 1)) l=$((l + 1))
    elif ((star >= 0)); then
      mark=$((mark + 1)) p=$((star + 1)) l=$mark
    else
      return 1
    fi
  done
  while ((p < ${#patterns[@]})) && [[ "${patterns[p]}" == ... ]]; do p=$((p + 1)); done
  ((p == ${#patterns[@]}))
}

# stderr_problems <patterns file> <stderr file>: prints the ways stderr doesn't fit its patterns, nothing when it does.
stderr_problems() {
  local -a patterns=() lines=()
  local i
  mapfile -t patterns < "$1"
  mapfile -t lines < "$2"
  if grep -qx -- '\.\.\.' "$1"; then
    if ! stderr_fits "$1" "$2"; then
      echo "stderr doesn't fit its patterns (a ... line stands for any number of lines):"
      cat -- "$2"
    fi
    return
  fi
  if [[ ${#lines[@]} -ne ${#patterns[@]} ]]; then
    echo "${#lines[@]} stderr line(s), expected ${#patterns[@]}:"
    cat -- "$2"
    return
  fi
  for ((i = 0; i < ${#patterns[@]}; i++)); do
    grep -qE -- "${patterns[i]}" <<< "${lines[i]}" || echo "stderr line $((i + 1)) '${lines[i]}' doesn't match '${patterns[i]}'"
  done
}
