
dp = dp or {}
dp.light = dp.light or {}
local L = dp.light

-- ── The room's real light ────────────────────────────────
--   dp.light.ambient()    -> 0..1: 0 = pitch dark, 1 = a normal bright
--                            room or brighter (the camera's estimate)
--   dp.light.direction()  -> Vector3, unit, world space, pointing TOWARD
--                            where the light comes from (a shadow falls
--                            along -direction)
--   dp.light.confidence() -> 0..1 for the direction (0 = no clear light)
--   dp.light.live()       -> true while a real estimate is flowing; false =
--                            fallback (ambient 1, the scene sun, confidence 0)
--   dp.light.simulated()  -> Editor: DreamPark > Simulated Room Light is on
--   dp.light.debug_set(ambient[, dir]) -> Editor only: read ambient (0..1)
--                            and dir (Vector3 or {x, y, z}, toward the
--                            light; omitted = keep); nil = the real estimate.
--                            Returns false on a device.
--   Each device reads its OWN room: NOT synced. A multiplayer puzzle
--   decides on one device (the leader) and sends the result.
--   Reading starts the camera estimate on this device; it stops ~10 s
--   after the last read.
L.ambient    = function() return dp_light_get(0) end
L.direction  = function()
    return CS.UnityEngine.Vector3(dp_light_get(1), dp_light_get(2), dp_light_get(3))
end
L.confidence = function() return dp_light_get(4) end
L.live       = function() return dp_light_get(5) > 0.5 end
L.simulated  = function() return dp_light_get(6) > 0.5 end
L.debug_set  = function(ambient, dir)
    if ambient == nil then return dp_light_debug_set(-1, 0, 0, 0) end
    local a = tonumber(ambient)
    if a == nil or a ~= a then return false end
    if a < 0 then a = 0 end
    local x, y, z = 0, 0, 0
    if dir ~= nil then
        local ok, dx, dy, dz = pcall(function()
            local px, py, pz = dir.x, dir.y, dir.z
            if px == nil and type(dir) == 'table' then px, py, pz = dir[1], dir[2], dir[3] end
            return px, py, pz
        end)
        dx, dy, dz = ok and tonumber(dx), ok and tonumber(dy), ok and tonumber(dz)
        if not (dx and dy and dz) then return false end
        x, y, z = dx, dy, dz
    end
    return dp_light_debug_set(a, x, y, z)
end
