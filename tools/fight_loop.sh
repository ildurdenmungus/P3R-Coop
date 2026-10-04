#!/usr/bin/env bash
# Press the battle keys (default Space Space = Attack) every 5 s until battle.log says "battle over" after the
# current line, the game is gone, or 5 minutes pass. Usage: fight_loop.sh [keys...]
R=/c/Users/mbasd/P3R-Coop/recon
b0=$(wc -l < "$R/battle.log")
d() {
  powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\mbasd\P3R-Coop\tools\focus.ps1" >/dev/null 2>&1
  um win drive --proc P3R "$@" >/dev/null 2>&1
}
for i in $(seq 1 60); do
  tail -n +$((b0 + 1)) "$R/battle.log" | grep -q "battle over" && { echo "BATTLE OVER $(date +%T)"; exit 0; }
  tasklist //FI "IMAGENAME eq P3R.exe" | grep -q P3R || { echo "GAME GONE $(date +%T)"; exit 1; }
  for k in ${@:-0x20 0x20}; do d "key $k"; sleep 1; done; sleep 3
done
echo "TIMEOUT $(date +%T)"
