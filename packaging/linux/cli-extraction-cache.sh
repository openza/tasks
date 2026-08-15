#!/usr/bin/env sh

openza_cli_cache_is_usable() {
  candidate="$1"
  [ -n "$candidate" ] || return 1
  [ ! -L "$candidate" ] || return 1
  mkdir -p "$candidate" 2>/dev/null || return 1
  [ -d "$candidate" ] && [ ! -L "$candidate" ] || return 1
  [ "$(stat -c %u "$candidate" 2>/dev/null)" = "$(id -u)" ] || return 1
  chmod 700 "$candidate" 2>/dev/null || return 1

  probe="$candidate/.openza-write-test-$$"
  mkdir "$probe" 2>/dev/null || return 1
  rmdir "$probe" 2>/dev/null || return 1
}

configure_openza_cli_extraction_cache() {
  [ -z "${DOTNET_BUNDLE_EXTRACT_BASE_DIR:-}" ] || return 0

  primary="${XDG_CACHE_HOME:-${HOME:-}/.cache}/openza/cli"
  if openza_cli_cache_is_usable "$primary"; then
    cache_root="$primary"
  else
    cache_root="${TMPDIR:-/tmp}/openza-cli-$(id -u)"
    if ! openza_cli_cache_is_usable "$cache_root"; then
      echo "Openza CLI could not create a private writable extraction cache." >&2
      return 1
    fi
  fi

  DOTNET_BUNDLE_EXTRACT_BASE_DIR="$cache_root"
  export DOTNET_BUNDLE_EXTRACT_BASE_DIR
}
