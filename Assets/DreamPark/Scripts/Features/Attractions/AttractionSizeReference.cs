using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// THE size-reference ladder for attraction footprints — the human-readable
    /// "fits in a ___-sized space" tag derived from a LevelTemplate's authored
    /// dimensions (feet). An attraction gets the SMALLEST reference space it
    /// fits inside (rotation allowed, 10% per-axis grace).
    ///
    /// MIRRORED THREE WAYS — keep all in lockstep (same ladder, same fit rule):
    ///   • DreamPark-Web  lib/attractionSizes.js            (server truth — stamps sizeTag on catalog docs)
    ///   • this file                                        (SDK: uploader payloads + inspector display)
    ///   • DreamPark-iOS  Creator/AttractionsModels.swift   (AttractionSizeReference — app display/sort/search)
    /// </summary>
    public static class AttractionSizeReference {
        public struct Reference {
            public string slug;    // stable API value (matches the backend sizeTag)
            public string label;   // display name
            public float widthFt;  // reference footprint, smaller side
            public float lengthFt; // reference footprint, larger side

            public Reference(string slug, string label, float widthFt, float lengthFt) {
                this.slug = slug;
                this.label = label;
                this.widthFt = widthFt;
                this.lengthFt = lengthFt;
            }
        }

        // Smallest → largest. Slugs are API values — never rename without a
        // migration across all three mirrors; labels are display-only.
        public static readonly Reference[] Ladder = new Reference[] {
            new Reference("phone_booth",      "Phone Booth",      4f,   4f),
            new Reference("elevator",         "Elevator",         6f,   8f),
            new Reference("bedroom",          "Bedroom",          11f,  12f),
            new Reference("parking_space",    "Parking Space",    9f,   18f),
            new Reference("living_room",      "Living Room",      15f,  20f),
            new Reference("two_car_garage",   "Two-Car Garage",   20f,  20f),
            new Reference("classroom",        "Classroom",        26f,  32f),
            new Reference("retail_store",     "Retail Store",     30f,  50f),
            new Reference("volleyball_court", "Volleyball Court", 30f,  60f),
            new Reference("tennis_court",     "Tennis Court",     36f,  78f),
            new Reference("basketball_court", "Basketball Court", 50f,  94f),
            new Reference("hockey_rink",      "Hockey Rink",      85f,  200f),
            new Reference("football_field",   "Football Field",   160f, 360f),
            new Reference("soccer_field",     "Soccer Field",     225f, 360f),
        };

        // 10% per-axis grace — mirrored in the JS/Swift ladders. The SDK's
        // stock sizes should land on friendly tags (Small 30×64 →
        // Volleyball Court, not Tennis Court).
        public const float FitTolerance = 1.10f;

        // Sanity ceiling (feet) — mirrored in the JS/Swift ladders.
        public const float MaxDimensionFt = 5000f;

        /// <summary>
        /// The smallest reference space a widthFt × lengthFt footprint fits
        /// inside (rotation allowed — (minor, major) vs (minor, major)).
        /// Larger than everything clamps to the largest tier; non-positive
        /// (or beyond-sanity) dims return null.
        /// </summary>
        public static Reference? Compute(float widthFt, float lengthFt) {
            if (!(widthFt > 0f) || !(lengthFt > 0f)) return null;
            if (widthFt > MaxDimensionFt || lengthFt > MaxDimensionFt) return null;
            float minor = Mathf.Min(widthFt, lengthFt);
            float major = Mathf.Max(widthFt, lengthFt);
            foreach (var reference in Ladder) {
                if (minor <= reference.widthFt * FitTolerance && major <= reference.lengthFt * FitTolerance) {
                    return reference;
                }
            }
            return Ladder[Ladder.Length - 1];
        }

        /// <summary>"50 × 94 ft — fits a Basketball Court" (inspector/log copy).</summary>
        public static string Describe(float widthFt, float lengthFt) {
            var reference = Compute(widthFt, lengthFt);
            if (reference == null) return "No dimensions";
            return $"{widthFt:0.#} × {lengthFt:0.#} ft — fits a {reference.Value.label}";
        }
    }
}
