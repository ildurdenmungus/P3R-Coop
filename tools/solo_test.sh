#!/usr/bin/env bash
# One-PC checks for things a real partner would show (2026-10-05, after the first 2-PC session).
#   solo_test.sh move     mirror partner (own moves echoed 0.25 s late, +250 X): walk a pattern on floor 214,
#                         report the ghost tracking lines from run/game.log
#   solo_test.sh floor N  bot partner as host with floor seed N: go to 214, log the layout params + fingerprint
# Launches the game itself (slot 7 = newest save -> Tartarus 214) and leaves it running.
set -u
P=/c/Users/mbasd/P3R-Coop
R=$P/recon
LINK=$P/src/P3RCoopLink/bin/Release/net8.0/P3RCoopLink.exe
d() {
  powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\mbasd\P3R-Coop\tools\focus.ps1" >/dev/null 2>&1
  um win drive --proc P3R "$@" >/dev/null 2>&1
}
stop_link() {
  for pid in $(tasklist //FI "IMAGENAME eq P3RCoopLink.exe" //FO CSV //NH | cut -d'"' -f4); do
    powershell -NoProfile -Command "Stop-Process -Id $pid -Force" >/dev/null 2>&1
  done
  rm -f "$P/run/peer.txt" "$P/run/remote.txt" "$P/run/bot_cmd.txt"
}

case "${1:-}" in
move)
  stop_link
  (P3RCOOP_DIR='C:\Users\mbasd\P3R-Coop\run' "$LINK" mirror > "$R/link_test.log" 2>&1 &)
  sleep 2
  g0=$(cat "$P/run/game.log" 2>/dev/null | wc -l)
  tasklist //FI "IMAGENAME eq P3R.exe" | grep -q P3R || STOP_AT_FLOOR=1 bash "$P/tools/run_battle.sh" move | tail -1
  sleep 6
  for k in "hold 0x57 3000" "hold 0x41 2000" "hold 0x53 3000" "hold 0x44 2000" "hold 0x57 1500"; do d "$k"; sleep 0.5; done
  sleep 6
  tail -n +$((g0 + 1)) "$P/run/game.log" | grep -E "ghost|tracking|looks"
  ;;
floor)
  stop_link
  pid=$(tasklist //FI "IMAGENAME eq P3R.exe" //FO CSV //NH | head -1 | cut -d'"' -f4)
  [ -n "$pid" ] && powershell -NoProfile -Command "Stop-Process -Id $pid -Force" && sleep 4   # floors build on arrival
  (P3RCOOP_DIR='C:\Users\mbasd\P3R-Coop\run' "$LINK" bot guest > "$R/link_test.log" 2>&1 &)
  sleep 2
  echo "floorseed ${2:-777}" > "$P/run/bot_cmd.txt"
  c0=$(cat "$R/coop.log" 2>/dev/null | wc -l); f0=$(cat "$R/floor_events.log" 2>/dev/null | wc -l)
  STOP_AT_FLOOR=1 bash "$P/tools/run_battle.sh" floor | tail -1
  tail -n +$((c0 + 1)) "$R/coop.log" | grep -E "floor (layout|seed)"
  tail -n +$((f0 + 1)) "$R/floor_events.log" | grep fingerprint
  ;;
*)
  echo "usage: solo_test.sh move | floor <seed>"
  ;;
esac
