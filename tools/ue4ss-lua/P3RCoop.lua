-- P3RCoop game side (UE4SS Lua). Talks to P3RCoopLink through two small files in RUN:
--   local.txt  (we write)  "<seq> <floor> <x> <y> <z> <yaw>"
--   remote.txt (we read)   the other player's line, same format
-- The other player is shown by borrowing one of our party's partner pawns (Duo B) and moving it.
-- Re-running this file (bridge: eval dofile(...)) replaces the previous instance cleanly.

local RUN = "C:/Users/mbasd/P3R-Coop/run/"
local TICK_MS = 50
local GHOST_PARTNER_INDEX = -1      -- which BP_FldPartner_C to borrow (-1 = last one)

_G.P3RCOOP_GEN = (_G.P3RCOOP_GEN or 0) + 1
local GEN = _G.P3RCOOP_GEN
local seq, floor_id = 0, 0
local player, ghost = nil, nil
local last_remote, remote_age = nil, 999
local target = nil            -- latest network position of the other player {X,Y,Z,Yaw}

local function log(s)
    local f = io.open(RUN .. "game.log", "a")
    if f then f:write(os.date("%H:%M:%S") .. "  " .. s .. "\n"); f:close() end
end

local function valid(o) return o ~= nil and o:IsValid() end

local function find_player()
    for _, a in ipairs(FindAllOf("BP_FldPlayerDUNGEON_C") or {}) do
        if valid(a) and not a.bHidden then return a end
    end
    return nil
end

local function find_ghost()
    local list = {}
    for _, a in ipairs(FindAllOf("BP_FldPartner_C") or {}) do
        if valid(a) and not a.bHidden then list[#list + 1] = a end
    end
    if #list == 0 then return nil end
    local i = GHOST_PARTNER_INDEX < 0 and (#list + 1 + GHOST_PARTNER_INDEX) or (GHOST_PARTNER_INDEX + 1)
    return list[math.max(1, math.min(#list, i))]
end

-- Cheap floor id: hash of the part actors' grid positions and types, refreshed every 2 s.
local function compute_floor_id()
    local parts = FindAllOf("FldDungeonPartActor")
    if not parts then return 0 end
    local rows = {}
    for _, p in ipairs(parts) do
        local ok, row = pcall(function()
            local l = p:K2_GetActorLocation()
            return string.format("%.0f,%.0f,%d,%d", l.X, l.Y, p.PartType, p.PartVariation)
        end)
        if ok then rows[#rows + 1] = row end
    end
    table.sort(rows)
    local h = 5381
    for _, r in ipairs(rows) do for i = 1, #r do h = (h * 33 + string.byte(r, i)) % 4294967296 end end
    return h
end

local function write_local(line)
    local f = io.open(RUN .. "local.txt", "w")
    if f then f:write(line .. "\n"); f:close() end
end

local function read_remote()
    local f = io.open(RUN .. "remote.txt", "r")
    if not f then return nil end
    local line = f:read("*l"); f:close()
    return line
end

local function tick()
    if not valid(player) then player = find_player(); if not valid(player) then return end end

    local l = player:K2_GetActorLocation()
    local r = player:K2_GetActorRotation()
    seq = seq + 1
    write_local(string.format("%d %u %.0f %.0f %.0f %.0f", seq, floor_id, l.X, l.Y, l.Z, r.Yaw))

    local line = read_remote()
    if not line or line == last_remote then remote_age = remote_age + 1; return end
    last_remote, remote_age = line, 0
    local rseq, rfloor, x, y, z, yaw = line:match("^(%d+) (%d+) (%-?%d+) (%-?%d+) (%-?%d+) (%-?%d+)")
    if not rseq then return end
    if tonumber(rfloor) ~= floor_id then return end   -- other player is on another floor: leave the ghost alone

    target = { X = tonumber(x), Y = tonumber(y), Z = tonumber(z), Yaw = tonumber(yaw) }
end

-- The borrowed partner normally follows our leader; switch that logic off so it only obeys the network.
local FOLLOW_COMPONENTS = { "/Script/xrd777.FldFollowerComp", "/Script/xrd777.FldPartnerComp" }
local function take_over(a)
    for _, path in ipairs(FOLLOW_COMPONENTS) do
        local cls = StaticFindObject(path)
        if cls and cls:IsValid() then
            -- K2_GetComponentsByClass returns a UE4SS TArray (not a Lua table): walk it with ForEach + get()
            local comps = a:K2_GetComponentsByClass(cls)
            if comps then
                comps:ForEach(function(_, elem)
                    local c = elem:get()
                    if c and c:IsValid() then
                        pcall(function() c:SetComponentTickEnabled(false) end)
                        log("ghost: disabled " .. c:GetClass():GetFName():ToString())
                    end
                end)
            end
        end
    end
    local ctrl = a.Controller
    if ctrl and ctrl:IsValid() then
        pcall(function() ctrl:SetActorTickEnabled(false) end)
        log("ghost: paused controller " .. ctrl:GetClass():GetFName():ToString())
    end
end

-- Walk the ghost toward the latest network position every frame-ish, so its own movement component plays
-- the walk/run animation. Far away (new floor, stairs, desync) -> snap instead.
local SNAP_DISTANCE = 600
local ARRIVE_DISTANCE = 40
local function steer()
    if not target then return end
    if not valid(ghost) then
        ghost = find_ghost()
        if not valid(ghost) then return end
        log("ghost actor = " .. ghost:GetFName():ToString())
        take_over(ghost)
    end
    local cur = ghost:K2_GetActorLocation()
    local dx, dy = target.X - cur.X, target.Y - cur.Y
    local dist = math.sqrt(dx * dx + dy * dy)
    if dist > SNAP_DISTANCE then
        ghost:K2_SetActorLocation({ X = target.X, Y = target.Y, Z = target.Z }, false, {}, true)
        ghost:K2_SetActorRotation({ Pitch = 0, Yaw = target.Yaw, Roll = 0 }, true)
    elseif dist > ARRIVE_DISTANCE then
        local scale = math.min(1.0, dist / 300)          -- slow down when close: walk, not sprint, into place
        ghost:AddMovementInput({ X = dx / dist, Y = dy / dist, Z = 0 }, scale, true)
    else
        ghost:K2_SetActorRotation({ Pitch = 0, Yaw = target.Yaw, Roll = 0 }, true)
    end
end

LoopAsync(TICK_MS, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, err = pcall(tick)
        if not ok then log("tick error: " .. tostring(err)) end
    end)
    return false
end)

LoopAsync(16, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, err = pcall(steer)
        if not ok then log("steer error: " .. tostring(err)) end
    end)
    return false
end)

LoopAsync(2000, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, id = pcall(compute_floor_id)
        if ok and id ~= floor_id then floor_id = id; log(string.format("floor id %u", id)) end
    end)
    return false
end)

log("P3RCoop game side loaded (gen " .. GEN .. ")")
return "P3RCoop loaded gen " .. GEN
