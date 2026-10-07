-- BattleRecon: logs how P3R battles flow — whose turn it is and what each unit decides to do.
-- Output: %USERPROFILE%/P3R-Coop/recon/battle.log. Load/reload with the bridge:
--   eval return dofile("C:/Users/mbasd/P3R-Coop/tools/ue4ss-lua/BattleRecon.lua")
--
-- Poll-only on purpose: UE4SS 3.0.1 crashed (2026-10-04) when Lua hooks were attached to
-- BtlActor:StandbyAction / ToEndTurn and to BtlActionManagerComponent:SetCommand* while a skill was chosen
-- (access violation reading 0x4abd inside the ProcessEvent hook path). Reading properties is safe.

local HOME = (os.getenv("USERPROFILE") or "C:/Users/Public"):gsub("\\", "/") .. "/P3R-Coop/"
local OUT = HOME .. "recon/battle.log"
local RUN = HOME .. "run/"
_G.BATTLERECON_GEN = (_G.BATTLERECON_GEN or 0) + 1
local GEN = _G.BATTLERECON_GEN

local function log(s)
    local f = io.open(OUT, "a")
    if f then f:write(os.date("%H:%M:%S") .. "  " .. s .. "\n"); f:close() end
end

local function valid(o) return o ~= nil and o:IsValid() end

local function name(o)
    local ok, s = pcall(function() return valid(o) and o:GetFName():ToString() or "nil" end)
    return ok and s or "?"
end

local function cls(o)
    local ok, s = pcall(function() return o:GetClass():GetFName():ToString() end)
    return ok and s or "?"
end

