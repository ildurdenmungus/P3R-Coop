-- CoopRecon: recon for the P3R co-op mod. Output goes to C:/Users/mbasd/P3R-Coop/recon/
-- F6 = class census + floor fingerprint
-- Automatic: dungeon state-machine steps -> floor_events.log; fingerprint after every floor build.

local OUT = "C:/Users/mbasd/P3R-Coop/recon/"
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
hook("/Script/xrd777.FldDungeonEnemyController:AutoFloorCreateActor")

-- CREATE_END = layout finished: fingerprint shortly after (let the part actors finish spawning).
pcall(function()
    RegisterHook(G .. "CREATE_END", function()
        append("floor_events.log", now() .. "  CREATE_END")
        ExecuteWithDelay(1500, function() ExecuteInGameThread(function() fingerprint("CREATE_END") end) end)
    end)
end)

append("floor_events.log", now() .. "  CoopRecon loaded")
print("[CoopRecon] loaded\n")
