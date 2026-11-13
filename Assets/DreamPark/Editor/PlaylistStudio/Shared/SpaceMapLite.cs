#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.SpaceMap
{
    // ─────────────────────────────────────────────────────────────────────
    //  SPACE MAP LITE (SDK editor only). The few dreampark-core SpaceMap types
    //  (Assets/Scripts/SpaceMap/SpaceMapTypes.cs) that the shared playlist
    //  planner reads (PlaylistPacker.Shared.cs, generated from core). SAME names,
    //  fields and math as core, trimmed to what the planner touches, so the
    //  generated copy compiles verbatim. Core never compiles this file (it has
    //  the real types). If a core packer change starts reading a member that is
    //  missing here, the SDK offline harness (Tools/playlist-studio-offline)
    //  fails to compile and names it: add it here with core's meaning.
    // ─────────────────────────────────────────────────────────────────────

    public class SpaceRect
    {
        public string rectId;
        public float x, z, w, d, y, clearance;
        public float yawDeg;
        public SpaceRectBorders borders;
        public string groundMode;
        public string storyId;
        public bool manual;
        public string surface;
        public Vector2 floorSlope;

        public float FloorAt(Vector2 parkXZ)
        {
            if (surface == null) return y;
            Vector2 c = Center;
            return y + floorSlope.x * (parkXZ.x - c.x) + floorSlope.y * (parkXZ.y - c.y);
        }

        public float FloorAt(float parkX, float parkZ) => FloorAt(new Vector2(parkX, parkZ));

        public Vector2 Center => new Vector2(x + w * 0.5f, z + d * 0.5f);
        public float Area => w * d;

        public Vector2 ParkToRect(Vector2 parkPoint)
        {
            Vector2 c = Center;
            float rad = yawDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            float dx = parkPoint.x - c.x, dz = parkPoint.y - c.y;
            return new Vector2(c.x + cos * dx - sin * dz, c.y + sin * dx + cos * dz);
        }

        public Vector2 RectToPark(Vector2 rectPoint)
        {
            Vector2 c = Center;
            float rad = yawDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            float dx = rectPoint.x - c.x, dz = rectPoint.y - c.y;
            return new Vector2(c.x + cos * dx + sin * dz, c.y - sin * dx + cos * dz);
        }

        public bool Contains(Vector2 parkPoint, float tolerance = 0f)
        {
            Vector2 p = ParkToRect(parkPoint);
            return p.x >= x - tolerance && p.x <= x + w + tolerance
                && p.y >= z - tolerance && p.y <= z + d + tolerance;
        }

        public Vector2 Corner(int index)
        {
            return RectToPark(index switch
            {
                0 => new Vector2(x, z),
                1 => new Vector2(x + w, z),
                2 => new Vector2(x + w, z + d),
                _ => new Vector2(x, z + d),
            });
        }
    }

    public class SpaceWall
    {
        public string rectId;
        public string kind;      // wall | door | opening | window
        public Vector3 center;
        public Vector3 normal;
        public float w;
        public float h;
        public string parentId;
        public string source;
        public string storyId;
        public float Area => w * h;
        public bool Placeable => kind == "wall";
    }

    public class SpaceRoom
    {
        public string roomId;
        public string label;
        public string kind;
        public string storyId;
        public readonly List<Vector2> polygon = new List<Vector2>();
        public readonly List<SpaceRect> rects = new List<SpaceRect>();
        public readonly List<SpaceWall> walls = new List<SpaceWall>();
        public Vector2 centroid;
        public string groundMode;
    }

    public static class SpaceGroundMode
    {
        public const string Architectural = "architectural";
        public const string Terrain = "terrain";
        public const string Flat = "flat";
        public static bool IsArchitectural(string mode) =>
            string.Equals(mode, Architectural, System.StringComparison.Ordinal);
        public static bool IsFlatFloor(string mode) =>
            IsArchitectural(mode) || string.Equals(mode, Flat, System.StringComparison.Ordinal);
        public static bool IsKnown(string mode) =>
            IsFlatFloor(mode) || string.Equals(mode, Terrain, System.StringComparison.Ordinal);
    }

    public class SpaceMapData
    {
        public const string Measured = "measured";
        public const string Nominal = "nominal";
        public Vector3 spawn;
        public readonly List<SpaceRoom> rooms = new List<SpaceRoom>();
        public readonly List<SpaceWall> roomPlanSurfaces = new List<SpaceWall>();
        public string confidence = "";
        public string sourceLabel = "";
    }
}
#endif
