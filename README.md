# Universal Mod Converter

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that moves [Penumbra](https://github.com/xivdev/Penumbra) mods to a different item, slot or race, keeping all of the mod's options and toggles intact. It also swaps idle and emote animations and retargets animations to other races.

> **Tip:** Use **Create a new mod** (the default) so your source mod is never touched, and report anything that looks wrong.

## What it does

- **Gear and facewear:** retarget a modded item to any other wearable item, including a different slot (for example body → hands). Weapons are not supported.
- **Hair, faces, tails and Viera ears:** retarget to another ID and/or another race, with the model reshaped for the target race. Tails and Viera ears can convert into each other.
- **Animations:** move an idle to another idle slot (optionally as a Penumbra option group with one option per slot), move an emote's animations to another emote, and retarget body animations to other races' skeletons.
- **Merge modpacks:** combine two modpacks — for example a base mod with materials and textures and a separate pack of upscaled models — into one new mod, with an explicit choice of which one wins where both change the same file.
- **Safe by default:** everything is previewed before anything is written, output is built in a staging folder and verified, and every conversion can be reverted.

## Installation

The plugin is distributed through the shared [DalamudPlugins](https://github.com/link-0402/DalamudPlugins) repository.

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
2. **From:** choose the item, hair, face, tail, ear or animation the mod replaces. Mods that touch several items list each one; nothing is guessed.
3. **To:**
   - Gear: choose the slot and the target item.
   - Hair, faces, tails and ears: choose the target race and one of the options players can pick for it, e.g. "Face 101 (Keeper of the Moon)".
4. **Add selection to conversion plan.** Repeat steps 2–4 for anything else to convert in the same pass.
5. **Output:** keep **Create a new mod** (recommended), choose **Add to this mod** to put the converted item beside the original, or **Convert in place** to replace the original.
6. **Review:** the plan is previewed automatically whenever it changes; there is nothing to click. The line above the button always says what is still missing.
   - **Plan** lists what to check, with problems first; **Advanced details** lists every change.
   - **Mesh groups** (gear) lets you leave parts of the model out, see [Changing slots](#changing-slots).
7. Click **Create new mod**, **Add to this mod** or **Convert in place**. The result is verified the way the game would load it, and the new or updated mod is loaded in Penumbra.

### The conversion plan

Nothing is converted until it is in the **conversion plan**, the card right under the item pickers. Set up a conversion in the From and To cards and click **Add selection to conversion plan**; the cards are then free for the next one, and changing them never affects what is already planned. Preview and Apply work on the plan only, so even a single conversion is confirmed by adding it. Each entry shows its source and target with their icons, and can be switched off or removed; **Clear the plan** at the top of the card removes them all.

Several entries are planned against the mod as it is on disk and merged into a single pass, so one apply produces one result and one entry in **History**, revertable as a whole. Two entries that would undo each other — the same item converted twice, the same game path claimed twice, the same file written by one and moved by another — cannot run together: the overlapping one is marked in the plan with the reason, and nothing is written until you remove one of them. Different slots of the same gear set are different items and convert together fine. A hair, face, tail or Viera-ear conversion has to be the plan's only entry.

The **Plan** tab lists what to check before applying; **Advanced details** adds the tables of game paths, metadata entries and file operations behind the plan.

### Skin and face textures

Skin textures (`chara/human/cXXXX/obj/body/…`) are detected like hair, faces, tails and Viera ears, and convert to other races the same way; skins and faces stay within their gender, because the bodies and faces are shaped differently. When a skin or face root in the mod holds **only textures**, the target card offers **Also for**: the same textures are written for further races too, all pointing at the one file — a texture has no paths inside it, so nothing has to differ between races. **Keep the original race too** leaves the source race with the textures instead of moving them. A root that also replaces a model or material cannot be shared this way, because those files name their race inside them; convert it to a single target instead.

### Facial expressions

A pose's face lives inside the same `.pap` as extra animations, one per face type, named after the body animation they play with. **Also attach a facial expression** (on a swap or retarget) or **Only add an expression** copies those facial animations from a donor into the converted animation, renames them after its body animation, and leaves every existing animation in the file byte for byte. The donor is either a game emote — read for each race from that race's own file, since every race animates a different face — or any `.pap` with a face in another installed Penumbra mod. A face type the animation already has keeps its own. Attaching changes the animation file itself, so on its own it needs **Create a new mod** or **Convert in place**.

### Changing slots

Converting to another slot only changes which item loads the model; the geometry stays as it was, so a skirt converted to a bracelet still contains the skirt, and its skin. The **Mesh groups** tab lists every model the output ships, with a **Keep** checkbox per mesh group. A group made of several parts has an arrow that opens it, so each part can be kept or removed on its own; removing a group's last part removes the group. Models with the same layout, usually the race versions of one model, are edited together.

To see what you are choosing, turn on **Show my choices on my character**: your character shows the model without the mesh groups and parts you untick, and is redrawn (a short blink) each time you change a checkbox. The preview only runs while you wear the original item with the source mod enabled; **Equip** puts the item on your character with Glamourer until you change gear. It uses a temporary Penumbra mod (priority 9999) with a copy of the source model, which goes away when you turn the preview off or leave the tab; nothing is written to your mods.

Mesh groups with a body material (skin, bibo, pubes or piercings) are switched off automatically whenever the model changes slots: they are body parts of the old slot. On an equipment slot you can tick them back on. On an accessory they stay off, because an accessory cannot load those materials and the game would not draw the model at all.

Removed parts are hidden in place, their triangles collapsed so nothing else in the file moves; removed groups are taken out of the model, and any material no remaining part uses is dropped with them.

### Merging modpacks

Some mods are split across modpacks: a base mod with the materials, textures and other models, and a second pack that only replaces some models with upscaled ones. Neither works right on its own. **Merge modpacks** (top right of the main window) opens a window where you choose the two modpacks and, explicitly, which of them wins when both change the same thing — there is no default. The merge is planned as soon as that is chosen, and lists everything both packs change before anything is written.

- Where both change the same game file or metadata entry, the winner's version is kept.
- Option groups with the same name and type are merged, options with the same name into one; everything else is added. The winner's groups are given a higher priority, so its options also win in Penumbra when options of both are on, and anything the winner sets by default is removed from the other pack's options, which would otherwise override it.
- Identical files are stored once. Two different files that happen to share a name inside the mod folders are both kept, one of them renamed; the game only sees game paths, so this changes nothing in game.
- The result is a new mod, built in a staging folder like any other and listed in **History**, from where it can be removed again. The two original modpacks are not changed; disable them in Penumbra once the merged mod is in use.

When a converted item uses a material or texture that exists neither in the mod nor in the game, the warning points to this: the file usually lives in a base modpack that should be merged in first.

## Safety and reverting

- **Nothing is written during preview.** Apply refuses to run if the source mod changed since the preview.
- **New mods** are built in a hidden staging folder next to the source, validated, and only then moved into place.
- **In-place conversions** build a full shadow copy, keep the original in a backup folder, and swap folders atomically. If Penumbra can't load the result, the original is restored automatically.
- **Adding to a mod** writes the same way, but nothing is moved or deleted: the converted paths are added next to the original's, in the same options, so the toggles the mod already has control both. Where a converted file needs different contents — a model or material with paths inside it — it gets its own copy and the original is left alone. The one exception is an **IMC option group**, which carries a single item identifier and so cannot drive two items; the converted item gets its own copy of that group, and the plan says so.
- **Reverting never deletes anything itself.** The reverted output is moved into that same backup folder.
- **Backups expire.** They are kept for 14 days, and the newest 10 are kept regardless of age; both limits are configurable in Settings, along with a "Clean up now" button and how much space backups currently use. A backup you could still revert to is never deleted, however old it is — once a backup does expire, the History tab says so instead of offering a revert that cannot work.
- **The backup folder is configurable** in Settings. By default it's the system temp folder, so the machine can reclaim the space too; when temp is on a different drive than the Penumbra mod directory it falls back to a hidden `.umc-backups` folder beside the mods, because publishing a conversion moves whole folders and a move cannot cross drives. Set a custom path to keep backups somewhere else instead.
- **A crash is cleaned up on the next start.** Leftover staging folders are removed, and a conversion interrupted between the two folder moves is rolled back to the original.
- **Status is reported in two parts:** writing the output to disk, and Penumbra loading it. An output Penumbra couldn't load is shown with its path and a retry button.

## How gear conversion works

Gear conversion works on the game paths a mod redirects, never on local file names. It follows the same chain the game does: a model loads its materials from `<root>/material/v<IMC material ID>/`, a material loads its textures, and a VFX loads its textures. Everything reached this way under the source item's root, and supplied by the mod, is moved to the target root. The references inside models, materials and effects are rewritten to match. Resources outside the source root, such as shared textures, keep their paths.

- **Variants:** the chosen source variant defines the look (its material folder, attributes, decal and VFX, including the mod's own IMC overrides). Every variant of the target model is redirected to it, so all items sharing the target model look complete.
- **Models:** only the models the mod ships are converted. A race the mod has no model for keeps seeing the target item's own model; no vanilla copy of the source item is added for it. Each race the output does ship a model for gets an EQDP entry saying so, in the same option as the model, because otherwise the game would load the race it falls back to (an item without a female model shows the male one).
- **Game files:** a material or effect the converted models need but the mod doesn't ship is copied from the game, so the target never references files that don't exist.
- **Unused materials:** a model converted to an accessory, or with parts removed in the Mesh groups tab, keeps only the materials its parts use. The game loads every material a model lists before drawing it, and a skin material cannot be found on an accessory slot at all.
- **Metadata:** EQDP, EQP, GMP, EST, IMC, Shp, Atr and GlobalEqp entries are retargeted, with EQDP bits moved between slots. Entries that make no sense on the target slot, such as body visibility flags on hands, are reported instead of being applied.
- **Options:** every group type is converted, including Combining and IMC groups. New mods keep only the converted item's data and drop groups that end up empty, unless another group references them.
- **In place:** resources used only by the converted item move to the target; resources other items still use are kept and duplicated for the target.
- **Paths** follow TexTools' rules: the `equipment/e####` / `accessory/a####` root, the item token and the slot suffix change together. MDL (v5 and v6) and MTRL string tables are edited structurally, including renames that change a material name's length.

## License

[AGPL-3.0-or-later](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for behavior references and attribution.
