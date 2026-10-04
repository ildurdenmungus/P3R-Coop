#!/usr/bin/env bash
# One scripted battle for the determinism test: launch P3R, load slot 7, teleport to Tartarus 214, walk into the
# nearest Shadow, fight with "Space Space" (attack) every 5 s until the battle ends, then close the game.
# Usage: run_battle.sh <label>   -> recon/run_<label>_coop.log, recon/run_<label>_battle.log
set -u
L=${1:-x}
P=/c/Users/mbasd/P3R-Coop
R=$P/recon
# Every key: bring the game to the front first (um win drive only types into the foreground window).
# (No idle-based "human is here" check: our own SendInput keys reset the Windows idle timer too.)
d() {
  powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\mbasd\P3R-Coop\tools\focus.ps1" >/dev/null 2>&1
  um win drive --proc P3R "$@" >/dev/null 2>&1
}
bridge() { echo "$1" > "$R/cmd.txt"; sleep 2; tail -1 "$R/bridge.log" | sed 's/.*-> //'; }
focus() { powershell -NoProfile -ExecutionPolicy Bypass -File $P/tools/shot.ps1 $P/shots/focus.png 0.1 >/dev/null; }

powershell -NoProfile -ExecutionPolicy Bypass -File "C:\\Users\\mbasd\\P3R-Coop\\tools\\launch.ps1"
start=$(date +%T)
sleep 20; focus
d "key 0x43"; sleep 5          # network features: No
d "key 0x20"; sleep 5          # "network disabled" OK
for i in 1 2 3 4; do d "key 0x0D"; sleep 4; done   # opening movie / intro / press any button
d "key 0x0D"; sleep 3          # title: Load
d "key 0x20"; sleep 3          # slot 7
d "key 0x20"; sleep 3          # yes (load this save?)
d "key 0x20"                   # again in case the dialog was late (harmless while loading)
loaded=0
for i in $(seq 1 45); do sleep 2      # loads take 15 s .. 3 min; the bridge answers late while loading
  r=$(bridge 'eval for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then return "player " .. os.time() end end return "loading " .. os.time()')
  case "$r" in "ok player"*) loaded=1; break;; esac
done
[ $loaded = 1 ] || { echo "LOAD TIMEOUT"; exit 1; }
sleep 5
bridge 'eval for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then a:K2_SetActorLocation({X=1490,Y=-440,Z=87}, false, {}, true) return "at teleporter" end end return "no player"'
for i in 1 2 3 4 5 6; do d "key 0x0D"; sleep 2; done
sleep 14
tail -1 "$R/floor_events.log"
# First-spawned Shadow (FindAllOf follows object-index order = spawn order, deterministic per floor), not the
# nearest one: Shadows wander, so "nearest" depends on timing and gives different encounters between runs.
bridge 'eval local p for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then p = a end end local pl = p:K2_GetActorLocation() local first for _, a in ipairs(FindAllOf("BP_FldEmNormal_C") or {}) do if a:IsValid() and not a.bHidden and a:GetClass():GetFName():ToString() == "BP_FldEmNormal_C" then first = a break end end if not first then return "no shadow" end local l = first:K2_GetActorLocation() p:K2_SetActorLocation({X=l.X-120,Y=l.Y,Z=pl.Z}, false, {}, true) return string.format("next to %s (%s) at %.0f %.0f", first:GetFName():ToString(), first:GetClass():GetFName():ToString(), l.X, l.Y)'
d "hold 0x57 600"; sleep 8
grep -q "TURN" <(awk -v s="$start" '$1>=s' "$R/battle.log") || { echo "NO BATTLE STARTED"; exit 1; }
if [ "${NOLOOP:-0}" = 1 ]; then echo "IN BATTLE (NOLOOP) $(date +%T)"; exit 0; fi
for i in $(seq 1 60); do
  last=$(tail -1 "$R/battle.log")
  if echo "$last" | grep -q "battle over"; then echo "BATTLE OVER $(date +%T)"; break; fi
  tasklist //FI "IMAGENAME eq P3R.exe" | grep -q P3R || { echo "GAME GONE $(date +%T)"; break; }
  d "key 0x20"; sleep 1; d "key 0x20"; sleep 4
done
sleep 4
awk -v s="$start" '$1>=s' "$R/coop.log" > "$R/run_${L}_coop.log"
awk -v s="$start" '$1>=s' "$R/battle.log" > "$R/run_${L}_battle.log"
pid=$(tasklist //FI "IMAGENAME eq P3R.exe" //FO CSV //NH | head -1 | cut -d'"' -f4)
[ -n "$pid" ] && powershell -NoProfile -Command "Stop-Process -Id $pid -Confirm:\$false" && echo "closed game pid $pid"
echo "run $L done"
