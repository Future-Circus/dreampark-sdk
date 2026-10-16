-- ============================================================================
--  dp.routes - DreamPark Routes, Phase 1 (pure Lua; SDK library)
--
--  Walk routes between placed attractions through REAL doorways, and tours
--  (an order + each stop's entry/exit door) over a set of stops. Ported from
--  CrashCourse's venue planner (cc_venue.lua.txt, Oct 2026): its door-graph
--  router, walking-grid Dijkstra, tour search (exact permutations / greedy +
--  2-opt, Viterbi over each stop's door options, closing leg, spur penalty),
--  clustering and hard caps. Same inputs give the same answer on every
--  headset: no RNG, deterministic ordering, no Unity calls.
--
--  Coordinates: plain {x, z} tables in ONE frame (world on a headset; the
--  dp.* wrappers convert). A STOP is
--    { id, name, cx, cz, yaw, W, L,              -- footprint centre / yaw / size (m)
--      flex = true,                              -- choose its entry/exit from openings
--      rect = { id, opens = { {ax,az,bx,bz,nx,nz,type,kind,to}, ... }, open_area } | nil,
--      inp/out/start = {x,z,fx,fz} | nil,        -- fixed in/out points (flex = false)
--      w = clearance width (m, default 1.0) }
--  `rect.opens` are the seated rect's PASSABLE stretches in the same frame
--  (v3 RECT_EDGES_SPEC: door / opening / unknown; only walls block), nx/nz the
--  outward normal of their side, `to` the rect beyond (dp.rect builds these
--  from core's data). `open_area` = all four sides unknown: the rect stands in
--  a big open space and connects from any side, to any other open-area rect.
--
--  Source of truth: Tools/routes-offline/dp_routes.lua (core + sdk). It is
--  embedded into Assets/DreamPark/Scripts/Core/DreamParkLuaRoutes.cs by
--  Tools/routes-offline/embed.py; NO double quotes in this file.
-- ============================================================================
local R = {}
R.version = 1

R.CFG = {
    min_open    = 0.70,   -- narrowest opening a stop can enter/leave through (m)
    wide_split  = 1.6,    -- openings wider than this get up to 3 candidate points
    cell        = 0.15,   -- walking grid (no rect data)
    body_r      = 0.20,
    margin      = 1.5,
    inside_cost = 3.0,    -- walking across ANOTHER stop's footprint costs this x
    deadend_pen = 6.0,    -- spur: in and out through the same opening (m of walk)
    max_exact   = 5,      -- exact order search up to this many non-start stops (2-opt above)
    cluster_gap = 6.0,    -- stops whose footprints come within this (m) form one venue
    max_pieces  = 10,     -- stops per tour (incl. the start)
    max_span    = 30.0,   -- grid bbox side cap (m)
    max_cells   = 20000,
    max_nodes   = 64,     -- candidate door points per tour
    door_slack  = 0.6,    -- a point within this of a doorway is in both rooms
}

-- ── geometry (Unity yaw: clockwise from above) ────────────────────────────
local function rot(yaw, x, z)
    local r = math.rad(yaw)
    local c, s = math.cos(r), math.sin(r)
    return x * c + z * s, -x * s + z * c
end
R.rot = rot
local function len2(x, z) return math.sqrt(x * x + z * z) end
R.len2 = len2
local function lerp(a, b, t) return a + (b - a) * t end

function R.to_world(p, x, z)
    local ox, oz = rot(p.yaw, x, z)
    return p.cx + ox, p.cz + oz
end
function R.to_local(p, wx, wz)
    return rot(-p.yaw, wx - p.cx, wz - p.cz)
end

--- The 4 footprint edges (local -Z, +X, +Z, -X), world end points, outward normal.
function R.edges(p)
    local a, b = p.W * 0.5, p.L * 0.5
    local loc = {
        { -a, -b, a, -b, 0, -1, '-Z' },
        { a, -b, a, b, 1, 0, '+X' },
        { a, b, -a, b, 0, 1, '+Z' },
        { -a, b, -a, -b, -1, 0, '-X' },
    }
    local out = {}
    for i = 1, 4 do
        local e = loc[i]
        local ax, az = R.to_world(p, e[1], e[2])
        local bx, bz = R.to_world(p, e[3], e[4])
        local nx, nz = rot(p.yaw, e[5], e[6])
        out[i] = { i = i, name = e[7], ax = ax, az = az, bx = bx, bz = bz, nx = nx, nz = nz,
                   len = len2(bx - ax, bz - az) }
    end
    return out
end

local function inside(p, x, z, pad)
    local lx, lz = R.to_local(p, x, z)
    return math.abs(lx) <= p.W * 0.5 - (pad or 0) and math.abs(lz) <= p.L * 0.5 - (pad or 0)
end
R.inside = inside

-- ── ports from rect openings ──────────────────────────────────────────────
--- A stop's openings projected onto its own footprint edge. Only openings that
--- lead somewhere (`to`) count when any do; a rect with none keeps its open spans.
--- An open-area rect (all sides unknown) keeps every side.
function R.rect_opens(p, cfg)
    cfg = cfg or R.CFG
    local out = {}
    local hw, hl = p.W * 0.5, p.L * 0.5
    local any_to = false
    for _, o in ipairs(p.rect.opens or {}) do if o.to ~= nil then any_to = true end end
    for _, o in ipairs(p.rect.opens or {}) do
      if o.to ~= nil or not any_to or p.rect.open_area then
        local lnx, lnz = rot(-p.yaw, o.nx, o.nz)
        local function proj(x, z)
            local lx, lz = R.to_local(p, x, z)
            if math.abs(lnx) > math.abs(lnz) then lx = (lnx > 0) and hw or -hw lz = math.max(-hl, math.min(hl, lz))
            else lz = (lnz > 0) and hl or -hl lx = math.max(-hw, math.min(hw, lx)) end
            return R.to_world(p, lx, lz)
        end
        local ax, az = proj(o.ax, o.az)
        local bx, bz = proj(o.bx, o.bz)
        local w = len2(bx - ax, bz - az)
        if w >= cfg.min_open * 0.85 then
            local edge = (math.abs(lnx) > math.abs(lnz)) and ((lnx > 0) and 2 or 4) or ((lnz > 0) and 3 or 1)
            out[#out + 1] = { piece = p.id, edge = edge, width = w, ax = ax, az = az, bx = bx, bz = bz,
                              nx = o.nx, nz = o.nz, to = o.to, type = o.type, rect = p.rect.id }
        end
      end
    end
    return out
end

--- Openings of a stop: rect data when it has some, else `world.seg_blocked` probes
--- along its footprint (titles that sense walls themselves), else every edge open.
function R.openings(p, world, cfg)
    cfg = cfg or R.CFG
    if p.rect ~= nil then return R.rect_opens(p, cfg) end
    local out = {}
    local step = cfg.probe_step or 0.10
    for _, e in ipairs(R.edges(p)) do
        local n = math.max(1, math.floor(e.len / step + 0.5))
        local open = {}
        for k = 0, n do
            local t = k / n
            local x, z = lerp(e.ax, e.bx, t), lerp(e.az, e.bz, t)
            if world == nil or world.seg_blocked == nil then open[k] = true
            else
                local x0, z0 = x - e.nx * (cfg.probe_in or 0.12), z - e.nz * (cfg.probe_in or 0.12)
                local x1, z1 = x + e.nx * (cfg.probe_out or 0.60), z + e.nz * (cfg.probe_out or 0.60)
                open[k] = not world.seg_blocked(x0, z0, x1, z1)
            end
        end
        local k = 0
        while k <= n do
            if open[k] then
                local k0 = k
                while k + 1 <= n and open[k + 1] do k = k + 1 end
                local t0, t1 = k0 / n, k / n
                local w = (t1 - t0) * e.len
                if w >= cfg.min_open - 1e-6 then
                    out[#out + 1] = { piece = p.id, edge = e.i, edge_name = e.name, t0 = t0, t1 = t1, width = w,
                                      ax = lerp(e.ax, e.bx, t0), az = lerp(e.az, e.bz, t0),
                                      bx = lerp(e.ax, e.bx, t1), bz = lerp(e.az, e.bz, t1),
                                      nx = e.nx, nz = e.nz }
                end
            end
            k = k + 1
        end
    end
    return out
end

--- Candidate entry/exit points: each opening's centre; up to 3 along wide ones.
function R.candidates(p, opens, cfg)
    cfg = cfg or R.CFG
    local out = {}
    local hw = (p.w or 1.0) * 0.5
    for oi, o in ipairs(opens) do
        local ts = {}
        if o.width > cfg.wide_split then
            local m = math.min(hw, o.width * 0.5) / o.width
            local cnt = math.max(2, math.min(3, math.floor(o.width) + 1))
            for k = 0, cnt - 1 do ts[#ts + 1] = lerp(m, 1 - m, (cnt == 1) and 0.5 or k / (cnt - 1)) end
        else
            ts[1] = 0.5
        end
        for _, t in ipairs(ts) do
            out[#out + 1] = { piece = p.id, open = oi, edge = o.edge, x = lerp(o.ax, o.bx, t), z = lerp(o.az, o.bz, t),
                              nx = o.nx, nz = o.nz, width = o.width, to = o.to, rect = o.rect, type = o.type }
        end
    end
    return out
end

-- ── walking grid + Dijkstra (8-connected), for titles without rect data ──
function R.grid(world, x0, z0, x1, z1, cfg, cost_at)
    cfg = cfg or R.CFG
    local c = cfg.cell
    local G = { x0 = x0, z0 = z0, c = c, nx = math.max(1, math.ceil((x1 - x0) / c)), nz = math.max(1, math.ceil((z1 - z0) / c)),
                free = {}, cost = {} }
    local blocked = 0
    local tick = cfg.tick
    for iz = 0, G.nz - 1 do
        if tick then tick() end
        for ix = 0, G.nx - 1 do
            local x, z = x0 + (ix + 0.5) * c, z0 + (iz + 0.5) * c
            local i = iz * G.nx + ix
            local f = not (world and world.cell_blocked and world.cell_blocked(x, z))
            G.free[i] = f
            if not f then blocked = blocked + 1 end
            G.cost[i] = cost_at and cost_at(x, z) or 1
        end
    end
    G.blocked = blocked
    return G
end

local function cell_of(G, x, z)
    local ix, iz = math.floor((x - G.x0) / G.c), math.floor((z - G.z0) / G.c)
    if ix < 0 or iz < 0 or ix >= G.nx or iz >= G.nz then return nil end
    return iz * G.nx + ix, ix, iz
end
local function center_of(G, i)
    local ix, iz = i % G.nx, math.floor(i / G.nx)
    return G.x0 + (ix + 0.5) * G.c, G.z0 + (iz + 0.5) * G.c
end
R.center_of = center_of

function R.snap(G, x, z, r)
    local i0 = cell_of(G, x, z)
    if i0 ~= nil and G.free[i0] then return i0 end
    local rr = math.ceil((r or 0.6) / G.c)
    local best, bd = nil, nil
    local cx = math.floor((x - G.x0) / G.c)
    local cz = math.floor((z - G.z0) / G.c)
    for dz = -rr, rr do
        for dx = -rr, rr do
            local ix, iz = cx + dx, cz + dz
            if ix >= 0 and iz >= 0 and ix < G.nx and iz < G.nz then
                local i = iz * G.nx + ix
                if G.free[i] then
                    local d = dx * dx + dz * dz
                    if bd == nil or d < bd or (d == bd and i < best) then best, bd = i, d end
                end
            end
        end
    end
    return best
end

local function hpush(h, d, i)
    local n = #h + 1
    h[n] = { d, i }
    while n > 1 do
        local p = math.floor(n / 2)
        if h[p][1] <= h[n][1] then break end
        h[p], h[n] = h[n], h[p]
        n = p
    end
end
local function hpop(h)
    local top = h[1]
    local last = h[#h]
    h[#h] = nil
    if #h > 0 then
        h[1] = last
        local n = 1
        while true do
            local l, r = n * 2, n * 2 + 1
            local m = n
            if l <= #h and h[l][1] < h[m][1] then m = l end
            if r <= #h and h[r][1] < h[m][1] then m = r end
            if m == n then break end
            h[m], h[n] = h[n], h[m]
            n = m
        end
    end
    return top
end

local NB = { { 1, 0, 1 }, { -1, 0, 1 }, { 0, 1, 1 }, { 0, -1, 1 },
             { 1, 1, 1.41421356 }, { 1, -1, 1.41421356 }, { -1, 1, 1.41421356 }, { -1, -1, 1.41421356 } }

function R.flood(G, src, cfg)
    local dist, par = {}, {}
    if src == nil then return { dist = dist, par = par } end
    dist[src] = 0
    local h = { { 0, src } }
    local tick = (cfg or R.CFG).tick
    local cnt = 0
    while #h > 0 do
        cnt = cnt + 1
        if tick and cnt % 400 == 0 then tick() end
        local top = hpop(h)
        local d, i = top[1], top[2]
        if d <= (dist[i] or 1e18) then
            local ix, iz = i % G.nx, math.floor(i / G.nx)
            for k = 1, 8 do
                local nb = NB[k]
                local jx, jz = ix + nb[1], iz + nb[2]
                if jx >= 0 and jz >= 0 and jx < G.nx and jz < G.nz then
                    local j = jz * G.nx + jx
                    local okd = G.free[j]
                    if okd and nb[3] > 1 then okd = G.free[iz * G.nx + jx] and G.free[jz * G.nx + ix] end
                    if okd then
                        local nd = d + nb[3] * G.c * 0.5 * ((G.cost[i] or 1) + (G.cost[j] or 1))
                        if nd < (dist[j] or 1e18) - 1e-9 then
                            dist[j] = nd
                            par[j] = i
                            hpush(h, nd, j)
                        end
                    end
                end
            end
        end
    end
    return { dist = dist, par = par, src = src }
end

local function los(G, a, b)
    local ax, az = center_of(G, a)
    local bx, bz = center_of(G, b)
    local d = len2(bx - ax, bz - az)
    local n = math.max(1, math.ceil(d / (G.c * 0.5)))
    for k = 0, n do
        local t = k / n
        local i = cell_of(G, lerp(ax, bx, t), lerp(az, bz, t))
        if i == nil or not G.free[i] then return false end
        if (G.cost[i] or 1) > 1.01 and (G.cost[a] or 1) <= 1.01 and (G.cost[b] or 1) <= 1.01 then return false end
    end
    return true
end

--- Path from the flood's source to cell `dst`, string-pulled, as {x,z} points.
function R.trace(G, fl, dst, sx, sz, ex, ez)
    if dst == nil or fl.dist[dst] == nil then return nil end
    local cells = {}
    local i = dst
    while i ~= nil do cells[#cells + 1] = i i = fl.par[i] end
    local n = #cells
    for k = 1, math.floor(n / 2) do cells[k], cells[n - k + 1] = cells[n - k + 1], cells[k] end
    local keep = { cells[1] }
    local a = 1
    while a < #cells do
        local b = #cells
        while b > a + 1 and not los(G, cells[a], cells[b]) do b = b - 1 end
        keep[#keep + 1] = cells[b]
        a = b
    end
    local pts = { { x = sx, z = sz } }
    for k = 2, #keep - 1 do local x, z = center_of(G, keep[k]) pts[#pts + 1] = { x = x, z = z } end
    pts[#pts + 1] = { x = ex, z = ez }
    return pts
end

function R.poly_len(pts)
    local L = 0
    for k = 2, #pts do L = L + len2(pts[k].x - pts[k - 1].x, pts[k].z - pts[k - 1].z) end
    return L
end

-- ── venues ────────────────────────────────────────────────────────────────
--- Split stops into venues: bounding circles within cfg.cluster_gap link. Deterministic.
function R.clusters(pieces, cfg)
    cfg = cfg or R.CFG
    local n = #pieces
    local par = {}
    for i = 1, n do par[i] = i end
    local function root(i) while par[i] ~= i do par[i] = par[par[i]] i = par[i] end return i end
    local function rad(p) return 0.5 * len2(p.W or 2, p.L or 2) end
    for i = 1, n do
        for j = i + 1, n do
            local a, b = pieces[i], pieces[j]
            if len2(a.cx - b.cx, a.cz - b.cz) - rad(a) - rad(b) <= cfg.cluster_gap then par[root(i)] = root(j) end
        end
    end
    local by, out = {}, {}
    for i = 1, n do
        local r = root(i)
        if by[r] == nil then by[r] = {} out[#out + 1] = by[r] end
        by[r][#by[r] + 1] = pieces[i]
    end
    return out
end

local function area(p) return (p.W or 0) * (p.L or 0) end

--- Deterministic stop order (name, then position): every headset sorts the same.
function R.sorted(pieces)
    local P = {}
    for i = 1, #pieces do P[i] = pieces[i] end
    table.sort(P, function(a, b)
        if a.name ~= b.name then return a.name < b.name end
        if math.abs(a.cx - b.cx) > 1e-3 then return a.cx < b.cx end
        return a.cz < b.cz
    end)
    return P
end

--- Default start: a fixed stop with a start point, else the most central stop
--- (then prefer_start, then the largest, then position).
function R.elect(P)
    local mx, mz = 0, 0
    for _, p in ipairs(P) do mx, mz = mx + p.cx / #P, mz + p.cz / #P end
    local function better(a, b)
        local ca, cb = len2(a.cx - mx, a.cz - mz), len2(b.cx - mx, b.cz - mz)
        if math.abs(ca - cb) > 0.25 then return ca < cb end
        if (a.prefer_start == true) ~= (b.prefer_start == true) then return a.prefer_start == true end
        local da, db = area(a), area(b)
        if math.abs(da - db) > 0.05 then return da > db end
        if math.abs(a.cx - b.cx) > 1e-3 then return a.cx < b.cx end
        return a.cz < b.cz
    end
    local best = nil
    for _, p in ipairs(P) do
        if not p.flex and p.start ~= nil and (best == nil or better(p, best)) then best = p end
    end
    if best ~= nil then return best end
    for _, p in ipairs(P) do
        if p.flex and (best == nil or better(p, best)) then best = p end
    end
    return best
end

local function venue_grid(P, world, cfg, start)
    local x0, z0, x1, z1 = 1e9, 1e9, -1e9, -1e9
    for _, p in ipairs(P) do
        for _, e in ipairs(R.edges(p)) do
            x0, z0 = math.min(x0, e.ax), math.min(z0, e.az)
            x1, z1 = math.max(x1, e.ax), math.max(z1, e.az)
        end
    end
    x0, z0, x1, z1 = x0 - cfg.margin, z0 - cfg.margin, x1 + cfg.margin, z1 + cfg.margin
    local half = cfg.max_span * 0.5
    x0, x1 = math.max(x0, start.cx - half), math.min(x1, start.cx + half)
    z0, z1 = math.max(z0, start.cz - half), math.min(z1, start.cz + half)
    local gcfg = {}
    for k, v in pairs(cfg) do gcfg[k] = v end
    while ((x1 - x0) / gcfg.cell) * ((z1 - z0) / gcfg.cell) > cfg.max_cells do gcfg.cell = gcfg.cell * 1.25 end
    if gcfg.cell > 0.4 then return nil, string.format('venue too big (%.0f x %.0f m)', x1 - x0, z1 - z0) end
    return R.grid(world, x0, z0, x1, z1, gcfg, function(x, z)
        for _, p in ipairs(P) do if inside(p, x, z, 0.05) then return cfg.inside_cost end end
        return 1
    end)
end
R.venue_grid = venue_grid

local function fixed_in(p, is_start)
    if is_start and p.start ~= nil then
        local s = p.start
        return s.x - s.fx * 0.6, s.z - s.fz * 0.6
    end
    local g = p.inp or p.out or p.start
    return g.x, g.z
end
local function fixed_out(p)
    local g = p.out or p.start or p.inp
    return g.x, g.z
end
R.fixed_in, R.fixed_out = fixed_in, fixed_out

-- ── door graph (rect data): straight lines inside a rect, door midpoints as vertices ──
local function all_have_rects(P)
    for _, p in ipairs(P) do if p.rect == nil then return false end end
    return #P > 0
end
R.all_have_rects = all_have_rects

--- The router's view of the park: the stops plus `graph` (rect-only entries: every rect of the park
--- with its openings, from core's space map — not tour stops). A graph rect a stop already sits on
--- is skipped. With no graph only the stops' own rects are known (Phase 1 default).
function R.with_graph(P, graph)
    if graph == nil or #graph == 0 then return P end
    local out, have = {}, {}
    for _, p in ipairs(P) do out[#out + 1] = p if p.rect ~= nil then have[p.rect.id] = true end end
    for _, g in ipairs(graph) do
        if g.rect ~= nil and not have[g.rect.id] then have[g.rect.id] = true out[#out + 1] = g end
    end
    return out
end

--- The rects a point belongs to: every stop whose footprint holds it (15 cm slack)
--- and the `to` rect of any opening within cfg.door_slack (a doorway is in both rooms).
--- Open-area rects (all sides unknown) also share the region R.OPEN_AREA: a straight
--- walk joins any two of them (a gym floor, a courtyard).
R.OPEN_AREA = '*open_area'
function R.point_rects(P, x, z, cfg)
    local slack = (cfg or R.CFG).door_slack or 0.6
    local set = {}
    for _, p in ipairs(P) do
        if p.rect ~= nil then
            if inside(p, x, z, -0.15) then
                set[p.rect.id] = true
                if p.rect.open_area then set[R.OPEN_AREA] = true end
            end
            for _, o in ipairs(p.rect.opens or {}) do
                if o.to ~= nil then
                    local dx, dz = o.bx - o.ax, o.bz - o.az
                    local L2 = dx * dx + dz * dz
                    local t = L2 > 0 and math.max(0, math.min(1, ((x - o.ax) * dx + (z - o.az) * dz) / L2)) or 0
                    if len2(x - o.ax - t * dx, z - o.az - t * dz) < slack then set[o.to] = true set[p.rect.id] = true end
                end
            end
        end
    end
    return set
end

--- Router over door midpoints. R.route(a, b) with a/b = {x, z, rects} -> distance, polyline, doors.
function R.door_router(P)
    local doors = {}
    local seen = {}
    for _, p in ipairs(P) do
        for _, o in ipairs((p.rect and p.rect.opens) or {}) do
            if o.to ~= nil then
                local mx, mz = (o.ax + o.bx) * 0.5, (o.az + o.bz) * 0.5
                local key = string.format('%.1f:%.1f', mx, mz)
                if not seen[key] then
                    seen[key] = true
                    local rs = { [p.rect.id] = true, [o.to] = true }
                    if p.rect.open_area then rs[R.OPEN_AREA] = true end
                    doors[#doors + 1] = { x = mx, z = mz, rects = rs,
                                          width = len2(o.bx - o.ax, o.bz - o.az), type = o.type, from = p.rect.id, to = o.to }
                end
            end
        end
    end
    local DR = { doors = doors }
    local function share(a, b)
        for k in pairs(a.rects) do if b.rects[k] then return true end end
        return false
    end
    function DR.route(a, b)
        local V = { a }
        for i = 1, #doors do V[#V + 1] = doors[i] end
        V[#V + 1] = b
        local n = #V
        local dist, prev, done = {}, {}, {}
        for i = 1, n do dist[i] = 1e18 end
        dist[1] = 0
        for _ = 1, n do
            local u, bd = nil, 1e18
            for i = 1, n do if not done[i] and dist[i] < bd then u, bd = i, dist[i] end end
            if u == nil or u == n then break end
            done[u] = true
            for v = 2, n do
                if not done[v] and share(V[u], V[v]) then
                    local nd = dist[u] + len2(V[v].x - V[u].x, V[v].z - V[u].z)
                    if nd < dist[v] then dist[v], prev[v] = nd, u end
                end
            end
        end
        if dist[n] >= 1e17 then return nil, nil end
        local pts, used, i = {}, {}, n
        while i ~= nil do
            table.insert(pts, 1, { x = V[i].x, z = V[i].z })
            if i > 1 and i < n then table.insert(used, 1, V[i]) end
            i = prev[i]
        end
        return dist[n], pts, used
    end
    return DR
end

-- ── tour search ───────────────────────────────────────────────────────────
local function permutations(n, fn)
    local a = {}
    for i = 1, n do a[i] = i end
    local function rec(k)
        if k > n then return fn(a) end
        for i = k, n do
            a[k], a[i] = a[i], a[k]
            local stop = rec(k + 1)
            a[k], a[i] = a[i], a[k]
            if stop then return true end
        end
    end
    rec(1)
end

--- Default entry/exit ends: the chosen candidates, as walk-in / walk-out poses.
function R.default_ends(o, p, nodes, cfg)
    local ci, co = nodes[o.inn].cand, nodes[o.out].cand
    local E = { x = ci.x, z = ci.z, dx = -ci.nx, dz = -ci.nz, edge = ci.edge, width = ci.width, to = ci.to, type = ci.type }
    local X = { x = co.x, z = co.z, dx = co.nx, dz = co.nz, edge = co.edge, width = co.width, to = co.to, type = co.type }
    if o.deadend then E.oneDoor, X.oneDoor = true, true end
    return E, X
end

--- Which stop starts, the order, each choosing stop's entry/exit.
--- opts: cfg, log, prepare(P), elect(P), ends(o, p, nodes, cfg), start (id), keep_order, loop (default true)
--- Returns dec = { start, solo, order = {id..}, d = { [id] = {E, X, deadend, role} }, cost }, ctx | nil, why
function R.search(pieces, world, opts)
    opts = opts or {}
    local cfg = opts.cfg or R.CFG
    local log = opts.log or function() end
    local tick = cfg.tick
    if #pieces > cfg.max_pieces then return nil, 'too many pieces (' .. #pieces .. ')' end
    local P = R.sorted(pieces)
    if opts.prepare then opts.prepare(P) end
    local start = nil
    if opts.start ~= nil then for _, p in ipairs(P) do if p.id == opts.start then start = p end end end
    if start == nil then start = (opts.elect or R.elect)(P) end
    if start == nil then return nil, 'no start candidate' end
    if not start.flex and start.out == nil and #P > 1 then
        return { start = start.id, order = { start.id }, d = {}, closed_track = true }, {}
    end
    if #P == 1 then
        local dec = { start = start.id, order = { start.id }, d = {} }
        if start.flex then dec.solo = true dec.d[start.id] = { role = 'solo' } end
        return dec, {}
    end

    for _, p in ipairs(P) do
        if p.flex then
            p.opens = R.openings(p, world, cfg)
            p.cands = R.candidates(p, p.opens, cfg)
        end
    end
    local doors = all_have_rects(P)
    local G, DR = nil, nil
    local PG = R.with_graph(P, opts.graph)
    if doors then
        DR = R.door_router(PG)
        log(string.format('door graph: %d doorways from rect data', #DR.doors))
    else
        local gerr
        G, gerr = venue_grid(P, world, cfg, start)
        if G == nil then return nil, gerr end
        log(string.format('grid %dx%d (cell %.2f m), %d blocked', G.nx, G.nz, G.c, G.blocked))
    end

    local nodes = {}
    local function node(x, z, piece, role, extra)
        local nd = { x = x, z = z, piece = piece, role = role }
        if doors then nd.rects = R.point_rects(PG, x, z, cfg) else nd.cell = R.snap(G, x, z, 0.6) end
        if extra then for k, v in pairs(extra) do nd[k] = v end end
        nodes[#nodes + 1] = nd
        return #nodes
    end
    local opt = {}
    for _, p in ipairs(P) do
        if p.flex then
            local ids = {}
            for _, c in ipairs(p.cands) do ids[#ids + 1] = node(c.x, c.z, p.id, 'cand', { cand = c }) end
            local list = {}
            for a = 1, #ids do
                for b = 1, #ids do
                    if a ~= b and nodes[ids[a]].cand.open ~= nodes[ids[b]].cand.open then
                        list[#list + 1] = { inn = ids[a], out = ids[b], pen = 0 }
                    end
                end
            end
            for a = 1, #ids do
                list[#list + 1] = { inn = ids[a], out = ids[a], pen = cfg.deadend_pen, deadend = true }
            end
            opt[p.id] = list
        else
            local ix, iz = fixed_in(p, p == start)
            local ox, oz = fixed_out(p)
            opt[p.id] = { { inn = node(ix, iz, p.id, 'in'), out = node(ox, oz, p.id, 'out'), pen = 0 } }
        end
    end
    if #nodes > cfg.max_nodes then return nil, 'too many doorway candidates (' .. #nodes .. ')' end
    if #(opt[start.id] or {}) == 0 then
        -- the elected start has no usable opening (a walled-in or unscanned rect): the next best that has one
        -- (cc_venue crashed here; with doors everywhere this never triggers, so plans are unchanged)
        local cands = {}
        for _, p in ipairs(P) do if #(opt[p.id] or {}) > 0 then cands[#cands + 1] = p end end
        if #cands == 0 then return nil, 'no stop has a usable opening' end
        log('start ' .. tostring(start.name) .. ' has no usable opening; electing again')
        start = (opts.elect or R.elect)(cands) or cands[1]
    end
    local D
    if doors then
        local memo = {}
        D = function(a, b)
            local k = a * 1000 + b
            local v = memo[k]
            if v == nil then
                if tick then tick() end
                v = (DR.route(nodes[a], nodes[b])) or 1e6
                memo[k] = v
            end
            return v
        end
    else
        local floods = {}
        for k, nd in ipairs(nodes) do floods[k] = R.flood(G, nd.cell, cfg) end
        D = function(a, b) return floods[a].dist[nodes[b].cell] or 1e6 end
    end

    local loop = opts.loop ~= false
    local rest = {}
    if opts.keep_order then
        -- authored order: the caller's list order (minus the start), never reordered
        for _, p in ipairs(pieces) do if p ~= start and #(opt[p.id] or {}) > 0 then rest[#rest + 1] = p end end
    else
        for _, p in ipairs(P) do if p ~= start and #(opt[p.id] or {}) > 0 then rest[#rest + 1] = p end end
    end

    local function score(order, so)
        local prev = { { c = 0, out = so.out } }
        local layers = {}
        for k = 1, #order do
            local cur = {}
            for oi, o in ipairs(opt[order[k].id]) do
                local best, bf = nil, nil
                for pi, pv in ipairs(prev) do
                    local c = pv.c + D(pv.out, o.inn) + o.pen
                    if best == nil or c < best then best, bf = c, pi end
                end
                cur[oi] = { c = best, from = bf, out = o.out, o = o }
            end
            layers[k] = cur
            prev = cur
        end
        local best, bi = nil, nil
        for oi, pv in ipairs(prev) do
            local c = pv.c + (loop and D(pv.out, so.inn) or 0)
            if best == nil or c < best then best, bi = c, oi end
        end
        local pick = {}
        local k, oi = #order, bi
        while k >= 1 do pick[k] = layers[k][oi].o oi = layers[k][oi].from k = k - 1 end
        return best + so.pen, pick
    end
    local function greedy(so)
        local left, order, cur = {}, {}, so.out
        for i = 1, #rest do left[i] = rest[i] end
        while #left > 0 do
            local bd, bi, bo = nil, nil, nil
            for i, p in ipairs(left) do
                for _, o in ipairs(opt[p.id]) do
                    local dd = D(cur, o.inn) + o.pen
                    if bd == nil or dd < bd then bd, bi, bo = dd, i, o end
                end
            end
            order[#order + 1] = table.remove(left, bi)
            cur = bo.out
        end
        return order
    end

    local best_so, best_c = nil, nil
    for _, so in ipairs(opt[start.id]) do
        local c = score(opts.keep_order and rest or greedy(so), so)
        if best_c == nil or c < best_c - 1e-6 then best_so, best_c = so, c end
    end
    local best_order, best_pick
    if opts.keep_order then
        best_order = rest
        best_c, best_pick = score(best_order, best_so)
    elseif #rest <= cfg.max_exact then
        local nperm = 0
        best_c = nil
        permutations(#rest, function(a)
            nperm = nperm + 1
            if tick and nperm % 50 == 0 then tick() end
            local order = {}
            for i = 1, #a do order[i] = rest[a[i]] end
            local c, pick = score(order, best_so)
            if best_c == nil or c < best_c - 1e-6 then
                best_c, best_pick, best_order = c, pick, order
            end
        end)
    else
        best_order = greedy(best_so)
        best_c, best_pick = score(best_order, best_so)
        local improved, passes = true, 0
        while improved and passes < 6 do
            improved, passes = false, passes + 1
            for i = 1, #best_order - 1 do
                for j = i + 1, #best_order do
                    if tick then tick() end
                    local o = {}
                    for k = 1, #best_order do o[k] = best_order[k] end
                    local a, b = i, j
                    while a < b do o[a], o[b] = o[b], o[a] a, b = a + 1, b - 1 end
                    local c, pick = score(o, best_so)
                    if c < best_c - 1e-6 then best_c, best_pick, best_order, improved = c, pick, o, true end
                end
            end
        end
    end

    local ends = opts.ends or R.default_ends
    local dec = { start = start.id, order = { start.id }, d = {}, cost = best_c, loop = loop }
    if start.flex then
        local E, X = ends(best_so, start, nodes, cfg)
        dec.d[start.id] = { E = E, X = X, deadend = best_so.deadend or false, role = 'start' }
    end
    for k = 1, #best_order do
        local p, o = best_order[k], best_pick[k]
        dec.order[k + 1] = p.id
        if p.flex then
            local E, X = ends(o, p, nodes, cfg)
            dec.d[p.id] = { E = E, X = X, deadend = o.deadend or false, role = 'normal' }
        end
    end
    return dec, { G = G, DR = DR, nodes = nodes }
end

--- Walking links between consecutive stops of a decided tour, incl. the closing leg (loop).
function R.links(P, dec, world, cfg, G, graph)
    cfg = cfg or R.CFG
    local byid = {}
    for _, p in ipairs(P) do byid[p.id] = p end
    local n = #dec.order
    if n < 2 then return {} end
    local start = byid[dec.start]
    local DR = nil
    local PG = R.with_graph(P, graph)
    if all_have_rects(P) then DR = R.door_router(PG)
    else
        if G == nil then G = venue_grid(P, world, cfg, start) end
        if G == nil then return {} end
    end
    local function outp(id)
        local d = dec.d[id]
        if d and d.X then return d.X.x, d.X.z end
        return fixed_out(byid[id])
    end
    local function inp(id)
        local d = dec.d[id]
        if d and d.E then return d.E.x, d.E.z end
        return fixed_in(byid[id], id == dec.start)
    end
    local links = {}
    local last = (dec.loop == false) and (n - 1) or n
    for k = 1, last do
        local a, b = dec.order[k], dec.order[(k % n) + 1]
        local ax, az = outp(a)
        local bx, bz = inp(b)
        local pts, used
        if DR ~= nil then
            local _, p2, u2 = DR.route({ x = ax, z = az, rects = R.point_rects(PG, ax, az, cfg) }, { x = bx, z = bz, rects = R.point_rects(PG, bx, bz, cfg) })
            pts, used = p2, u2
        else
            local fl = R.flood(G, R.snap(G, ax, az, 0.6), cfg)
            pts = R.trace(G, fl, R.snap(G, bx, bz, 0.6), ax, az, bx, bz)
        end
        links[k] = { from = a, to = b, closing = (k == n), pts = pts, len = pts and R.poly_len(pts) or nil,
                     straight = len2(bx - ax, bz - az), doors = used }
    end
    return links
end

--- search + links. Returns plan = { order = {stops}, pieces = {[id] = d}, links, dec, cost } | nil, why
function R.plan(pieces, world, opts)
    opts = opts or {}
    local cfg = opts.cfg or R.CFG
    local dec, ctx = R.search(pieces, world, opts)
    if dec == nil then return nil, ctx end
    local byid = {}
    for _, p in ipairs(pieces) do byid[p.id] = p end
    local plan = { order = {}, pieces = {}, dec = dec, cost = dec.cost, solo = dec.solo, closed_track = dec.closed_track }
    for _, id in ipairs(dec.order) do plan.order[#plan.order + 1] = byid[id] end
    for id, d in pairs(dec.d) do plan.pieces[id] = d end
    plan.links = (dec.solo or dec.closed_track) and {} or R.links(pieces, dec, world, cfg, ctx and ctx.G, opts.graph)
    -- stops no walk reaches (no doorway into their rect): reported, never silently dropped
    plan.unreachable, plan.dropped = {}, {}
    local n = #plan.links
    for k = 1, n do
        local inn = plan.links[k]
        local out = plan.links[(k % n) + 1]
        if inn.pts == nil and (out == nil or out.pts == nil or n == 1) then plan.unreachable[#plan.unreachable + 1] = inn.to end
    end
    -- choosing stops with no usable opening are left out of the order (cc_venue did this silently)
    local inorder = {}
    for _, id in ipairs(dec.order) do inorder[id] = true end
    for _, p in ipairs(pieces) do if not inorder[p.id] then plan.dropped[#plan.dropped + 1] = p.id end end
    return plan
end

--- One walk between two points over a set of stops (door graph when every stop has
--- rect data, else the grid when `world` senses walls, else a straight line).
--- Returns { ok, pts, len, hops, doors, source, why }.
function R.route(P, ax, az, bx, bz, world, cfg, graph)
    cfg = cfg or R.CFG
    local straight = { { x = ax, z = az }, { x = bx, z = bz } }
    local withData = {}
    for _, p in ipairs(P) do if p.rect ~= nil then withData[#withData + 1] = p end end
    P = R.with_graph(withData, graph)
    if #P > 0 and all_have_rects(P) then
        local DR = R.door_router(P)
        local ra, rb = R.point_rects(P, ax, az, cfg), R.point_rects(P, bx, bz, cfg)
        if next(ra) == nil or next(rb) == nil then
            return { ok = true, pts = straight, len = R.poly_len(straight), hops = 0, doors = {}, source = 'straight',
                     why = 'an end is outside every known rect' }
        end
        local d, pts, used = DR.route({ x = ax, z = az, rects = ra }, { x = bx, z = bz, rects = rb })
        if d == nil then return { ok = false, pts = straight, len = R.poly_len(straight), hops = 0, doors = {}, source = 'doors', why = 'no doorway path' } end
        return { ok = true, pts = pts, len = d, hops = #used, doors = used, source = 'doors' }
    end
    if world ~= nil and world.cell_blocked ~= nil then
        local box = { cx = (ax + bx) * 0.5, cz = (az + bz) * 0.5, yaw = 0, W = math.abs(bx - ax) + 0.1, L = math.abs(bz - az) + 0.1 }
        local Q = { box }
        for _, p in ipairs(P) do Q[#Q + 1] = p end
        local G = venue_grid(Q, world, cfg, box)
        if G ~= nil then
            local fl = R.flood(G, R.snap(G, ax, az, 0.6), cfg)
            local pts = R.trace(G, fl, R.snap(G, bx, bz, 0.6), ax, az, bx, bz)
            if pts ~= nil then return { ok = true, pts = pts, len = R.poly_len(pts), hops = 0, doors = {}, source = 'grid' } end
            return { ok = false, pts = straight, len = R.poly_len(straight), hops = 0, doors = {}, source = 'grid', why = 'no walking path' }
        end
    end
    return { ok = true, pts = straight, len = R.poly_len(straight), hops = 0, doors = {}, source = 'straight', why = 'no park data' }
end

-- ── polylines ─────────────────────────────────────────────────────────────
--- Uniform resample at `step`: {x, z, fx, fz, s}; last station at the exact end.
function R.resample(pts, step)
    local out = {}
    if pts == nil or #pts < 2 then return out end
    local cum = { 0 }
    for k = 2, #pts do cum[k] = cum[k - 1] + len2(pts[k].x - pts[k - 1].x, pts[k].z - pts[k - 1].z) end
    local L = cum[#cum]
    local n = math.max(1, math.floor(L / step + 0.5))
    local seg = 2
    for i = 0, n do
        local s = L * i / n
        while seg < #pts and cum[seg] < s do seg = seg + 1 end
        local a, b = pts[seg - 1], pts[seg]
        local l = cum[seg] - cum[seg - 1]
        local t = (l > 1e-9) and (s - cum[seg - 1]) / l or 0
        out[#out + 1] = { x = lerp(a.x, b.x, t), z = lerp(a.z, b.z, t), s = s }
    end
    local closed = #out > 2 and len2(out[1].x - out[#out].x, out[1].z - out[#out].z) < 1e-3
    for i = 1, #out do
        local a, b
        if closed and (i == 1 or i == #out) then a, b = out[#out - 1], out[2]
        else a, b = out[math.max(1, i - 1)], out[math.min(#out, i + 1)] end
        local fx, fz = b.x - a.x, b.z - a.z
        local m = len2(fx, fz)
        if m < 1e-9 then fx, fz, m = 0, 1, 1 end
        out[i].fx, out[i].fz = fx / m, fz / m
    end
    out.len = L
    out.closed = closed
    return out
end

--- Station nearest to arc length s (wraps on a closed line).
function R.at(st, s)
    if #st == 0 then return nil end
    local L = st.len or st[#st].s
    if st.closed and L > 0 then s = s % L end
    s = math.max(0, math.min(L, s))
    local i = 1
    if L > 0 then i = math.floor(s / L * (#st - 1) + 0.5) + 1 end
    return st[math.max(1, math.min(#st, i))]
end

--- Arc length of the point on a polyline nearest (x, z), and its distance.
function R.project(pts, x, z)
    local best, bs, acc = nil, 0, 0
    for k = 2, #pts do
        local a, b = pts[k - 1], pts[k]
        local dx, dz = b.x - a.x, b.z - a.z
        local L2 = dx * dx + dz * dz
        local t = L2 > 0 and math.max(0, math.min(1, ((x - a.x) * dx + (z - a.z) * dz) / L2)) or 0
        local d = len2(x - a.x - t * dx, z - a.z - t * dz)
        if best == nil or d < best then best, bs = d, acc + t * math.sqrt(L2) end
        acc = acc + math.sqrt(L2)
    end
    return bs, best
end

--- Arrow poses along a walking path (skipping the first 0.4 / last 0.35 m).
function R.link_arrows(pts, gap)
    if pts == nil or #pts < 2 then return {} end
    local st = R.resample(pts, 0.1)
    local L = st.len or 0
    local out = {}
    local s = 0.4
    while s <= L - 0.35 do out[#out + 1] = R.at(st, s) s = s + gap end
    if #out == 0 and L > 0.3 then out[1] = R.at(st, L * 0.5) end
    return out
end

-- ── a compact plan on the wire (leader-published; everyone decodes) ───────
-- t = { r = hash, n = count, p<k> = 'cx,cz|role|ex,ez|xx,xz' } in tour order, 2-decimal
-- metres in the frame `to_park` maps to (park-local on the wire). Roles: f fixed, s start,
-- o solo, n normal; + 'd' = a spur (in and out through one opening).
local function q(v) return string.format('%.2f', v) end
function R.encode(dec, pieces, to_park)
    to_park = to_park or function(x, z) return x, z end
    local byid = {}
    for _, p in ipairs(pieces) do byid[p.id] = p end
    local t = { n = #dec.order }
    local all = {}
    for k, id in ipairs(dec.order) do
        local p = byid[id]
        local cx, cz = to_park(p.cx, p.cz)
        local s = q(cx) .. ',' .. q(cz)
        local d = dec.d[id]
        if d == nil then s = s .. '|f'
        else
            local role = (d.role == 'solo') and 'o' or ((d.role == 'start') and 's' or 'n')
            if d.deadend then role = role .. 'd' end
            s = s .. '|' .. role
            if d.E ~= nil then
                local ex, ez = to_park(d.E.x, d.E.z)
                local xx, xz = to_park(d.X.x, d.X.z)
                s = s .. '|' .. q(ex) .. ',' .. q(ez) .. '|' .. q(xx) .. ',' .. q(xz)
            end
        end
        t['p' .. k] = s
        all[#all + 1] = s
    end
    local h = 5381
    local str = table.concat(all, ';')
    for i = 1, #str do h = (h * 33 + string.byte(str, i)) % 4294967296 end
    t.r = string.format('%08x', h)
    return t
end

function R.blob(t)
    local parts = { 'r=' .. tostring(t.r), 'n=' .. tostring(t.n) }
    for k = 1, t.n do parts[#parts + 1] = t['p' .. k] end
    return table.concat(parts, '~')
end
function R.unblob(b)
    if type(b) ~= 'string' or b == '' then return nil end
    local t, k = {}, 0
    for part in string.gmatch(b, '[^~]+') do
        local key, val = string.match(part, '^([rn])=(.*)$')
        if key == 'r' then t.r = val
        elseif key == 'n' then t.n = tonumber(val)
        else k = k + 1 t['p' .. k] = part end
    end
    if t.r == nil or t.n == nil or k ~= t.n then return nil end
    return t
end

--- Decode a published plan onto THIS headset's stops (matched by centre within
--- `tol`, default 0.35 m). nil, why when they don't match (a stop moved/appeared).
function R.decode(t, pieces, from_park, tol)
    from_park = from_park or function(x, z) return x, z end
    tol = tol or 0.35
    if t == nil or t.n == nil then return nil, 'empty' end
    local n = math.floor(tonumber(t.n) or 0)
    if n < 1 or n ~= #pieces then return nil, 'stop count ' .. tostring(n) .. ' vs ' .. #pieces end
    local used = {}
    local dec = { order = {}, d = {}, r = t.r, wire = t }
    for k = 1, n do
        local s = t['p' .. k]
        if type(s) ~= 'string' then return nil, 'missing p' .. k end
        local f = {}
        for part in string.gmatch(s, '[^|]+') do f[#f + 1] = part end
        local cx, cz = string.match(f[1] or '', '^([%-%d%.]+),([%-%d%.]+)$')
        if cx == nil then return nil, 'bad p' .. k end
        local wx, wz = from_park(tonumber(cx), tonumber(cz))
        local best, bd = nil, nil
        for _, p in ipairs(pieces) do
            if not used[p.id] then
                local dd = len2(p.cx - wx, p.cz - wz)
                if bd == nil or dd < bd then best, bd = p, dd end
            end
        end
        if best == nil or bd > tol then return nil, 'no stop at p' .. k end
        used[best.id] = true
        dec.order[k] = best.id
        local role = f[2] or 'f'
        local r1 = string.sub(role, 1, 1)
        if r1 == 's' or r1 == 'o' or (k == 1 and role == 'f') then dec.start = best.id end
        if role ~= 'f' then
            local d = { deadend = string.find(role, 'd', 2, true) ~= nil,
                        role = (r1 == 'o') and 'solo' or ((r1 == 's') and 'start' or 'normal') }
            if d.role == 'solo' then dec.solo = true end
            if f[3] ~= nil and f[4] ~= nil then
                local ex, ez = string.match(f[3], '^([%-%d%.]+),([%-%d%.]+)$')
                local xx, xz = string.match(f[4], '^([%-%d%.]+),([%-%d%.]+)$')
                if ex == nil or xx == nil then return nil, 'bad ends p' .. k end
                local Ex, Ez = from_park(tonumber(ex), tonumber(ez))
                local Xx, Xz = from_park(tonumber(xx), tonumber(xz))
                d.E = { x = Ex, z = Ez }
                d.X = { x = Xx, z = Xz }
            end
            dec.d[best.id] = d
        end
    end
    if dec.start == nil then dec.start = dec.order[1] end
    return dec
end

return R
