-- P3RCoop game side (UE4SS Lua). Talks to P3RCoopLink through two small files in RUN:
--   local.txt  (we write)  "<seq> <floor> <x> <y> <z> <yaw>"
--   remote.txt (we read)   the other player's line, same format
-- The other player is shown by borrowing one of our party's partner pawns and moving it:
--   host's game:  the pawn of the guest's duo leader (party slot 2) walks where the guest is.
--   guest's game: our own pawn takes the look of the guest's leader, and the leader's pawn takes the MC's look
--                 and walks where the host is (character swap through AppCharacterComp SetIndexID/SetCostume).
-- party.txt (from the C# PartySync) = party order by character id, e.g. "1 4 2 3"; peer.txt = "<role> <0|1>".
-- Re-running this file (bridge: eval dofile(...)) replaces the previous instance cleanly.

local HOME = (os.getenv("USERPROFILE") or "C:/Users/Public"):gsub("\\", "/") .. "/P3R-Coop/"
local RUN = HOME .. "run/"
local TICK_MS = 33                   -- position exchange ~30 Hz (the link polls at the same rate)
local MC = 1                         -- character id of the protagonist

_G.P3RCOOP_GEN = (_G.P3RCOOP_GEN or 0) + 1
local GEN = _G.P3RCOOP_GEN
local seq, floor_id = 0, 0
local player, ghost = nil, nil
local last_remote, remote_age = nil, 999
local target = nil            -- latest network position of the other player {X,Y,Z,Yaw}
local target_time = 0         -- os.time() of the last update (only set while both are on the same floor)
local target_vel = { X = 0, Y = 0 }   -- partner's speed in units per tick, from the last two samples
local target_seq = nil
local steps_since = 0         -- steer() calls since the last sample (prediction horizon)
-- Ghost tracking quality for game.log: distance ghost -> partner's reported position, averaged every 5 s.
local err_sum, err_max, err_n, err_snaps, err_next = 0, 0, 0, 0, 0
local next_ghost_search = 0

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

local function read_line(name)
    local f = io.open(RUN .. name, "r")
    if not f then return nil end
    local line = f:read("*l"); f:close()
    return line
end

-- "host" / "guest" while the link reports a partner, else nil
local function coop_role()
    local role, up = (read_line("peer.txt") or ""):match("^(%a+)%s+(%d)")
    if up == "1" then return role end
    return nil
end

-- character id of the guest's duo leader (party slot 2)
local function guest_leader()
    local ids = {}
    for id in (read_line("party.txt") or ""):gmatch("%d+") do ids[#ids + 1] = tonumber(id) end
    return ids[3]
end

local function char_comp(a)
    local ok, c = pcall(function() return a.AppCharacterComp end)
    if ok and c and c:IsValid() then return c end
    return nil
end

local function partner_with(id)
    for _, a in ipairs(FindAllOf("BP_FldPartner_C") or {}) do
        local c = valid(a) and char_comp(a)
        if c and c.IndexID == id then return a end
    end
    return nil
end

-- The partner pawn that shows the other player (see the header).
local function find_ghost()
    local role = coop_role()
    if role == "guest" then return partner_with(MC) end
    local leader = guest_leader()
    if leader then return partner_with(leader) end
    return nil
end

-- Guest only: swap looks between our pawn (MC) and the leader's partner pawn. Checked every 2 s (new floors
-- spawn fresh pawns); undone if the partner goes away.
local swapped = false
local dressed_at = -1                     -- battle count (BattleRecon) when the looks were last applied
local function set_look(c, id, costume)
    c:SetIndexID(id); c:SetCostumeID(costume); c:SetCostume()
end
local function update_looks()
    if not valid(player) then return end
    local pc = char_comp(player)
    if not pc then return end
    local leader = guest_leader()
    local want = coop_role() == "guest" and leader ~= nil
    -- After a battle the game re-dresses the field pawns from the party data without changing IndexID (2026-10-07:
    -- "after one battle the guest looked like the host"): apply the looks again after every battle.
    local battles = _G.P3RCOOP_BATTLE_ENDS or 0
    if want and swapped and pc.IndexID ~= MC and dressed_at ~= battles then
        local other = partner_with(MC) or partner_with(leader)
        set_look(pc, leader, 0)
        if other and char_comp(other) then set_look(char_comp(other), MC, -1) end
        dressed_at = battles
        ghost = nil
        log("looks applied again after a battle")
        return
    end
    if want and pc.IndexID == MC then
        local other = partner_with(leader)
        local oc = other and char_comp(other)
        if not oc then return end
        local my_costume, other_costume = pc.CostumeID, oc.CostumeID
        set_look(pc, leader, other_costume)
        set_look(oc, MC, my_costume)
        swapped = true
        dressed_at = _G.P3RCOOP_BATTLE_ENDS or 0
        ghost = nil                                   -- pick the (now MC-looking) ghost again
        log(string.format("looks swapped: we are character %d, the host is shown on %s", leader, other:GetFName():ToString()))
    elseif not want and swapped and pc.IndexID ~= MC then
        local other = partner_with(MC)
        local oc = other and char_comp(other)
        local leader_id = pc.IndexID
        set_look(pc, MC, oc and oc.CostumeID or -1)
        if oc then set_look(oc, leader_id, 0) end
        swapped = false
        log("looks restored")
    end
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

-- Joint battles: the partner started a battle and our field is idle -> the C# mod writes join_go.txt and we let
-- the enemy manager request an encounter (its C# hook picks the partner's Shadow and sets the encounter).
local function check_join()
    local f = io.open(RUN .. "join_go.txt", "r")
    if not f then return end
    local id = f:read("*l"); f:close(); os.remove(RUN .. "join_go.txt")
    local m = FindFirstOf("FldEnemyManager")
    if valid(m) then
        m:ForceEncount()
        log("join " .. tostring(id) .. ": ForceEncount called")
    else
        log("join " .. tostring(id) .. ": no FldEnemyManager")
    end
end

-- F8 at a boss that waits for the partner: fight it now (the partner joins if it can).
if not _G.P3RCOOP_F8 then
    _G.P3RCOOP_F8 = true
    RegisterKeyBind(Key.F8, function()
        _G.P3RCOOP_BOSS_OVERRIDE = os.time() + 120
        local f = io.open(RUN .. "boss_solo.txt", "w")
        if f then f:write("1"); f:close() end
        log("F8: boss without waiting (2 min)")
    end)
end

-- Boss wait (Block Tales: everyone at the boss): while co-op is on, a Gatekeeper's collision stays off - it can't
-- be touched - until the partner stands near it too (their network position, same floor). F8 = fight now.
local BOSS_NEAR = 1500
-- Model actor classes standing on a boss (found once per boss: every actor class named BP_Em...).
local model_cache = {}
local function boss_model_classes(boss)
    local key = boss:GetFName():ToString()
    if model_cache[key] then return model_cache[key] end
    local l, found, list = boss:K2_GetActorLocation(), {}, {}
    for _, m in ipairs(FindAllOf("Actor") or {}) do
        pcall(function()
            local cls = m:GetClass():GetFName():ToString()
            if cls:match("^BP_Em%d") and not found[cls] then
                local ml = m:K2_GetActorLocation()
                local dx, dy = ml.X - l.X, ml.Y - l.Y
                if dx * dx + dy * dy < 600 * 600 then found[cls] = true; list[#list + 1] = cls end
            end
        end)
    end
    model_cache[key] = list
    return list
end
local enemy_mgr = nil
local gate_state = {}                                 -- boss actor name -> allowed
local function boss_gate()
    if not valid(player) then return end
    if not valid(enemy_mgr) then enemy_mgr = FindFirstOf("FldEnemyManager") end
    if not valid(enemy_mgr) then return end
    local role = coop_role()
    local override = os.time() < (_G.P3RCOOP_BOSS_OVERRIDE or 0)
    for i = 1, #enemy_mgr.Enemies do
        local h = enemy_mgr.Enemies[i]
        local c, a = h.EnemyComp, h.Actor
        if c and c:IsValid() and a and a:IsValid() and c.EnemyType == 4 then
            local allow = (role == nil) or override
            if not allow and target and os.time() - target_time <= 2 then
                local l = a:K2_GetActorLocation()
                local dx, dy = target.X - l.X, target.Y - l.Y
                allow = dx * dx + dy * dy < BOSS_NEAR * BOSS_NEAR
            end
            local key = a:GetFName():ToString()
            if gate_state[key] ~= allow then
                gate_state[key] = allow
                c.bCollisionEnable = allow
                a:SetActorEnableCollision(allow)
                -- The boss's visible parts are separate model actors (BP_Em0093_003_C, BP_Em0062_003_C on 246) on
                -- the same spot: a strike may hit those, so they follow the gate too.
                local l = a:K2_GetActorLocation()
                for _, cls in ipairs(boss_model_classes(a)) do
                    for _, m in ipairs(FindAllOf(cls) or {}) do
                        if valid(m) then
                            local ml = m:K2_GetActorLocation()
                            local dx, dy = ml.X - l.X, ml.Y - l.Y
                            if dx * dx + dy * dy < 600 * 600 then m:SetActorEnableCollision(allow) end
                        end
                    end
                end
                -- The collision gate alone did not stop the boss fight (2026-10-07): also switch off the event/trigger
                -- boxes around the boss (FldHitActor*, BP_DEVT_*) while waiting.
                for _, tcls in ipairs({ "FldHitActorBOX", "FldHitActor", "BP_DEVT_RefActor_C" }) do
                    for _, t in ipairs(FindAllOf(tcls) or {}) do
                        pcall(function()
                            local tl = t:K2_GetActorLocation()
                            local dx, dy = tl.X - l.X, tl.Y - l.Y
                            if dx * dx + dy * dy < 3000 * 3000 then t:SetActorEnableCollision(allow) end
                        end)
                    end
                end
                log("boss " .. key .. (allow and ": can be fought now" or ": waiting for the partner to come here (F8 = fight now)"))
            end
        end
    end
end

local next_player_search = 0
local function tick()
    check_join()
    if not valid(player) then
        -- FindAllOf scans every UObject: only retry every 2 s (the field pawn is hidden during battles)
        if os.clock() < next_player_search then return end
        next_player_search = os.clock() + 2
        player = find_player()
        if not valid(player) then return end
    end

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

    local nx, ny, ns = tonumber(x), tonumber(y), tonumber(rseq)
    if target and target_seq and ns > target_seq and ns - target_seq < 30 then
        target_vel = { X = (nx - target.X) / (ns - target_seq), Y = (ny - target.Y) / (ns - target_seq) }
    else
        target_vel = { X = 0, Y = 0 }
    end
    target = { X = nx, Y = ny, Z = tonumber(z), Yaw = tonumber(yaw) }
    target_seq = ns
    target_time = os.time()
    steps_since = 0
end

-- The borrowed partner normally follows our leader; switch that logic off so it only obeys the network.
local FOLLOW_COMPONENTS = { "/Script/xrd777.FldFollowerComp", "/Script/xrd777.FldPartnerComp" }
local function take_over(a)
    for _, path in ipairs(FOLLOW_COMPONENTS) do
        local cls = StaticFindObject(path)
        if cls and cls:IsValid() then
            -- K2_GetComponentsByClass returns a UE4SS TArray (not a Lua table): walk it with ForEach + get()
            local comps = a:K2_GetComponentsByClass(cls)
            local list = {}
            if type(comps) == "table" then list = comps
            elseif comps then comps:ForEach(function(_, elem) list[#list + 1] = elem:get() end) end
            for _, c in ipairs(list) do
                if c and c:IsValid() then
                    pcall(function() c:SetComponentTickEnabled(false) end)
                    log("ghost: disabled " .. c:GetClass():GetFName():ToString())
                end
            end
        end
    end
    -- BP_FldPartner_C.FldPartnerComp drives the follow-the-leader movement (the class lookup above found
    -- nothing in the first real session, so the ghost kept walking back to our leader while we pushed it).
    local ok = pcall(function()
        local pc = a.FldPartnerComp
        if pc and pc:IsValid() then
            pc:SetComponentTickEnabled(false)
            log("ghost: partner follow logic off (FldPartnerComp)")
        end
    end)
    if not ok then log("ghost: no FldPartnerComp") end
    -- The ghost is moved directly (see steer); its own movement component would fight that, but its Velocity is
    -- what the animation reads, so it stays as a value holder with its tick off.
    pcall(function() a.CharacterMovement:SetComponentTickEnabled(false); log("ghost: movement component off") end)
    local ctrl = a.Controller
    if ctrl and ctrl:IsValid() then
        pcall(function() ctrl:StopMovement() end)
        pcall(function() ctrl:SetActorTickEnabled(false) end)
        log("ghost: paused controller " .. ctrl:GetClass():GetFName():ToString())
    end
end

-- Walk the ghost toward the latest network position every frame-ish, so its own movement component plays
-- the walk/run animation. Far away (new floor, stairs, desync) -> snap instead.
local SNAP_DISTANCE = 1500                -- far (stairs, new floor, desync): jump
local PULL_DISTANCE = 200                 -- the partner pawn walks slower than a running player: pull it along
local ARRIVE_DISTANCE = 40
local function steer()
    if not target then return end
    if not valid(ghost) then
        if os.clock() < (next_ghost_search or 0) then return end
        next_ghost_search = os.clock() + 2
        ghost = find_ghost()
        if not valid(ghost) then return end
        log("ghost actor = " .. ghost:GetFName():ToString())
        take_over(ghost)
    end
    local cur = ghost:K2_GetActorLocation()
    -- Aim where the partner is now, not where the last packet said: extrapolate a few ticks (max ~0.2 s).
    steps_since = steps_since + 1
    local ahead = math.min(steps_since, 6)
    local px, py = target.X + target_vel.X * ahead, target.Y + target_vel.Y * ahead
    local dx, dy = px - cur.X, py - cur.Y
    local dist = math.sqrt(dx * dx + dy * dy)
    local ex, ey = target.X - cur.X, target.Y - cur.Y
    local err = math.sqrt(ex * ex + ey * ey)
    err_sum, err_n, err_max = err_sum + err, err_n + 1, math.max(err_max, err)
    if os.time() >= err_next and err_n > 0 then
        log(string.format("ghost tracking: avg %.0f max %.0f units, %d snaps, %d samples/5s", err_sum / err_n, err_max, err_snaps, err_n))
        err_sum, err_max, err_n, err_snaps, err_next = 0, 0, 0, 0, os.time() + 5
    end
    if dist > SNAP_DISTANCE then
        err_snaps = err_snaps + 1
        ghost:K2_SetActorLocation({ X = target.X, Y = target.Y, Z = target.Z }, false, {}, true)
        ghost:K2_SetActorRotation({ Pitch = 0, Yaw = target.Yaw, Roll = 0 }, true)
        return
    end
    -- Glide toward the (predicted) position: a third of the gap per step (~30 Hz) - smooth, catches up in ~0.2 s.
    -- (AddMovementInput did not move the taken-over pawn: it slid along ~200 units behind, 2026-10-07 session.)
    local k = 0.35
    local nx, ny = cur.X + dx * k, cur.Y + dy * k
    local nz = cur.Z + (target.Z - cur.Z) * k
    ghost:K2_SetActorLocation({ X = nx, Y = ny, Z = nz }, false, {}, true)
    -- Animation speed comes from the movement component's Velocity (units/s, one step = 1/30 s).
    local vx, vy = (nx - cur.X) * 30, (ny - cur.Y) * 30
    pcall(function() ghost.CharacterMovement.Velocity = { X = vx, Y = vy, Z = 0 } end)
    -- Face where it walks; standing still, turn to the partner's own facing.
    local yaw
    if vx * vx + vy * vy > 60 * 60 then yaw = math.deg(math.atan(vy, vx)) else yaw = target.Yaw end
    local r = ghost:K2_GetActorRotation()
    local dyaw = (yaw - r.Yaw + 540) % 360 - 180
    ghost:K2_SetActorRotation({ Pitch = 0, Yaw = r.Yaw + dyaw * 0.3, Roll = 0 }, true)
end

LoopAsync(TICK_MS, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, err = pcall(tick)
        if not ok then log("tick error: " .. tostring(err)) end
    end)
    return false
end)

LoopAsync(33, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, err = pcall(steer)
        if not ok then log("steer error: " .. tostring(err)) end
    end)
    return false
end)

LoopAsync(500, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, err = pcall(boss_gate)
        if not ok then log("boss gate error: " .. tostring(err)) end
    end)
    return false
end)

LoopAsync(2000, function()
    if _G.P3RCOOP_GEN ~= GEN then return true end
    ExecuteInGameThread(function()
        local ok, id = pcall(compute_floor_id)
        if ok and id ~= floor_id then floor_id = id; log(string.format("floor id %u", id)) end
        local ok2, err = pcall(update_looks)
        if not ok2 then log("looks error: " .. tostring(err)) end
    end)
    return false
end)

log("P3RCoop game side loaded (gen " .. GEN .. ")")
return "P3RCoop loaded gen " .. GEN
