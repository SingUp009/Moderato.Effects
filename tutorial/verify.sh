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

actual=$("$@" 03)
expected=$'Preparing the order.\nOrder ready (quantity: 2).\nInvalid quantity: Quantity must be greater than zero.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 03 output did not match:\n%s\n' "$actual"
  exit 1
fi

actual=$("$@" 04)
expected=$'Preparing the order.\nConfirming the order.\nOrder confirmed (quantity: 2).\nOrder rejected: Quantity must be greater than zero.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 04 output did not match:\n%s\n' "$actual"
  exit 1
fi

actual=$("$@" 05)
expected=$'Preparing the order.\nOrder ready.\nCaptured: Preparing the order.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 05 output did not match:\n%s\n' "$actual"
  exit 1
fi

actual=$("$@" 06)
expected=$'Opening order draft.\nClosed order draft.\nOrder draft (quantity: 2).\nOpening order draft.\nClosed order draft.\nOrder rejected: Quantity must be greater than zero.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 06 output did not match:\n%s\n' "$actual"
  exit 1
fi

actual=$("$@" 07)
expected=$'Released shipping buffer.\nStandard shipping: $5.00.\nUnits available: 12\nReleased shipping buffer after cancellation.\nCheckout failed: Item is out of stock.'
if [[ "${actual//$'\r'/}" != "$expected" ]]; then
  printf 'Chapter 07 output did not match:\n%s\n' "$actual"
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
  [[ "$actual" == *"03 - Handling Expected Errors"* ]]
  [[ "$actual" == *"04 - Composing Effects"* ]]
  [[ "$actual" == *"05 - Managing Dependencies"* ]]
  [[ "$actual" == *"06 - Managing Resources"* ]]
  [[ "$actual" == *"07 - Running Effects Concurrently"* ]]
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