local function actors(arr)
    local out = {}
    pcall(function() arr:ForEach(function(_, e) out[#out + 1] = name(e:get()) end) end)
    return "[" .. table.concat(out, ",") .. "]"
end

-- One line describing a unit's AI decision fields (ABtlAI: ActionType/ActionID/SkillID/ItemID/targets).
local function decision(ai)
    if not valid(ai) then return "ai=none" end
    return string.format("ai=%s type=%d id=%d skill=%d item=%d minor=%d targets=%s", cls(ai), ai.ActionType,
        ai.ActionID, ai.ActionSkillID, ai.ActionItemID, ai.MinorParam, actors(ai.ActionTargets))
end

local last_turn, last_ai, last_am = nil, nil, nil
local core, am = nil, nil
local next_search = 0

-- Battle-control experiment (off unless _G.COOP_BOT_UNIT is set through the bridge, e.g.
--   eval _G.COOP_BOT_UNIT = "BP_BtlPc08_C"; _G.COOP_BOT_CMD = {type=5, id=0}
-- At the start of that unit's turn the mod issues the command itself through the action manager.
-- Called once per turn change (see poll), so the same unit is driven again every round.
local function bot(cur, turn)
    local want = _G.COOP_BOT_UNIT
    if not want or cls(cur) ~= want then return end
    local cmd = _G.COOP_BOT_CMD or { type = 5, id = 0 }
    local ok, err = pcall(function()
        am:SetCommand(cur, cmd.type, cmd.id)
        am:DoCommand()
    end)
    log(string.format("  BOT %s -> SetCommand(type=%d,id=%d)+DoCommand %s", turn, cmd.type, cmd.id,
        ok and "ok" or ("FAILED " .. tostring(err))))
end

-- Joint battles: the C# BattleHooks writes battle_coop.txt = "<battle id> <partner slots>", e.g. "4 P2,P3".
-- The partner's units are switched to automatic operation, so no command menu opens for them here (their AI
-- choice is held by BattleHooks and replaced by the partner's real command).
-- battle_coop.txt is written when the battle loads (C# PreLoad hook), so this runs before the first turn's menu.
-- When done, battle_ready.txt tells the C# side (the joiner then reports "INBATTLE" to the starter, who waits for
-- that before its first action so both games start the battle together).
-- Party slots that don't exist (a 3-member party has no P3) are skipped: SetManualOperate(nil) crashed the game.
local coop_done = nil
local function coop_battle_start()
    local f = io.open(RUN .. "battle_coop.txt", "r")
    if not f then return end
    local line = f:read("*l") or ""; f:close()
    local id, slots = line:match("^(%-?%d+)%s*(.*)$")
    if not id or id == coop_done then return end
    local n = 0
    pcall(function() n = #core.PlayerList end)
    if n == 0 then return end                         -- party not spawned yet: next poll
    coop_done = id
    if _G.COOP_AUTO_REMOTE ~= false and slots ~= "" then
        local db = FindFirstOf("BtlDataBaseComponent")
        if not valid(db) then log("  COOP no BtlDataBaseComponent") end
        for slot in slots:gmatch("P(%d)") do
            local i = tonumber(slot) + 1
            if i > n then
                log(string.format("  COOP battle %s: P%s not in this party (%d members), skipped", id, slot, n))
            elseif valid(db) then
                local ok, err = pcall(function()
                    local actor = core.PlayerList[i]
                    if not valid(actor) then error("no actor") end
                    db:SetManualOperate(actor, false)
                    log(string.format("  COOP battle %s: P%s (%s) set to automatic", id, slot, name(actor)))
                end)
                if not ok then log("  COOP SetManualOperate P" .. slot .. " failed: " .. tostring(err)) end
            end
        end
    end
    local rf = io.open(RUN .. "battle_ready.txt", "w")
    if rf then rf:write(id); rf:close() end
end

-- Shuffle Time (card choice after some battles): the host chooses. Host: cursor moves -> "SHUFFLECUR i", the
-- chosen card -> "SHUFFLEPICK i" (lua_out.txt, sent by the link). Guest: shuffle.txt ("cur i" / "pick i", from the
-- C# side) puts our cursor on the host's card. Everything seen is logged (first real use, 2026-10-07).
local SHUFFLE_CLASSES = { "BtlShuffleMainBase", "BP_BtlShuffleMain_C", "BP_BtlShuffle_C", "BtlShuffleMain" }
local shuffle, shuffle_seen, shuffle_next, shuffle_last = nil, nil, 0, nil
local function role()
    local f = io.open(RUN .. "peer.txt", "r")
    if not f then return nil end
    local r, up = (f:read("*l") or ""):match("^(%a+)%s+(%d)"); f:close()
    return up == "1" and r or nil
end
local function send(line)
    local f = io.open(RUN .. "lua_out.txt", "a")
    if f then f:write(line .. "\n"); f:close() end
end
local function shuffle_sync()
    if not valid(shuffle) then
        if os.clock() < shuffle_next then return end
        shuffle_next = os.clock() + 1                 -- FindFirstOf scans every object: once a second at most
        for _, c in ipairs(SHUFFLE_CLASSES) do
            local s = FindFirstOf(c)
            if valid(s) then shuffle = s; log("  SHUFFLE found " .. cls(s)); break end
        end
        if not valid(shuffle) then return end
    end
    local ok, cur, pick, n, en = pcall(function()
        return shuffle.SelectCoursorIndex, shuffle.ActionCardIndex, shuffle.SelectedCardNum, shuffle.EnableSelectNum
    end)
    if not ok then return end
    local state = string.format("cursor=%s action=%s selected=%s enable=%s", tostring(cur), tostring(pick), tostring(n), tostring(en))
    if state ~= shuffle_seen then
        shuffle_seen = state
        log("  SHUFFLE " .. state)
        local r = role()
        if r == "host" then
            send("SHUFFLECUR " .. tostring(cur))
            if n and n > 0 then send("SHUFFLEPICK " .. tostring(cur)) end
        end
    end
    if role() == "guest" then
        local f = io.open(RUN .. "shuffle.txt", "r")
        if f then
            local what, idx = (f:read("*l") or ""):match("^(%a+)%s+(%-?%d+)"); f:close()
            idx = tonumber(idx)
            if idx and idx >= 0 and cur ~= idx then
                pcall(function() shuffle.SelectCoursorIndex = idx end)
                log("  SHUFFLE guest cursor -> host's card " .. idx .. " (" .. tostring(what) .. ")")
            end
        end
    end
end

-- All-Out Attack prompt (C# AlloutSync): the partner answered it -> press the same button here.
-- BtlInputComponent:CallInputTrigKeyboard(key) fires the battle input delegate that key would: 2 (RR) = TrigOK
-- = "Go", 4 (RD) = TrigCANCEL (read from the native, P3R.exe+0x104B3B0: key index -> TrigRU/TrigOK/TrigRL/TrigCANCEL).
-- It must be the fighting phase's input (BP_BtlMainConductor_C.BtlInput): FindFirstOf("BtlInputComponent") gives
-- the GUI core's one, which the prompt ignores (2026-10-08).
-- One press, 1 s after the file appears (the prompt may bind its input a moment after it opens). No retry: the game
-- reports "Go" (DoGeneralAttack) only seconds later, after the intro, so a second OK would land in the All-Out.
-- If the press came too early the player answers by hand. C# deletes the file once the game took an answer.
local allout_seen, allout_pressed = nil, false
local function battle_input()
    local ph = FindFirstOf("BP_BtlPhaseFighting_C")
    if valid(ph) and valid(ph.BtlInput) then return ph.BtlInput end
    for _, i in ipairs(FindAllOf("BtlInputComponent") or {}) do
        if valid(i) and i:GetFullName():find("MainConductor", 1, true) then return i end
    end
end
local function allout_force()
    local f = io.open(RUN .. "allout_force.txt", "r")
    if not f then allout_seen, allout_pressed = nil, false; return end
    local what = f:read("*l") or ""; f:close()
    local now = os.clock()
    if not allout_seen then allout_seen = now; return end
    if allout_pressed or now - allout_seen < 1.0 then return end
    allout_pressed = true
    local input = battle_input()
    if not valid(input) then log("  ALLOUT no battle input component"); return end
    local key = what == "go" and 2 or 4
    local ok, err = pcall(function() input:CallInputTrigKeyboard(key) end)
    log(string.format("  ALLOUT pressed %s for the partner (key %d) %s", what, key, ok and "ok" or ("FAILED " .. tostring(err))))
end

-- Camera diagnostics (user 2026-10-08: on the partner's turn the camera "points at wrong thing"): who the battle
-- camera manager frames and what the action's target manager holds, logged when it changes. (ABtlCamera is never
-- spawned; the camera is BP_BtlCameraManagerComponent_C. Searched at most every 2 s: FindFirstOf scans every
-- UObject, and a per-poll search made the camera hitch before.)
local cam, last_cam, cam_next = nil, nil, 0
local function camera_log()
    if not valid(cam) then
        if os.clock() < cam_next then return end
        cam_next = os.clock() + 2
        cam = FindFirstOf("BP_BtlCameraManagerComponent_C")
        if not valid(cam) then return end
    end
    local tm = valid(am) and am.TargetsManager or nil
    local s = string.format("cam main=%s anim=%s | tm main=%s targets=%s cursor=%s", name(cam.MainCharacter),
        tostring(cam.IsCameraAnim), valid(tm) and name(tm.MainCharacter) or "-", valid(tm) and actors(tm.Targets) or "-",
        valid(tm) and actors(tm.CursorTargets) or "-")
    if s ~= last_cam then last_cam = s; log("  CAM " .. s) end
end

local function poll()
    _G.P3RCOOP_IN_BATTLE = valid(core)               -- the field scripts pause their object scans meanwhile
    if not valid(core) then
        if last_turn then
            log("battle over / no core"); last_turn, last_ai, last_am = nil, nil, nil
            cam, last_cam, cam_next = nil, nil, 0
            coop_done = nil
            _G.P3RCOOP_BATTLE_ENDS = (_G.P3RCOOP_BATTLE_ENDS or 0) + 1   -- P3RCoop.lua re-dresses the pawns
            shuffle, shuffle_seen = nil, nil
            local f = io.open(RUN .. "battle_end.txt", "w"); if f then f:write(os.date("%H:%M:%S")); f:close() end
        end
        -- FindFirstOf scans every UObject: outside battles only look again every 2 s
        if os.clock() < next_search then return end
        next_search = os.clock() + 2
        core = FindFirstOf("BtlCoreComponent")
        am = FindFirstOf("BtlActionManagerComponent")
        if not valid(core) then return end
    end
    coop_battle_start()
    local ok_c, err_c = pcall(camera_log)
    if not ok_c and last_cam ~= "error" then last_cam = "error"; log("  CAM error " .. tostring(err_c)) end
    local ok_a, err_a = pcall(allout_force)
    if not ok_a then log("  ALLOUT error " .. tostring(err_a)) end
    -- Shuffle Time only comes after the result: no object scan for it during the fight (camera hitches).
    local finished = false
    pcall(function() finished = core.BattleResult ~= 0 end)
    if finished or valid(shuffle) then
        local ok_s, err_s = pcall(shuffle_sync)
        if not ok_s then log("  SHUFFLE error " .. tostring(err_s)) end
    end
    local cur = core.CurrentAction
    local turn = name(cur)
    if turn ~= last_turn then
        last_turn, last_ai = turn, nil
        if valid(cur) then
            log(string.format("TURN %s (%s) enemy=%s encount=%d preemptive=%d players=%s enemies=%s", turn, cls(cur),
                tostring(cur.EnemyFlag), core.EncountParameter.EncountID, core.EncountParameter.Preemptive,
                actors(core.PlayerList), actors(core.EnemyList)))
            if valid(am) then bot(cur, turn) end
        end
    end
    if valid(cur) then
        local ai = cur.AI
        local d = decision(valid(ai) and ai or nil)
        if d ~= last_ai then last_ai = d; log("  DECIDE " .. turn .. " " .. d) end
    end
    -- C# BattleHooks held a remote unit's DoCommand; when the other player's command has arrived it writes
    -- kick.txt and we call DoCommand again from the game thread so the hook can apply it and let it run.
    if valid(am) then
        local kf = io.open(RUN .. "kick.txt", "r")
        if kf then
            local what = kf:read("*l"); kf:close(); os.remove(RUN .. "kick.txt")
            local ok, err = pcall(function() am:DoCommand() end)
            log("  KICK " .. tostring(what) .. " -> DoCommand " .. (ok and "ok" or ("FAILED " .. tostring(err))))
        end
    end
    if valid(am) then
        local s2 = string.format("commander=%s type=%d id=%d targets=%s", name(am.ActionCommander), am.ActionType,
            am.ActionID, actors(am.ActionTargets))
        if s2 ~= last_am then last_am = s2; log("  ACTMGR " .. s2) end
    end
end

LoopAsync(100, function()
    if _G.BATTLERECON_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, err = pcall(poll)
        if not ok then log("poll error: " .. tostring(err)) end
    end)
    return false
end)

log("BattleRecon (poll-only) loaded gen " .. GEN)
return "BattleRecon loaded gen " .. GEN
