# Third-Party Components

The DreamPark SDK bundles or depends on the third-party components listed below. Each is governed by its own license, which is **not** superseded by the DreamPark SDK License (see `LICENSE`).

The bundled copies are included for convenience so creators can build and test the SDK without separately installing each dependency. Where a component ships its license alongside its source (e.g. `Assets/DreamPark/ThirdParty/JSON/LICENSE.md`), that file controls. For components without a bundled license file, refer to the upstream project for current terms.

| Component | Path | Upstream | License |
|---|---|---|---|
| Defective.JSON | `Assets/DreamPark/ThirdParty/JSON/` | https://github.com/mtschoen/JSONObject | See bundled `LICENSE.md` |
| LiteNetLib | `Assets/DreamPark/ThirdParty/LiteNetLib/` | https://github.com/RevenantX/LiteNetLib | MIT |
| XLua | `Assets/DreamPark/ThirdParty/XLua/` | https://github.com/Tencent/xLua | MIT |
| GLTFast | `Assets/DreamPark/ThirdParty/GLTFast/` | https://github.com/atteneder/glTFast | Apache-2.0 |
| LibTessDotNet | `Assets/DreamPark/ThirdParty/LibTessDotNet/` | https://github.com/speps/LibTessDotNet | SGI-B-2.0 |
| TextMesh Pro | `Assets/DreamPark/ThirdParty/TextMesh Pro/` | Unity Technologies (bundled with Unity) | Unity Companion License |
| DinoFracture | `Assets/DreamPark/ThirdParty/Fracture/` | https://assetstore.unity.com/packages/tools/level-design/dinofracture-a-dynamic-fracture-library-22979 | Unity Asset Store EULA (purchased license required for redistribution) |
| vInspector | `Assets/DreamPark/ThirdParty/vInspector/` | https://assetstore.unity.com/packages/tools/utilities/vinspector-2-262130 | Unity Asset Store EULA |
| vTabs | `Assets/DreamPark/ThirdParty/vTabs/` | https://assetstore.unity.com/packages/tools/utilities/vtabs-2-275864 | Unity Asset Store EULA |

## Important notes

**Unity Asset Store components.** DinoFracture, vInspector, and vTabs were purchased from the Unity Asset Store. Asset Store EULA terms generally permit use within a single Unity project but **restrict redistribution** of the asset itself. Confirm with the original publisher (or your Asset Store license seat) that bundling these in a public source-available SDK is permitted under your seat. If not, they should be removed from this repository and creators directed to install them separately from the Asset Store.

**TextMesh Pro.** Bundled with Unity itself; covered under your Unity license. Whether the bundled copy in this repo can be redistributed depends on Unity's Companion License terms — consider replacing the bundled copy with a Unity Package Manager dependency reference instead.

**XLua and LiteNetLib.** Both MIT-licensed; redistribution permitted as long as the upstream copyright notice is preserved. We have not added explicit upstream copyright notice files to these folders; consider doing so to be tidy.

**GLTFast.** Apache-2.0; redistribution permitted with notice. Consider including the upstream Apache-2.0 LICENSE file in `Assets/DreamPark/ThirdParty/GLTFast/`.

---

This file enumerates third-party components for transparency and license-compliance purposes. It is not legal advice. Confirm with your legal counsel that the SDK's distribution model is compatible with each upstream component's license terms before publishing.
