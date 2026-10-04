#!/usr/bin/env bash
# Drive P3R from the "enable network features?" prompt to a battle on Tartarus 214 (save slot 7 = Tartarus
# entrance, 27.12 Dark Hour). Needs the CoopRecon bridge (recon/cmd.txt) and um win drive.
# Keys: C = No/cancel, Enter = advance/talk, Space = confirm.
set -u
R=/c/Users/mbasd/P3R-Coop/recon
d() { um win drive --proc P3R "$@" >/dev/null 2>&1; }
bridge() { echo "$1" > "$R/cmd.txt"; sleep 2; tail -1 "$R/bridge.log" | sed 's/.*-> //'; }
# um win drive only types into the game when it is the foreground window: bring the "P3R" window forward
# (shot.ps1 minimizes UE4SS's debug window and focuses the game as a side effect).
powershell -NoProfile -ExecutionPolicy Bypass -File /c/Users/mbasd/P3R-Coop/tools/shot.ps1 /c/Users/mbasd/P3R-Coop/shots/focus.png 0.1 >/dev/null

d "key 0x43"; sleep 4          # network features: No
d "key 0x0D"; sleep 4          # skip opening movie
d "key 0x0D"; sleep 4          # press any button
d "key 0x0D"; sleep 3          # title: Load
d "key 0x20"; sleep 2          # slot 7 (last used, preselected)
d "key 0x20"; sleep 8          # load it: yes
for i in $(seq 1 20); do       # wait until the field player exists (loads take 10-30 s)
  r=$(bridge 'eval for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then return "player" end end return "loading"')
  [ "$r" = "ok player" ] && break; sleep 2
done
sleep 3
bridge 'eval for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then a:K2_SetActorLocation({X=1490,Y=-440,Z=87}, false, {}, true) return "at teleporter" end end return "no player"'
for i in 1 2 3 4 5 6; do d "key 0x0D"; sleep 2; done   # Fuuka: continue from 214
sleep 14
tail -1 "$R/floor_events.log"
bridge 'eval local p for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do if a:IsValid() and not a.bHidden then p = a end end local pl = p:K2_GetActorLocation() local best, bd for _, a in ipairs(FindAllOf("BP_FldEmNormal_C") or {}) do if a:IsValid() and not a.bHidden then local l = a:K2_GetActorLocation() local dd = (l.X-pl.X)^2+(l.Y-pl.Y)^2 if not bd or dd < bd then best, bd = a, dd end end end if not best then return "no shadow" end local l = best:K2_GetActorLocation() p:K2_SetActorLocation({X=l.X-120,Y=l.Y,Z=pl.Z}, false, {}, true) return "next to shadow"'
d "hold 0x57 600"; sleep 8
grep TURN "$R/battle.log" | tail -1 | cut -c1-80
