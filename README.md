# MeshLoaderTweak

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that modifies the way that Resonite schedules asset uploads.
By allowing multiple meshes to process in parallel, and shifting particularly heavyweight meshes to a dedicated queue,
it helps avoid situations where one particularly heavyweight mesh plugs up the queue and breaks things like the context menu.

**WARNING**: This mod will probably break as The Splittening(tm) proceeds! Use at your own risk.

## Installation

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Place [MeshLoaderTweak.dll](https://github.com/bdunderscore/MeshLoaderTweak/releases/latest/download/MeshLoaderTweak.dll) into your `rml_mods` folder. This folder should be at `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods` for a default install. You can create it if it's missing, or if you launch the game once with ResoniteModLoader installed it will create the folder for you.

## Credits

- [hazre's VRCFTReceiver](https://github.com/hazre/VRCFTReceiver/), which I used as a starting point for this mod
