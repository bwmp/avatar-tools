# Avatar Tools

Editor tools for building VRChat avatars in Unity.

## Install

1. Open the [listing page](https://bwmp.github.io/avatar-tools/) and click **Add to VCC**, or in the VRChat Creator Companion go to **Settings → Packages → Add Repository** and paste `https://bwmp.github.io/avatar-tools/index.json`.
2. Open your avatar project in the Creator Companion, choose **Manage Project**, and add **Avatar Tools**.

Requires Unity 2022.3 and the VRChat Avatars SDK 3.10.5 or newer.

## Tools

Everything is under **Tools → Avatar Tools** in Unity.

- **Animator:** Animation Clip Recorder, Animator Cleaner, Blendshape Slider Generator, Exclusive Toggle Group, Gesture Layer Builder, Material Swap Generator, Missing Animation Path Finder, Toggle Generator, Tracking & Layer Control Helper, Write Defaults Auditor
- **Dynamics:** Contact Generator, PhysBone Overview, PhysBone Preset Library
- **Expressions:** Animation Impact Report, Menu Tree Editor, Parameter Budget Analyzer
- **Optimization:** Material Batch Editor, Mesh Merger, Performance Rank Preview, Quest Conversion Helper, Texture Auditor, Unused Blendshape & Bone Remover
- **Setup:** Avatar Descriptor Quick Setup, Avatar Variant Maker, Blueprint ID Manager, Bone Name Remapper, Lighting Preview Rig, Outfit Merger, Prefab Attach Helper, Shape Key Sync, Upload Diff
- **Workflow:** Avatar Backup Snapshot, Batch Rename, Hierarchy Organizer, VRChat Account Switcher

**Avatar Presets** is a component (**Add Component → Avatar Tools → Avatar Presets**) that adds save/load preset slots to the avatar's expression menu. It is applied when you upload or enter Play mode and never modifies the avatar's own assets.

Generated assets, backups, and preset libraries are written to `Assets/AvatarTools` in your project.

## Releasing

Bump `version` in `Packages/dev.bwmp.avatartools/package.json`, push, then run the **Build Release** workflow. The listing rebuilds automatically.
