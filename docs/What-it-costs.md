# What it costs

Measured on an Apple M5 Pro at 1280 x 720 on 2026-10-07 (with Scatterer, EVE, TUFX and Waterfall
installed):

* A picture of a kerbal in a seat takes the game's own thread **0.2 to 0.25 ms**, and that is what it
  costs the frame. With one portrait showing, 60 pictures a second and the game running at about 80
  frames a second, the frame time over five rounds was 12.67 to 12.94 ms with the mod (and 14.3 once)
  against 12.50 to 12.95 ms with the game drawing the portrait its own way: **about 0.2 ms a frame, 1.5%**.
  With two portraits at 59 pictures a second each the game stayed at the 60 frames a second the display
  was then holding it to.
* The mod **holds itself to a share of every frame**, three hundredths as installed (`Share of a
  frame`), so that is the most it can cost however many portraits are showing. It times every picture
  it takes. Where the share does not pay for every portrait in every frame the portraits take turns,
  evenly: each gets as many pictures as can be afforded, and no single frame pays for more than its
  share. At 3% and 80 frames a second that is one portrait at 60 pictures a second, or three at about
  30 each; the game gives each 7 to 10.
* It is only that cheap because of *when* the pictures are taken: after the frame is finished. Taken in
  the middle of a frame, as the game takes its own, the first camera to draw anything has to wait while
  the whole scene is made ready for drawing, and the same picture then appears to take 3.7 ms. (That is
  work the frame does anyway a moment later, so the game's own pictures do not really cost that either;
  but a mod counting what it spends would see 3.7 ms and hold itself back for nothing.)
* **A kerbal outside the ship is dearer**: their portrait is the whole scene drawn again from their
  helmet, five cameras' worth, 1.7 ms a picture on the launch pad. With a share of 3% that portrait got
  19 pictures a second where the game gives it 8. `Kerbals outside too` leaves those to the game.

Frame by frame (a recording locked to 60 frames a second, two kerbals in a Kerbal X on the pad): with
the mod the picture in each portrait changed in 84 and 88 of 89 frames; drawn by the game, in 9 and 11.
