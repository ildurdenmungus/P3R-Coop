#!/usr/bin/env bash
# Launch P3R and load the save the load menu opens on (the last one used; SLOT_UP=n moves up first), then wait
# until the player pawn is in the field. Same opening as run_battle.sh, without the Tartarus part.
set -u
P=/c/Users/mbasd/P3R-Coop
R=$P/recon
d() {
  powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\mbasd\P3R-Coop\tools\focus.ps1" >/dev/null 2>&1
  um win drive --proc P3R "$@" >/dev/null 2>&1
}
bridge() { echo "$1" > "$R/cmd.txt"; sleep 2; tail -1 "$R/bridge.log" | sed 's/.*-> //'; }
focus() { powershell -NoProfile -ExecutionPolicy Bypass -File $P/tools/shot.ps1 $P/shots/focus.png 0.1 >/dev/null; }

powershell -NoProfile -ExecutionPolicy Bypass -File "C:\\Users\\mbasd\\P3R-Coop\\tools\\launch.ps1"
sleep 20; focus
d "key 0x43"; sleep 5          # network features: No
d "key 0x20"; sleep 5          # "network disabled" OK
for i in 1 2 3 4; do d "key 0x0D"; sleep 4; done   # opening movie / intro / press any button
d "key 0x0D"; sleep 6          # title: Load
for i in $(seq 1 ${SLOT_UP:-0}); do d "key 0x26"; sleep 1; done
d "key 0x20"; sleep 3          # the slot
d "key 0x20"; sleep 3          # yes (load this save?)
d "key 0x20"
for i in $(seq 1 45); do sleep 2
  r=$(bridge 'eval for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then local l = a:K2_GetActorLocation() return string.format("player %.0f %.0f", l.X, l.Y) end end return "loading " .. os.time()')
  case "$r" in "ok player"*) echo "LOADED $(date +%T): $r"; exit 0;; esac
done
echo "LOAD TIMEOUT"; exit 1
