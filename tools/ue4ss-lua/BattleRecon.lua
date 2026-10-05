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
local coop_done = nil
local function coop_battle_start()
    local f = io.open(RUN .. "battle_coop.txt", "r")
    if not f then return end
    local line = f:read("*l") or ""; f:close()
    local id, slots = line:match("^(%-?%d+)%s*(.*)$")
    if not id or id == coop_done then return end
    coop_done = id
    if _G.COOP_AUTO_REMOTE == false or slots == "" then return end
    local db = FindFirstOf("BtlDataBaseComponent")
    if not valid(db) then log("  COOP no BtlDataBaseComponent"); return end
    for slot in slots:gmatch("P(%d)") do
        local i = tonumber(slot) + 1
        local ok, err = pcall(function()
            local actor = core.PlayerList[i]
            db:SetManualOperate(actor, false)
            log(string.format("  COOP battle %s: P%s (%s) set to automatic", id, slot, name(actor)))
        end)
        if not ok then log("  COOP SetManualOperate P" .. slot .. " failed: " .. tostring(err)) end
    end
end

local function poll()
    if not valid(core) then
        if last_turn then
            log("battle over / no core"); last_turn, last_ai, last_am = nil, nil, nil
            coop_done = nil
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
