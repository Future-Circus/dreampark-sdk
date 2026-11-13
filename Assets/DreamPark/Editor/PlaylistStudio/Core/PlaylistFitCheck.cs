#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.SpaceMap;
using Defective.JSON;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>One space of the fit matrix (a synthetic SpaceMap, built like core's offline packer tests).</summary>
    public sealed class PlaylistFitSpace
    {
        public string id, label;
        public SpaceMapData map;
        public int rooms;
        public float areaM2;
        public bool indoor, outdoor;
    }

    /// <summary>The planner's answer for one playlist in one space.</summary>
    public sealed class PlaylistFitRow
    {
        public PlaylistFitSpace space;
        /// fits = every stop stands for its whole leg; streams = it plays, but some stops load on a
        /// finished stop's floor or legs swap in place; no-fit = a start/boss/end has no floor (or the
        /// playlist's own `fit` rules this space out); skipped = the playlist says it needs more than this space.
        public string verdict;
        public string status, layoutMode, reason;
        public int streamed, alternates, shrinks, roomsUsed, swaps;
        public bool declared;
        public PlaylistPlan plan;
        public readonly List<string> notes = new List<string>();
    }

    /// <summary>
    /// The Studio's fit check: the SHARED core planner (PlaylistPacker, PLAYLISTS.md § 6) over a fixed matrix
    /// of spaces, from a 12×18 ft room to a park. Pure and deterministic.
    /// </summary>
    public static class PlaylistFitCheck
    {
        public const string Fits = "fits", Streams = "streams", NoFit = "no-fit", Skipped = "skipped";

        static List<PlaylistFitSpace> _matrix;
        public static IReadOnlyList<PlaylistFitSpace> Matrix => _matrix ??= BuildMatrix();

        /// The playlist as the planner reads it (the authoring JSON, exactly what ships).
        public static PlaylistSpec SpecFor(PackagePlaylist playlist, string titleId)
            => PlaylistSpec.FromJson(PlaylistStudioJson.WritePlaylist(playlist, titleId), titleId);

        /// Does the playlist's own `fit` allow this space? (A no here is "skipped", never a failure.)
        public static bool Declares(PackagePlaylist p, PlaylistFitSpace s)
        {
            PlaylistFit f = p.fit ?? new PlaylistFit();
            if (f.minRooms > 1 && s.rooms < f.minRooms) return false;
            if (f.minAreaM2 > 0f && s.areaM2 + 0.01f < f.minAreaM2) return false;
            if (!f.indoor && !s.outdoor) return false;
            if (!f.outdoor && !s.indoor) return false;
            return true;
        }

        public static List<PlaylistFitRow> Run(PackagePlaylist playlist, string titleId, PlaylistFootprintBook book,
            IEnumerable<PlaylistFitSpace> spaces = null)
        {
            var rows = new List<PlaylistFitRow>();
            PlaylistSpec spec = SpecFor(playlist, titleId);
            foreach (PlaylistFitSpace space in spaces ?? Matrix)
            {
                var row = new PlaylistFitRow { space = space, declared = Declares(playlist, space) };
                rows.Add(row);
                if (spec == null || spec.legs.Count == 0) { row.verdict = NoFit; row.reason = "no legs with stops"; continue; }
                PlaylistPlan plan;
                try { plan = PlaylistPacker.Pack(spec, space.map, book); }
                catch (Exception e) { row.verdict = NoFit; row.reason = "planner error: " + e.Message; continue; }
                row.plan = plan;
                row.status = plan.status;
                row.layoutMode = plan.layoutMode;
                row.streamed = plan.stops.Count(s => s.streamed);
                row.alternates = plan.stops.Count(s => s.variant == "alternate");
                row.shrinks = plan.stops.Count(s => s.variant == "shrink");
                row.roomsUsed = plan.stops.Where(s => s.placedOk).Select(s => s.roomId).Distinct().Count();
                row.swaps = plan.legs.Count(l => l.nextApplied == "swap_in_place" && l.degraded);
                row.notes.AddRange(plan.notes);
                bool placedAll = plan.stops.Count > 0 && plan.stops.All(s => s.placedOk);
                if (!row.declared)
                {
                    row.verdict = Skipped;
                    row.reason = "the playlist's fit asks for more (" + FitText(playlist.fit) + ")";
                }
                else if (plan.status == "no-fit" || !plan.fits || plan.stops.Count == 0)
                {
                    row.verdict = NoFit;
                    row.reason = !string.IsNullOrEmpty(plan.fitReason) ? plan.fitReason : "no floor for a key stop";
                }
                else if (!placedAll)
                {
                    row.verdict = NoFit;
                    row.reason = "a stage has no floor: " + string.Join(", ", plan.stops.Where(s => !s.placedOk).Select(s => s.attraction));
                }
                else if (row.streamed > 0 || plan.layoutMode == "stream")
                {
                    row.verdict = Streams;
                    row.reason = row.streamed > 0 ? $"{row.streamed} stop(s) stream in on a finished stop's floor" : "one stop after another";
                }
                else
                {
                    row.verdict = Fits;
                    // Legs that wanted to walk on but share the room (one open space) still stand whole: a note, not a downgrade.
                    if (row.swaps > 0) row.reason = $"{row.swaps} leg(s) play in the same room (one open space)";
                }
            }
            return rows;
        }

        /// Saveable: plays (fits or streams) in every matrix space its own `fit` allows, and in at least one.
        public static bool Passes(IEnumerable<PlaylistFitRow> rows, out string why)
        {
            var list = rows.ToList();
            var bad = list.Where(r => r.declared && r.verdict == NoFit).ToList();
            if (bad.Count > 0)
            {
                why = "does not fit " + string.Join(", ", bad.Select(r => r.space.label)) + " (" + bad[0].reason + "). Add smaller alternates or raise fit.minRooms / minAreaM2.";
                return false;
            }
            if (!list.Any(r => r.verdict == Fits || r.verdict == Streams)) { why = "fits none of the test spaces"; return false; }
            why = null;
            return true;
        }

        public static bool FitsSmallRoom(IEnumerable<PlaylistFitRow> rows)
            => rows.Any(r => r.space.id == "small-room" && (r.verdict == Fits || r.verdict == Streams));

        public static string FitText(PlaylistFit f)
        {
            f = f ?? new PlaylistFit();
            var parts = new List<string>();
            if (f.minRooms > 1) parts.Add($"{f.minRooms}+ rooms");
            if (f.minAreaM2 > 0f) parts.Add($"{f.minAreaM2:0.#} m²+");
            if (!f.indoor) parts.Add("outdoor only");
            if (!f.outdoor) parts.Add("indoor only");
            if (f.minPlayers > 0) parts.Add($"{f.minPlayers}+ players");
            return parts.Count == 0 ? "any space" : string.Join(", ", parts);
        }

        // ── the matrix (same archetypes as dreampark-core Tools/playlist-pack-offline) ──

        static List<PlaylistFitSpace> BuildMatrix() => new List<PlaylistFitSpace>
        {
            Space("small-room", "12×18 ft room", SmallRoom()),
            Space("apartment", "Apartment", Apartment()),
            Space("house", "House", House(false)),
            Space("house-backyard", "House + backyard", House(true)),
            Space("party-room", "Party room", PartyRoom()),
            Space("park", "Park", Park()),
        };

        static PlaylistFitSpace Space(string id, string label, SpaceMapData map)
        {
            var g = PlaylistRoomGraph.Build(map);
            return new PlaylistFitSpace
            {
                id = id, label = label, map = map,
                rooms = g.nodes.Count(n => !n.excluded),
                areaM2 = map.rooms.SelectMany(r => r.rects).Sum(r => r.Area),
                indoor = g.nodes.Any(n => !n.outdoor), outdoor = g.nodes.Any(n => n.outdoor),
            };
        }

        static SpaceRect R(string id, float x, float z, float w, float d) => new SpaceRect { rectId = id, x = x, z = z, w = w, d = d, groundMode = "architectural" };

        static void Room(SpaceMapData map, string id, string label, string ground, params SpaceRect[] rects)
        {
            var room = new SpaceRoom { roomId = id, label = label, groundMode = ground };
            foreach (var r in rects) { r.groundMode = ground; room.rects.Add(r); }
            map.rooms.Add(room);
        }

        static void Surface(SpaceMapData map, string kind, float x1, float z1, float x2, float z2)
        {
            var a = new Vector2(x1, z1); var b = new Vector2(x2, z2);
            Vector2 t = (b - a).normalized;
            map.roomPlanSurfaces.Add(new SpaceWall
            {
                rectId = kind + map.roomPlanSurfaces.Count, kind = kind, w = (b - a).magnitude, h = kind == "door" ? 2.0f : 2.4f,
                center = new Vector3((x1 + x2) / 2, kind == "door" ? 1.0f : 1.2f, (z1 + z2) / 2), normal = new Vector3(-t.y, 0, t.x),
            });
        }

        static void WallWithDoor(SpaceMapData map, float x1, float z1, float x2, float z2, float doorAt, float doorW = 0.9f)
        {
            var a = new Vector2(x1, z1); var b = new Vector2(x2, z2);
            float len = (b - a).magnitude; Vector2 t = (b - a) / len;
            Vector2 d0 = a + t * (doorAt - doorW / 2), d1 = a + t * (doorAt + doorW / 2);
            Surface(map, "wall", a.x, a.y, d0.x, d0.y);
            Surface(map, "door", d0.x, d0.y, d1.x, d1.y);
            Surface(map, "wall", d1.x, d1.y, b.x, b.y);
        }

        static SpaceMapData Finish(SpaceMapData map, float sx, float sz)
        {
            map.spawn = new Vector3(sx, 0, sz);
            map.confidence = SpaceMapData.Measured;
            foreach (var room in map.rooms)
            {
                Vector2 c = Vector2.zero; float a = 0;
                foreach (var r in room.rects) { c += r.Center * r.Area; a += r.Area; }
                room.centroid = c / a;
                var r0 = room.rects[0];
                room.polygon.Add(new Vector2(r0.x, r0.z)); room.polygon.Add(new Vector2(r0.x + r0.w, r0.z));
                room.polygon.Add(new Vector2(r0.x + r0.w, r0.z + r0.d)); room.polygon.Add(new Vector2(r0.x, r0.z + r0.d));
            }
            return map;
        }

        /// One 12 × 18 ft room (3.66 × 5.49 m).
        public static SpaceMapData SmallRoom()
        {
            var map = new SpaceMapData();
            Room(map, "room_0", "", "architectural", R("room_0_r0", 0, 0, 3.66f, 5.49f));
            Surface(map, "wall", 0, 0, 3.66f, 0); Surface(map, "wall", 3.66f, 0, 3.66f, 5.49f);
            Surface(map, "wall", 3.66f, 5.49f, 0, 5.49f); Surface(map, "wall", 0, 5.49f, 0, 0);
            return Finish(map, 0.5f, 0.4f);
        }

        /// Living room 20×15 ft (front door) + a 13×13 ft bedroom through a door.
        public static SpaceMapData Apartment()
        {
            var map = new SpaceMapData();
            Room(map, "room_living", "livingRoom", "architectural", R("living_r0", 0, 0, 6f, 4.5f));
            Room(map, "room_bed", "bedroom", "architectural", R("bed_r0", 6f, 0, 4f, 4f));
            WallWithDoor(map, 6f, 0, 6f, 4.5f, 2.5f);
            Surface(map, "wall", 0, 0, 10f, 0); Surface(map, "wall", 0, 4.5f, 6f, 4.5f); Surface(map, "wall", 0, 0, 0, 4.5f);
            Surface(map, "wall", 10f, 0, 10f, 4f); Surface(map, "wall", 6f, 4f, 10f, 4f);
            return Finish(map, 0.5f, 0.5f);
        }

        /// Front door into the living room; kitchen open to it; a hall to two bedrooms and a bathroom;
        /// with `backyard`, a back door from the kitchen onto a 38×39 ft lawn.
        public static SpaceMapData House(bool backyard)
        {
            var map = new SpaceMapData();
            Room(map, "room_living", "livingRoom", "architectural", R("living_r0", 0, 0, 5.5f, 6f));
            Room(map, "room_kitchen", "kitchen", "architectural", R("kitchen_r0", 0, 6f, 5.5f, 4.5f));
            Room(map, "room_hall", "", "architectural", R("hall_r0", 5.5f, 0, 1.4f, 10.5f));
            Room(map, "room_bed1", "bedroom", "architectural", R("bed1_r0", 6.9f, 0, 4.2f, 5f));
            Room(map, "room_bed2", "bedroom", "architectural", R("bed2_r0", 6.9f, 5f, 4.2f, 5.5f));
            Room(map, "room_bath", "bathroom", "architectural", R("bath_r0", 5.5f, 10.5f, 2.5f, 2f));
            WallWithDoor(map, 5.5f, 0, 5.5f, 6f, 3f);
            Surface(map, "wall", 5.5f, 6f, 5.5f, 10.5f);
            WallWithDoor(map, 6.9f, 0, 6.9f, 5f, 2.5f);
            WallWithDoor(map, 6.9f, 5f, 6.9f, 10.5f, 2.5f);
            WallWithDoor(map, 5.5f, 10.5f, 6.9f, 10.5f, 0.7f);
            Surface(map, "wall", 6.9f, 5f, 11.1f, 5f);
            Surface(map, "wall", 0, 0, 11.1f, 0); Surface(map, "wall", 11.1f, 0, 11.1f, 10.5f); Surface(map, "wall", 0, 10.5f, 5.5f, 10.5f);
            Surface(map, "wall", 6.9f, 10.5f, 11.1f, 10.5f);
            if (backyard)
            {
                Room(map, "room_yard", "", "terrain", R("yard_r0", -12f, -1f, 11.6f, 12f));
                Surface(map, "wall", 0, 0, 0, 6f);
                WallWithDoor(map, 0, 6f, 0, 10.5f, 2.2f);
            }
            else Surface(map, "wall", 0, 0, 0, 10.5f);
            return Finish(map, 2.75f, 0.4f);
        }

        /// One open 9 × 12 m party room.
        public static SpaceMapData PartyRoom()
        {
            var map = new SpaceMapData();
            Room(map, "room_party", "", "architectural", R("party_r0", 0, 0, 9f, 12f));
            Surface(map, "wall", 0, 0, 9f, 0); Surface(map, "wall", 9f, 0, 9f, 12f); Surface(map, "wall", 9f, 12f, 0, 12f); Surface(map, "wall", 0, 12f, 0, 0);
            return Finish(map, 4.5f, 0.5f);
        }

        /// A 30 × 20 m open lawn.
        public static SpaceMapData Park()
        {
            var map = new SpaceMapData();
            Room(map, "room_lawn", "", "terrain", R("lawn_r0", 0, 0, 30f, 20f));
            return Finish(map, 1f, 10f);
        }
    }
}
#endif
