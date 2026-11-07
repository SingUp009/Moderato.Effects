#!/usr/bin/env bash
set -euo pipefail

actual=$("$@" 01)
expected=$'Accepted quantity: 2\nInvalid quantity: Quantity must be greater than zero.\nInvalid quantity: Quantity must be greater than zero.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 01 output did not match:\n%s\n' "$actual"
  exit 1
fi

actual=$("$@" 02)
expected=$'Effect created.\nPreparing the order.\nOrder ready.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 02 output did not match:\n%s\n' "$actual"
  exit 1
fi

for mode in help no-arguments; do
  if [[ "$mode" == help ]]; then
    actual=$("$@" --help)
  else
    actual=$("$@")
  fi
  [[ "$actual" == *"01 - Success and Failure with Result"* ]]
  [[ "$actual" == *"02 - Your First Effect"* ]]
done

if "$@" 99; then
  printf 'An unknown chapter must fail.\n'
  exit 1
fi

if "$@" 01 extra; then
  printf 'Extra arguments must fail.\n'
  exit 1
fi

printf 'Tutorial output and argument checks passed.\n'
