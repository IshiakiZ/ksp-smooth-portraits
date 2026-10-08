# How it works

The game draws each portrait with a camera of its own pointed at the kerbal in their seat, and lets
each of those cameras take a picture only every 0.10 to 0.15 seconds: seven to ten pictures a second
however fast the game runs, which is why the faces jerk. This mod has the pictures taken as often as
the screen is redrawn (up to a limit you can set) and tells the game's own timers to stand back.

Nothing about a picture is changed. Each is taken by the game's own code with the same camera at the
same size; only how often changes. Turn the mod off in its settings and the game's timers take over
again within two seconds.
