# Universal Mod Converter

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that moves [Penumbra](https://github.com/xivdev/Penumbra) mods to a different item, slot or race, keeping the mod's options and toggles intact.

## What it does

- **Gear and facewear:** retarget an item to any other wearable item, including a different slot. Weapons are not supported.
- **Hair, faces, tails, Viera ears and skins:** retarget to another ID and/or race, reshaped for the target. Tails and Viera ears can convert into each other.
- **Retextures:** add a mod's textures (and face or skin materials) for other races and IDs at once (the other faces of a race, for example), either beside the original in its existing options or in new option groups per race. This covers skins, faces, and any hair, tail or ear mod that only replaces textures. A skin has no slots, so it only lists the races that have a skin of their own (Elezen and Miqo'te wear the Midlander one, for example).
- **Animations:** move an idle to another idle slot, move an emote's animations to another emote, move a facial expression to another expression, retarget body animations to other races, and attach a facial expression from the game's list or another mod.
- **Merge modpacks:** combine two modpacks into one new mod, with an explicit choice of which one wins where they overlap.
- **Batch conversions:** queue up several conversions and apply or revert them together as one result.
- **Safe by default:** everything is previewed before anything is written, and every conversion can be reverted.

## Installation

The plugin is distributed through my shared [DalamudPlugins](https://github.com/link-0402/DalamudPlugins) repository.

1. Open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add this URL and click **Save**:
   ```
   https://raw.githubusercontent.com/link-0402/DalamudPlugins/main/repo.json
   ```
3. Find **Universal Mod Converter** in `/xlplugins` and install it.

Penumbra must be installed. Without it the plugin still works on mod folders you enter by path, but new mods have to be added to Penumbra by hand.

## Using it

Open the window with `/umc` (settings: `/umcconfig` or the cog icon).

1. **Pick a mod** in the browser on the left, or enter a folder path under **Other folder**.
2. **From/To:** pick the source item, hair, face, tail, ear, skin or animation, and its target.
3. **Add selection to conversion plan.** Repeat for anything else to convert in the same pass.
4. **Output:** **Create a new mod** (recommended), **Add to this mod** to put the result beside the original, or **Convert in place** to replace it. 
A new mod holds only the converted items or animations; hair, face, tail, ear and skin conversions instead copy the whole mod with that part converted.

Retextures instead offer **Add paths on existing options** or **Create new groups for new paths**, optionally with **Create as a new mod** to write them to a copy of the whole mod; the original always keeps working.

An animation option group (a variant per slot) is added to this mod or created as a new mod, never converted in place.

An expression added to this mod gets an option group of its own, so the animation can still be played without it.

Hovering an option explains it, and the text under the selected one says what it does with what you planned.

6. **Review:** the plan previews automatically.**Mesh groups** lets you leave parts of a gear model out, for every gear conversion in the plan, with a live preview on your character (Glamourer can put the original item on for you). Body parts of a model that changes slots start switched off.
7. Click **Create new mod**, **Add to this mod** or **Convert in place** based on the selection above. The result is verified and loaded in Penumbra.

Nothing converts until it's added to the **conversion plan**; Preview and Apply both work on the whole plan, so one apply is one revertable entry in **History**.
**Merge modpacks** (top right) is a separate flow for combining two modpacks, such as separate mods containing only textures and only models. I'd recommend using this before attempting to swap such mods.
During gear conversion you can tick off parts of the mesh you'd like to remove. When converting to a different slot, body parts will automatically be removed. Note that this cannot automatically add body meshes for the new output slot.

## Safety and reverting

- Nothing is written during preview, and new mods are validated in a staging folder before being moved into place.
- Converting in place or adding to a mod keeps a backup and swaps in atomically; a failed load restores the original automatically. Merges always create a new mod, so they leave both modpacks untouched.
- Reverting never deletes anything — it moves the output into a backup folder. Backups expire after a configurable age/count (never one you could still revert to), and a crash is cleaned up on the next start whether or not old backups are deleted automatically.

## License

[AGPL-3.0-or-later](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for behavior references and attribution.
