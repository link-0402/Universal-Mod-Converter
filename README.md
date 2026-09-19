# Advanced Penumbra Mod Converter

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that moves [Penumbra](https://github.com/xivdev/Penumbra) mods to a different item, slot or race, keeping all of the mod's options and toggles intact.

> **Testing build.** The plugin is published as a testing-only plugin and is under active development. Use **Create a new mod** (the default) so your source mod is never touched, and report anything that looks wrong.

## What it does

- **Gear and facewear:** retarget a modded item to any other wearable item, including a different slot (for example body → hands). Weapons are not supported.
- **Hair, faces, tails and Viera ears:** retarget to another ID and/or another race, with the model reshaped for the target race. Tails and Viera ears can convert into each other.
- **Safe by default:** everything is previewed before anything is written, output is built in a staging folder and verified, and every conversion can be reverted.

## Installation

The plugin is distributed through the shared [DalamudPlugins](https://github.com/link-0402/DalamudPlugins) repository.

1. Open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add this URL and click **Save**:
   ```
   https://raw.githubusercontent.com/link-0402/DalamudPlugins/main/repo.json
   ```
3. On the same tab, enable **Get plugin testing builds**.
4. Find **Advanced Penumbra Mod Converter** in `/xlplugins` and install it.

Penumbra must be installed. Without it the plugin still works on mod folders you enter by path, but new mods have to be added to Penumbra by hand.

## Using it

Open the window with `/apmc` (settings: `/apmcconfig` or the cog icon).

1. **Pick a mod** in the browser on the left, or enter a folder path under **Other folder**.
2. **From:** choose the item, hair, face, tail or ear the mod replaces. Mods that touch several items list each one; nothing is guessed.
3. **To:**
   - Gear: choose the slot and the target item.
   - Hair, faces, tails and ears: choose the target race and one of the options players can pick for it, e.g. "Face 101 (Keeper of the Moon)".
4. **Output:** keep **Create a new mod** (recommended), or choose **Convert in place** to edit the mod itself.
5. **Review:** the conversion previews automatically once the inputs are complete. The line above the buttons always says what is still missing.
   - **Plan** lists every change, with blockers and warnings first.
   - **Mesh groups** (gear) lets you leave parts of the model out, see [Changing slots](#changing-slots).
6. Click **Create new mod** or **Convert in place**. The result is verified the way the game would load it, and the new or updated mod is loaded in Penumbra.

Changed your mind? Click **Revert** on the result, or in the **History** tab. Reverting a new mod removes it from Penumbra; reverting an in-place conversion restores the original mod.

Scanning, previewing, converting and reverting all run in the background, so the game keeps running while large models are processed.

## Changing slots

Converting between slots changes which item loads the model. **It does not change the model itself:** a body model converted to hands still contains the whole body mesh and is drawn whenever the hands item is worn.

After the preview, the **Mesh groups** tab lists every model the new item will use, with each mesh group's material, triangle count, parts and attributes. Skin meshes are marked. Untick the groups that don't belong on the new slot. Race versions of the same model are grouped and edited together.

Every model keeps at least one mesh group. MDL v5 models, and files an in-place conversion shares with other items, can't be edited.

## Hair, faces, tails and ears

- **Only player options are offered.** The options come from the game's character creation data, not hard-coded ranges, so NPC-only faces, hairs and tails are excluded. Unlockable hairstyles are included. When only one clan can pick an option, the clan is named.
- **Race rules:** Lalafell convert only to other Lalafell, and faces stay within the same gender. These rules are also enforced when planning, not just in the UI.
- **Cross-race conversions** reshape the model along the game's race tree (`human.pbd`), one step at a time, matching TexTools' conversion order. A bone that can't be resolved is left unchanged and reported instead of blocking the conversion. Racial reshaping requires MDL v6; same-race conversions also work with MDL v5.
- **Shared materials** are resolved the way the game loads them. For example, hairs 101–200 share the Midlander materials, and Hrothgar tails share the `t0001` root. Shared materials are copied, never moved or overwritten, so other races keep working.
- **Extra skeletons** (EST / physics bones) of hair and faces are carried over explicitly for the target. A skeleton that doesn't exist for the target race is reported.
- **Tails ↔ Viera ears** rename the part and material layout. Real tails use tail bones that Viera don't have; this is reported.

## Safety and reverting

- **Nothing is written during preview.** Apply refuses to run if the source mod changed since the preview.
- **New mods** are built in a hidden staging folder next to the source, validated, and only then moved into place.
- **In-place conversions** build a full shadow copy, keep the original in a hidden `.apmc-backups` folder inside the Penumbra mod directory, and swap folders atomically. If Penumbra can't load the result, the original is restored automatically.
- **Reverting never deletes anything.** The reverted output is moved into `.apmc-backups`. Delete that folder yourself to reclaim space.
- **Status is reported in two parts:** writing the output to disk, and Penumbra loading it. An output Penumbra couldn't load is shown with its path and a retry button.

## How gear conversion works

Gear conversion works on the game paths a mod redirects, never on local file names. It follows the same chain the game does: a model loads its materials from `<root>/material/v<IMC material ID>/`, a material loads its textures, and a VFX loads its textures. Everything reached this way under the source item's root, and supplied by the mod, is moved to the target root. The references inside models, materials and effects are rewritten to match. Resources outside the source root, such as shared textures, keep their paths.

- **Variants:** the chosen source variant defines the look (its material folder, attributes, decal and VFX, including the mod's own IMC overrides). Every variant of the target model is redirected to it, so all items sharing the target model look complete.
- **Game files:** if the mod doesn't replace a model for some race, or a material the converted models need, the game's own file is copied into the output. The target never references files that don't exist.
- **Metadata:** EQDP, EQP, GMP, EST, IMC, Shp, Atr and GlobalEqp entries are retargeted, with EQDP bits moved between slots. Entries that make no sense on the target slot, such as body visibility flags on hands, are reported instead of being applied.
- **Options:** every group type is converted, including Combining and IMC groups. New mods keep only the converted item's data and drop groups that end up empty, unless another group references them.
- **In place:** resources used only by the converted item move to the target; resources other items still use are kept and duplicated for the target.
- **Paths** follow TexTools' rules: the `equipment/e####` / `accessory/a####` root, the item token and the slot suffix change together. MDL (v5 and v6) and MTRL string tables are edited structurally, including renames that change a material name's length.

## License

[AGPL-3.0-or-later](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for behavior references and attribution.
