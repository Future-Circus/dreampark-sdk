-- ============================================================================
--  dp.* Routes facade (Phase 1). Runs after dp.routes (dp_routes.lua) is loaded.
--  Talks to C# only through internal dp_* delegates (DreamParkLuaRoutes.cs):
--    dp_route_stop(go)  -> stop descriptor of go's attraction (world), or nil
--    dp_route_stops()   -> every placed attraction's descriptor
--    dp_ports(go)       -> the applied frame's portals, resolved against the rect openings
--    dp_neighbors(go)   -> rects/attractions through go's openings
--  Positions are WORLD on this headset; route_plan puts park-local on the wire.
--  NO double quotes in this file (it is embedded in a C# verbatim string).
-- ============================================================================
dp = dp or {}
local R = dp.routes

local UE = (rawget(_G, 'CS') ~= nil) and CS.UnityEngine or nil
-- Unity Vector3 in the app; a plain {x, y, z} offline (Lua sims, tests).
local function V3(x, y, z) if UE ~= nil then return UE.Vector3(x, y, z) end return { x = x, y = y, z = z } end

local function is_go(v)
    if v == nil or type(v) ~= 'userdata' then return false end
    local ok, g = pcall(function() return v.gameObject end)
    return ok and g ~= nil
end

local function xyz(v)
    if v == nil then return nil end
    if type(v) == 'table' then
        if v.pos ~= nil then return xyz(v.pos) end
        if v.x ~= nil and v.z ~= nil then return v.x, v.y or 0, v.z end
        if v.cx ~= nil then return v.cx, v.y or 0, v.cz end
        return nil
    end
    if is_go(v) then
        local p = v.transform.position
        return p.x, p.y, p.z
    end
    local ok, x, y, z = pcall(function() return v.x, v.y, v.z end)
    if ok and x ~= nil then return x, y, z end
    return nil
end

local function copy_cfg(opts)
    local cfg = {}
    for k, v in pairs(R.CFG) do cfg[k] = v end
    if opts ~= nil then
        if opts.cfg ~= nil then for k, v in pairs(opts.cfg) do cfg[k] = v end end
        if opts.spur_penalty ~= nil then cfg.deadend_pen = opts.spur_penalty end
        if opts.max_exact ~= nil then cfg.max_exact = opts.max_exact end
    end
    return cfg
end

local function pts_v3(pts, y)
    local out = {}
    for i, p in ipairs(pts or {}) do out[i] = V3(p.x, y or 0, p.z) end
    return out
end

--- Wrap a {x,z} polyline as a route object: r.at(s) -> pos, fwd; r.project(pos) -> s, dist.
local function as_route(r, y)
    local raw = r.pts
    r.raw = raw
    r.pts = pts_v3(raw, y)
    r.y = y
    local st = nil
    function r.at(s)
        if raw == nil or #raw < 2 then return r.pts[1], V3(0, 0, 1) end
        st = st or R.resample(raw, 0.05)
        local q = R.at(st, s)
        return V3(q.x, y or 0, q.z), V3(q.fx, 0, q.fz)
    end
    function r.project(pos)
        local x, _, z = xyz(pos)
        if x == nil or raw == nil or #raw < 2 then return 0, nil end
        return R.project(raw, x, z)
    end
    function r.arrows(gap) return R.link_arrows(raw, gap or 0.8) end
    local doors = {}
    for i, d in ipairs(r.doors or {}) do
        doors[i] = { pos = V3(d.x, y or 0, d.z), width = d.width, type = d.type, from = d.from, to = d.to }
    end
    r.doors = doors
    return r
end

-- ── the park graph: every rect with its openings (core's space map; empty without one) ──
--- dp.park_rects() -> { {id, cx, cz, yaw, W, L, rect = {id, opens, links, spans, open_area}} } (world), for
--- drawing or own routing. v3 (RECT_EDGES_SPEC): opens = passable stretches (door / opening / unknown, `to`),
--- links = stretches a neighbouring rect shares (any kind, `to`), spans = the raw spans, open_area = all unknown.
dp.park_rects = function() return (dp_route_graph and dp_route_graph()) or {} end

-- ── what is around me ────────────────────────────────────────────────────
dp.rect = function(go)
    local s = dp_route_stop(go)
    if s == nil then return nil end
    local r = s.rect or { id = '', opens = {} }
    r.has_data = s.rect ~= nil
    r.source = s.source
    r.attraction = s.root
    return r
end

dp.ports = function(go) return dp_ports(go) end

dp.neighbors = function(go) return dp_neighbors(go) end

-- ── routes ───────────────────────────────────────────────────────────────
--- dp.route(a, b[, opts]): a/b = Vector3 | GameObject | port | stop. Door graph over every
--- placed attraction with rect data; else opts.world's grid; else a straight line.
dp.route = function(a, b, opts)
    opts = opts or {}
    local ax, ay, az = xyz(a)
    local bx, _, bz = xyz(b)
    if ax == nil or bx == nil then return { ok = false, why = 'bad endpoint', pts = {}, len = 0, hops = 0, doors = {} } end
    local P = opts.stops or dp_route_stops() or {}
    local graph = opts.graph or (dp_route_graph and dp_route_graph()) or nil
    local r = R.route(P, ax, az, bx, bz, opts.world, copy_cfg(opts), graph)
    return as_route(r, ay)
end

local function descriptor(v)
    if type(v) == 'table' and v.cx ~= nil then return v end
    if is_go(v) then return dp_route_stop(v.gameObject) end
    return nil
end

--- dp.tour(stops[, opts]) -> { ok, order, roots, ends, links, len, cost, dec, why }
--- stops: GameObjects (any object in an attraction) or stop descriptors.
--- opts: loop (true), keep_order, start (GameObject | id), spur_penalty (6), max_exact (5),
---       world (wall sensing for stops without rect data), links (true), cfg, log,
---       prepare / elect / ends (planner hooks; see dp.routes.search).
dp.tour = function(stops, opts)
    opts = opts or {}
    local P, seen = {}, {}
    for _, v in ipairs(stops or {}) do
        local d = descriptor(v)
        if d ~= nil and not seen[d.id] then seen[d.id] = true P[#P + 1] = d end
    end
    if #P == 0 then return { ok = false, why = 'no stops', order = {}, roots = {}, ends = {}, links = {}, len = 0 } end
    local cfg = copy_cfg(opts)
    local start = opts.start
    if start ~= nil and type(start) ~= 'string' then local d = descriptor(start) start = d and d.id end
    local graph = opts.graph
    if graph == nil and opts.park_graph ~= false and dp_route_graph ~= nil then graph = dp_route_graph() end
    local sopts = { cfg = cfg, log = opts.log, prepare = opts.prepare, elect = opts.elect, ends = opts.ends,
                    start = start, keep_order = opts.keep_order, loop = opts.loop, graph = graph }
    local dec, ctx = R.search(P, opts.world, sopts)
    if dec == nil then return { ok = false, why = ctx, order = {}, roots = {}, ends = {}, links = {}, len = 0 } end
    local t = { ok = true, dec = dec, ctx = ctx, cost = dec.cost, loop = opts.loop ~= false, stops = P,
                order = {}, roots = {}, ends = {}, links = {}, len = 0 }
    local byid = {}
    for _, p in ipairs(P) do byid[p.id] = p end
    for i, id in ipairs(dec.order) do
        local p = byid[id]
        t.order[i] = p
        t.roots[i] = p.root
        local d = dec.d[id]
        local y = p.y or 0
        if d ~= nil and d.E ~= nil then
            t.ends[i] = { role = d.role, spur = d.deadend == true,
                entry = { pos = V3(d.E.x, y, d.E.z), fwd = V3(d.E.dx or 0, 0, d.E.dz or 1), width = d.E.width, to = d.E.to, type = d.E.type },
                exit = { pos = V3(d.X.x, y, d.X.z), fwd = V3(d.X.dx or 0, 0, d.X.dz or 1), width = d.X.width, to = d.X.to, type = d.X.type } }
        else
            t.ends[i] = { role = d and d.role or 'fixed', spur = false }
        end
    end
    if opts.links ~= false and not dec.solo and not dec.closed_track then
        local links = R.links(P, dec, opts.world, cfg, ctx and ctx.G, graph)
        for k, l in ipairs(links) do
            local y = (byid[l.from] and byid[l.from].y) or 0
            local r = as_route({ ok = l.pts ~= nil, pts = l.pts or {}, len = l.len or 0, hops = l.doors and #l.doors or 0,
                                 doors = l.doors, from = l.from, to = l.to, closing = l.closing, straight = l.straight }, y)
            r.from_root, r.to_root = byid[l.from] and byid[l.from].root, byid[l.to] and byid[l.to].root
            t.links[k] = r
            t.len = t.len + (l.len or 0)
        end
    end
    return t
end

--- dp.clusters(stops[, gap]) -> lists of descriptors whose footprints come within `gap` m.
dp.route_clusters = function(stops, gap)
    local P = {}
    for _, v in ipairs(stops or {}) do local d = descriptor(v) if d ~= nil then P[#P + 1] = d end end
    local cfg = copy_cfg(nil)
    if gap ~= nil then cfg.cluster_gap = gap end
    return R.clusters(P, cfg)
end

-- ── agreeing on a plan (optional; dp.tour is deterministic from park data) ──
--- dp.route_plan(name[, go]) -> p: p.publish(t) (leader only), p.get(stops) -> tour | nil, why,
--- p.on_change(fn[, owner]), p.blob(). Built on dp.shared_state(name); park-local on the wire.
dp.route_plan = function(name, go, caller)
    local s = dp.shared_state and dp.shared_state(name or 'dp_route_plan', go, caller)
    if s == nil then return nil end
    local function tp(x, z)
        local v = dp.to_park and dp.to_park(V3(x, 0, z))
        if v == nil then return x, z end
        return v.x, v.z
    end
    local function fp(x, z)
        local v = dp.from_park and dp.from_park(V3(x, 0, z))
        if v == nil then return x, z end
        return v.x, v.z
    end
    local p = {}
    function p.publish(t)
        if t == nil or t.dec == nil or t.stops == nil then return false, 'no tour' end
        if dp.is_leader and not dp.is_leader(caller or go) then return false, 'not the leader' end
        local wire = t.dec.wire or R.encode(t.dec, t.stops, tp)
        return s.set('plan', R.blob(wire))
    end
    function p.blob() return s.get('plan') end
    function p.get(stops, opts)
        local t = R.unblob(s.get('plan'))
        if t == nil then return nil, 'no plan' end
        local P = {}
        for _, v in ipairs(stops or {}) do local d = descriptor(v) if d ~= nil then P[#P + 1] = d end end
        local dec, why = R.decode(t, P, fp, opts and opts.tol)
        if dec == nil then return nil, why end
        local res = { ok = true, dec = dec, stops = P, order = {}, roots = {}, ends = {} }
        local byid = {}
        for _, q in ipairs(P) do byid[q.id] = q end
        for i, id in ipairs(dec.order) do
            local q = byid[id]
            res.order[i], res.roots[i] = q, q.root
            local d = dec.d[id]
            if d ~= nil and d.E ~= nil then
                res.ends[i] = { role = d.role, spur = d.deadend, entry = { pos = V3(d.E.x, q.y or 0, d.E.z) }, exit = { pos = V3(d.X.x, q.y or 0, d.X.z) } }
            else res.ends[i] = { role = d and d.role or 'fixed', spur = false } end
        end
        return res
    end
    function p.on_change(fn, owner) return s.on_change(function() fn(p) end, owner) end
    p.synced = function() return s.synced() end
    return p
end
