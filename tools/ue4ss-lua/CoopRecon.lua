-- CoopRecon (dev only): recon for the P3R co-op mod. Output goes to %USERPROFILE%/P3R-Coop/recon/
-- F6 = class census + floor fingerprint
-- Automatic: dungeon state-machine steps -> floor_events.log; fingerprint after every floor build.

local HOME = (os.getenv("USERPROFILE") or "C:/Users/Public"):gsub("\\", "/") .. "/P3R-Coop/"
local OUT = HOME .. "recon/"
local PATTERNS = { "Btl", "Battle", "Dng", "Dungeon", "Tartarus", "Encount", "Shadow", "Fld", "Party",
                   "Partner", "Player", "Command", "Turn", "Floor", "Map", "Pad", "Input", "Unit" }
local census_n, floor_n = 0, 0

local function append(file, line)
    local f = io.open(OUT .. file, "a")
    if f then f:write(line .. "\n"); f:close() end
end

local function now() return os.date("%H:%M:%S") end

local function world_name()
    local ok, name = pcall(function()
        local pc = FindFirstOf("PlayerController")
        if pc and pc:IsValid() then return pc:GetWorld():GetFullName() end
        return "?"
    end)
    return ok and name or "?"
end

local function census()
    census_n = census_n + 1
    local counts = {}
    ForEachUObject(function(obj)
        local ok, cls = pcall(function() return obj:GetClass():GetFName():ToString() end)
        if ok and cls then
            for _, p in ipairs(PATTERNS) do
                if string.find(cls, p, 1, true) then
                    counts[cls] = (counts[cls] or 0) + 1
                    break
                end
            end
        end
    end)
    local names = {}
    for k in pairs(counts) do names[#names + 1] = k end
    table.sort(names)
    local file = string.format("census_%02d.txt", census_n)
    append(file, "# " .. now() .. " world=" .. world_name())
    for _, k in ipairs(names) do append(file, string.format("%5d  %s", counts[k], k)) end
    print(string.format("[CoopRecon] census %d: %d classes -> %s\n", census_n, #names, file))
end

-- Floor fingerprint: every placed part (type/variation/direction/grid position), sorted, plus a hash.
local function fingerprint(reason)
    local parts = FindAllOf("FldDungeonPartActor")
    if not parts then
        append("floor_events.log", now() .. "  fingerprint(" .. reason .. "): no FldDungeonPartActor")
        return
    end
    local rows = {}
    for _, p in ipairs(parts) do
        local ok, row = pcall(function()
            local loc = p:K2_GetActorLocation()
            return string.format("%8.0f %8.0f %6.0f  type=%d var=%d dir=%d",
                loc.X, loc.Y, loc.Z, p.PartType, p.PartVariation, p.PartDirType)
        end)
        if ok then rows[#rows + 1] = row end
    end
    table.sort(rows)
    local h = 5381
    for _, r in ipairs(rows) do
        for i = 1, #r do h = (h * 33 + string.byte(r, i)) % 4294967296 end
    end
    floor_n = floor_n + 1
    local file = string.format("floor_%02d.txt", floor_n)
    append(file, string.format("# %s %s parts=%d hash=%08x", now(), reason, #rows, h))
    for _, r in ipairs(rows) do append(file, r) end
    append("floor_events.log", string.format("%s  fingerprint(%s) parts=%d hash=%08x -> %s",
        now(), reason, #rows, h, file))
end

RegisterKeyBind(Key.F6, function()
    ExecuteInGameThread(function()
        census()
        fingerprint("F6")
    end)
end)

-- Dungeon state machine + floor selection hooks (UFunctions; fire only if called through ProcessEvent).
local function hook(path, describe)
    local ok, err = pcall(function()
        RegisterHook(path, function(self, ...)
            local extra = ""
            if describe then
                local ok2, s = pcall(describe, self, ...)
                extra = ok2 and s or ("<" .. tostring(s) .. ">")
            end
            append("floor_events.log", now() .. "  " .. path:match(":(.+)$") .. extra)
        end)
    end)
    append("floor_events.log", now() .. "  hook " .. path .. (ok and " OK" or (" FAILED " .. tostring(err))))
end

local G = "/Script/xrd777.FldDungeonGameInit:"
for _, fn in ipairs({ "LOAD_START", "INIT_FLOOR_LEVEL", "INIT_FIXED_FLOOR", "INIT_DESIGIN_MAP", "FloorInit",
                      "DungeonInit", "CREATE_LAYOUT", "ADVANCE_PROCESS", "INIT_END" }) do
    hook(G .. fn)
end
hook("/Script/xrd777.FldDungeonControlActor:SetField", function(self, major, minor, idx)
    return string.format("(%d, %d, %d)", major:get(), minor:get(), idx:get())
end)
hook("/Script/xrd777.FldDungeonControlActor:SetNextFieldFloor")
-- AutoFloorCreateActor fires once per random floor (verified 2026-10-04); fingerprint after spawning settles.
pcall(function()
    RegisterHook("/Script/xrd777.FldDungeonEnemyController:AutoFloorCreateActor", function()
        append("floor_events.log", now() .. "  AutoFloorCreateActor")
        ExecuteWithDelay(2000, function() ExecuteInGameThread(function() fingerprint("AutoFloorCreateActor") end) end)
    end)
end)

-- CREATE_END = layout finished: fingerprint shortly after (let the part actors finish spawning).
pcall(function()
    RegisterHook(G .. "CREATE_END", function()
        append("floor_events.log", now() .. "  CREATE_END")
        ExecuteWithDelay(1500, function() ExecuteInGameThread(function() fingerprint("CREATE_END") end) end)
    end)
end)

-- Polling fingerprint: the floor's part actors are streamed in over several frames and not every floor goes
-- through a hookable UFunction, so every 2 s hash the current parts and record when the set settles on a new value.
local last_hash, pending_hash = nil, nil
local function floor_hash()
    local parts = FindAllOf("FldDungeonPartActor")
    if not parts or #parts == 0 then return nil, 0 end
    local rows = {}
    for _, p in ipairs(parts) do
        local ok, row = pcall(function()
            local loc = p:K2_GetActorLocation()
            return string.format("%.0f,%.0f,%d,%d,%d", loc.X, loc.Y, p.PartType, p.PartVariation, p.PartDirType)
        end)
        if ok then rows[#rows + 1] = row end
    end
    table.sort(rows)
    local h = 5381
    for _, r in ipairs(rows) do for i = 1, #r do h = (h * 33 + string.byte(r, i)) % 4294967296 end end
    return h, #rows
end

LoopAsync(2000, function()
    ExecuteInGameThread(function()
        local ok, h, n = pcall(floor_hash)
        if not ok or not h then pending_hash = nil; return end
        if h ~= last_hash and h == pending_hash then
            last_hash = h
            fingerprint(string.format("poll n=%d", n))
        end
        pending_hash = h
    end)
    return false
end)
-- File-drop command bridge (dev only): write one command per line to recon/cmd.txt; results go to bridge.log.
--   where | next | prev | setfield <major> <minor> <idx> | fp | census | eval <lua chunk>
local CMD = OUT .. "cmd.txt"
local function bridge_log(s) append("bridge.log", now() .. "  " .. s) end

local function control_actor()
    local a = FindFirstOf("FldDungeonControlActor")
    if a and a:IsValid() then return a end
    return nil
end

local commands = {}
commands.where = function()
    local a = control_actor()
    local h, n = floor_hash()
    return string.format("control=%s parts=%s hash=%s world=%s", a and a:GetFullName() or "none",
        tostring(n), h and string.format("%08x", h) or "-", world_name())
end
commands.next = function() local a = control_actor(); if not a then return "no FldDungeonControlActor" end; a:DebugSetNextFieldFloor(); return "DebugSetNextFieldFloor called" end
commands.prev = function() local a = control_actor(); if not a then return "no FldDungeonControlActor" end; a:DebugSetPrevFieldFloor(); return "DebugSetPrevFieldFloor called" end
commands.setfield = function(args)
    local a = control_actor(); if not a then return "no FldDungeonControlActor" end
    local M, m, i = args:match("(%-?%d+)%s+(%-?%d+)%s+(%-?%d+)")
    a:SetField(tonumber(M), tonumber(m), tonumber(i)); return "SetField(" .. M .. "," .. m .. "," .. i .. ")"
end
commands.fp = function() fingerprint("bridge"); return "fingerprint written" end
commands.census = function() census(); return "census written" end
commands.eval = function(args)
    local fn, err = load(args)
    if not fn then return "compile error: " .. tostring(err) end
    local ok, res = pcall(fn)
    return (ok and "ok " or "error ") .. tostring(res)
end

LoopAsync(500, function()
    local f = io.open(CMD, "r")
    if not f then return false end
    local text = f:read("*a"); f:close()
    if not text or text:match("^%s*$") then return false end
    local w = io.open(CMD, "w"); if w then w:close() end
    for line in text:gmatch("[^\r\n]+") do
        local name, args = line:match("^%s*(%S+)%s*(.*)$")
        local cmd = name and commands[name]
        if cmd then
            ExecuteInGameThread(function()
                local ok, res = pcall(cmd, args)
                bridge_log(line .. " -> " .. (ok and tostring(res) or ("ERROR " .. tostring(res))))
            end)
        else
            bridge_log(line .. " -> unknown command")
        end
    end
    return false
end)
append("floor_events.log", now() .. "  CoopRecon loaded")
print("[CoopRecon] loaded\n")

-- Game-side co-op scripts live in the same Lua state as the bridge, so bridge 'eval dofile' replaces them
-- (each file retires its old loops through a generation counter).
for _, f in ipairs({ "P3RCoop.lua", "BattleRecon.lua" }) do
    local ok, err = pcall(dofile, HOME .. "tools/ue4ss-lua/" .. f)
    if not ok then append("floor_events.log", now() .. "  load " .. f .. " FAILED: " .. tostring(err)) end
end
