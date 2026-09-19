# Third-party behavior references

This project is licensed under the GNU Affero General Public License v3.0 or later. See `LICENSE`.

No source files from the projects below are bundled wholesale. The conversion behavior and compatibility rules were independently implemented with reference to:

- TexTools UI `CopyModelDialog.xaml.cs`, whose “Copy model/material to” action calls the framework model-copy pipeline. Repository revision: `6f4ababa2fc9a1f71c19f86296b92e0a3cc75214`. Upstream license: GPL-3.0. https://github.com/TexTools/FFXIV_TexTools_UI
- TexTools `xivModdingFramework`, especially `RootCloner.cs`, `ModelModifiers.cs`, `Mtrl.cs`, `PDB.cs`, and `Mdl.cs`, including root-aware dependency paths, material string rebuilding, PBD race-conversion traversal, weighted deformation behavior, and MDL v6 layout. Repository revision: `a6e8f0ddf76d07e70e365b42764e86450342a4a4`. Upstream license: GPL-3.0. https://github.com/TexTools/xivModdingFramework
- Yet-Another-Addon `xivpy/model`, used as a secondary MDL v6 layout and round-trip behavior reference. Repository revision: `a5bc0cc9d71812f57d8bbdbe09756b6e24ad5a13`. Upstream license: GPL-3.0. https://github.com/link-0402/Yet-Another-Addon
- Penumbra item-swap behavior, especially `EquipmentSwap.cs` and `CustomizationSwap.cs`. Repository revision: `a9e1889b1b5f2cd5f16925830f5fac9bab7e5927` on the `testing` branch. Upstream license: AGPL-3.0-or-later. https://github.com/xivdev/Penumbra
- Xande `SklbFile.cs`, used as an SKLB 0x3132/0x3133 container-layout reference. Repository revision: `172423c87135f696be7a5f63672c594c84778286`. Upstream license: AGPL-3.0-or-later. https://github.com/xivdev/Xande
- XIV Instant Edit's animation editor, from which the PAP envelope and timeline motion-name editing, idle-slot families, Havok load/save/sampling/interleaved-build code and rest-relative bone retargeting were adapted (and reviewed/changed: see the README's Animations section). Upstream license: GPL-3.0-or-later. https://github.com/link-0402/XIV-Instant-Edit
- VFXEditor, the original reference for the PAP layout (`Formats/PapFormat`), timeline entry layouts (`Formats/TmbFormat`), Havok load/save and interleaved/spline animation construction (`Interop/Havok`), skeleton-mapper layout and emote path conventions, as adapted by XIV Instant Edit. Revision `cebfba38a0b09ef5318f1a86e90c6d96d41a2717` of the link-0402 fork. Upstream license: GPL-3.0. https://github.com/0ceal0t/Dalamud-VFXEditor
- FFXIVClientStructs Havok declarations, consumed through the Dalamud SDK to copy loaded skeleton bone names and parent indices into managed data, and to sample, build and serialize animations. Repository revision: `d8633414de71407f9eb45da830472e6e0fe26a08`. Upstream license: MIT. https://github.com/aers/FFXIVClientStructs

Final Fantasy XIV and its data formats are property of Square Enix. This project is not affiliated with or endorsed by Square Enix.
