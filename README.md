# Smooth Portraits

The crew's portraits at the bottom right of the flight screen, drawn as often as the game itself. The game redraws
them only seven to ten times a second, which is why the faces jerk. From 0.2.0 their edges are smooth too, and the crew's
heads move with the ride (each can be switched off).

![The crew's portraits at lift-off: the game's own on the left, with Smooth Portraits on the right](src/SmoothPortraits/img/smooth-portraits.gif)

For Kerbal Space Program 1.12.x. **Needs [Keystone](https://github.com/IshiakiZ/ksp-keystone)**, a separate download
([on SpaceDock](https://spacedock.info/mod/4665/Keystone)): this mod does not load without it.

## Install

Copy the `SmoothPortraits` folder from the download into your KSP's `GameData`, beside `Keystone`. It is on as
installed.

## Settings

In the Keystone window (the keystone button on the game's toolbar, or Alt-K; Option-K on a Mac): the most pictures a
second (60 as installed), the most of each frame's time the portraits may take (3 %), whether kerbals outside
the ship are included, and **Smooth edges** (0.2.0, on as installed): the portraits drawn with four samples a point,
and a kerbal outside the ship in full colour where the game draws sixteen-bit colour, which bands. **Shaken by the ride**
(0.2.0, on): the crew's heads move with the g and the rumble, as in film from real cockpits.

## Good to know

* It costs about 0.2 ms a frame for one portrait at 60 pictures a second (measured again on 2026-10-18 with Keystone's meter:
  0.23-0.24 ms of the main thread, the whole frame 0.5-0.9 ms longer), and never more than the share you set:
  where that does not pay for every portrait in every frame, the portraits take turns.

## Works on

Mac: tried. Windows 11: 0.1.2 tried; what 0.2.0 adds, not yet. Linux: never run (it is plain C#, with nothing of one
system's in it).

**More:** [the long description](https://github.com/IshiakiZ/ksp-smooth-portraits/wiki/The-long-description) (everything in full, and
what was tried on each system) and the rest of the [wiki](https://github.com/IshiakiZ/ksp-smooth-portraits/wiki).

## Licence

[CC BY 4.0](LICENSE): use it, change it and pass it on as you like, as long as you credit Ishiaki and link back here.
