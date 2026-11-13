#!/usr/bin/env python3
"""Check built asset bundles for objects whose saved data doesn't match their own layout.

Unity reads such an object past the end of its data and dies with a fatal
CachedReader::OutOfBoundsError, with no way to catch it. The usual cause is a serialized field
behind a platform #if: the bundle's layout comes from the Editor, its data from the player compile.

    python3 -m pip install --target /tmp/unitypy UnityPy
    PYTHONPATH=/tmp/unitypy python3 check_bundle_layouts.py ServerData/iOS

Exit code: 0 = every object parsed, 1 = broken objects found, 2 = nothing checked.
"""
import glob
import os
import sys
import warnings

warnings.filterwarnings("ignore")
import UnityPy  # noqa: E402

# Stripped bundles don't record their Unity version; this only picks the parser's format.
UnityPy.config.FALLBACK_UNITY_VERSION = "6000.3.23f1"
HEADER = {"m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
          "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier"}


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else "."
    bundles = sorted(glob.glob(os.path.join(folder, "**", "*.bundle"), recursive=True))
    checked, no_layout, broken = 0, 0, []
    for path in bundles:
        for obj in UnityPy.load(path).objects:
            if obj.type.name != "MonoBehaviour":
                continue
            node = obj.serialized_type.node if obj.serialized_type else None
            if node is None:
                no_layout += 1
                continue
            checked += 1
            try:
                obj.parse_as_dict()
            except Exception as err:
                fields = [c.m_Name for c in node.m_Children if c.m_Name not in HEADER]
                broken.append((os.path.basename(path), obj.path_id, obj.byte_size, fields[:8], str(err)[:60]))
    print(f"{len(bundles)} bundle(s), {checked} scripted object(s) checked, {no_layout} with no layout to check")
    for name, path_id, size, fields, err in broken:
        print(f"BROKEN {name}\n  object {path_id}, {size} bytes: {err}\n  fields: {', '.join(fields)}")
    print(f"{len(broken)} broken")
    if broken:
        return 1
    return 0 if checked else 2


if __name__ == "__main__":
    sys.exit(main())
