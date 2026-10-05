-- UE4SS mod "P3RCoop" (partner kit): loads the co-op game scripts that sit next to this file.
local dir = (debug.getinfo(1, "S").source or ""):match("^@?(.*[\\/])")
if not dir or dir == "" then
    dir = (os.getenv("USERPROFILE") or "C:/Users/Public"):gsub("\\", "/")
        .. "/P3R-Coop/Reloaded-II/Mods/p3rpc.UE4SS.Reloaded/Mods/P3RCoop/Scripts/"
end
for _, f in ipairs({ "P3RCoop.lua", "BattleRecon.lua" }) do
    local ok, err = pcall(dofile, dir .. f)
    if not ok then print("[P3RCoop] could not load " .. f .. ": " .. tostring(err) .. "\n") end
end
