# Universal Mod Converter

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that moves [Penumbra](https://github.com/xivdev/Penumbra) mods to a different item, slot or race, keeping the mod's options and toggles intact.

> **Tip:** Use **Create a new mod** (the default) so your source mod is never touched, and report anything that looks wrong.

## What it does

- **Gear and facewear:** retarget an item to any other wearable item, including a different slot. Weapons are not supported.
- **Hair, faces, tails, Viera ears and skins:** retarget to another ID and/or race, reshaped for the target. Tails and Viera ears can convert into each other; texture-only roots can fan out to several races at once.
- **Animations:** move an idle to another idle slot, move an emote's animations to another emote, retarget body animations to other races, and attach a donor's facial expression.
- **Merge modpacks:** combine two modpacks into one new mod, with an explicit choice of which one wins where they overlap.
- **Batch conversions:** queue up several conversions and apply or revert them together as one result.
- **Safe by default:** everything is previewed before anything is written, and every conversion can be reverted.

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
2. **From/To:** pick the source item, hair, face, tail, ear, skin or animation, and its target.
3. **Add selection to conversion plan.** Repeat for anything else to convert in the same pass.
4. **Output:** **Create a new mod** (recommended), **Add to this mod** to put the result beside the original, or **Convert in place** to replace it.
5. **Review:** the plan previews automatically. **Mesh groups** lets you leave parts of a gear model out, with a live preview on your character via Glamourer.
6. Click **Create new mod**, **Add to this mod** or **Convert in place**. The result is verified and loaded in Penumbra.

Nothing converts until it's added to the **conversion plan**; Preview and Apply both work on the whole plan, so one apply is one revertable entry in **History**. **Merge modpacks** (top right) is a separate flow for combining two modpacks.

## Safety and reverting

- Nothing is written during preview, and new mods are validated in a staging folder before being moved into place.
- In-place conversions and merges keep a backup and swap in atomically; a failed load restores the original automatically.
- Reverting never deletes anything — it moves the output into a backup folder. Backups expire after a configurable age/count (never one you could still revert to), and a crash is cleaned up on the next start.

## License

[AGPL-3.0-or-later](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for behavior references and attribution.
