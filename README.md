# Great Heist Auto

A realistic third person arena shooter in a procedurally built city, made with Unity 6.5 (URP) and the Local Asset
Generation Pipeline (LAGP v1.3, `C:\AI`) to show what that pipeline can do. Every model, texture, sound, music track and the
menu video comes from LAGP, generated on the laptop.

## Play

Open the project in Unity 6000.5.10f1 and run **Tools > Great Heist Auto > Build Everything** once: the repository holds the art
(`Assets/Game/Art`) and the code, and that builds the city, the scenes, the characters' animators and prefabs from them
(`Assets/Game/Generated` and `Assets/Game/Scenes`, about 2.8 GB, are not in the repository). Then press Play in
`Assets/Game/Scenes/MainMenu.unity`, or build the player.

| Control | On foot | In a car |
|---|---|---|
| WASD, mouse | Move, look | Drive, look around |
| Shift / Ctrl or C / Space | Run (walking without it) / crouch / jump | Space: brake; with steering at speed, drift |
| Left mouse / right mouse | Fire / aim (zooms, walking pace only) | |
| R, 1-4, 0 or wheel | Reload, choose a gun; 0 or scrolling past the last puts it away (you start unarmed) | |
| Middle mouse held, standing still | Swing the camera around the player | |
| E | Get in a car when close | Get out, below 25 km/h |
| Tab / L | | Horn / headlights (automatic at night) |
| Esc | Pause | Pause |

The city starts calm: armed men walk around with their guns put away, among pedestrians. Attack any of the armed men, by bullet,
car or explosion, and all of them turn hostile. A dead enemy reappears at its home spot a few seconds later, and an exploded car
returns to its parking spot. Attack a pedestrian and that one comes after you with fists. Dying respawns you at the starting point
and calms the whole city. Calm drivers stop for people, and all traffic slows at intersections and zebra crossings.

Rules: a head shot kills, a body shot takes 25 % of the health, being hit by a moving car kills (a slow nudge, under 18 km/h, only knocks you or a pedestrian over). Cars catch fire when shot, burn down
and explode with area damage, which can chain to other cars. Enemies drive cars too, and may try to run you over.

## Animation lab

The **Animation Lab** button on the main menu (or `Assets/Game/Scenes/AnimLab.unity` in the Editor) opens a test bench: any character or all six,
gait and direction on a floor that scrolls at the walking speed, crouch, air, driving, each gun with each hold, the actions, every single clip with
nothing on top, close-up views, slow motion and frame stepping. The top bar shows the clips playing, their weights and the palm direction. Drag to orbit, wheel to zoom.
`tools/verify_anim.py` plays through it by itself.

## How it is built

- **Models:** 1 player, 3 enemies and 2 pedestrians (tinted at runtime: the shader tints low-saturation texels, so clothes change
  colour and skin does not), 2 pistols, 2 rifles, 5 cars (each with a cheaper distant version: a sedan, an SUV, and three real Eastern Bloc cars,
  Dacia 1310, Oltcit Club and Trabant 601, generated from photos of the real cars), and 7 props.
- **Cars:** each generated car is turned to face +Z and levelled on its four tyres when it is baked (`BakeCarMesh`, pitch
  and roll in the build log), then scaled to its real size. Arcade physics on four suspension rays (`Vehicle`). Parked ones are kinematic and cost almost nothing. The game is played in
  third person only, on foot and driving.
- **Day and night:** `DayNightCycle` (3 + 3 minutes) drives the sky shader, sun and moon, ambient light, lens flare and every window; only
  the nearest lamps are real lights (`StreetLights`), the rest are drawn glows. `WeatherSystem` brings random fog, rain and lightning.
- **Film look:** `CinematicFx` drives motion blur from speed, depth of field while aiming, and reacts to damage.
- **Clips:** each character's clips come from LAGP's Animation Library (Mixamo first). `clip_sets` in `tools/assets.json` maps
  every game role (Idle, Walk, Jog, Death ...) to a library clip, men and women have their own locomotion; `gen_models.py`
  rigs with those clips into `final_<rig_version>`, and Unity imports each clip under its role name (`<model>.clips.json`,
  which also says which clips loop and which are pinned in place: `_flags` in `clip_sets`).
- **What the clips do:** gun out, the legs use eight-way Mixamo clips (walk, run, sprint and crouch-walk in every direction, from
  the Pro Rifle pack); stopping to crouch with a gun drops to one knee and rises from it. Deaths depend on the killing shot: from the
  front, behind or the right, head shots, crouched. Enemies sidestep or roll out of fire and throw grenades from cover at where they
  last saw you. Pedestrians stop to look around or talk with their hands (turned to anyone standing close), some stagger drunk at
  night, and anyone hurt limps from then on; so does the player below half health.
- **Animation:** the Animator plays the LAGP clips on Generic rigs for the body. Aiming is procedural (`CharacterRig`): spine turn,
  and two-bone IK putting both hands on the gun, so pistols and rifles share one system; with the gun away the legs twist towards the
  direction of travel.
- **City:** `CityBuilder` generates everything from a seed: 452 x 452 m of playable streets (about 200 000 m2, skyline beyond),
  rectangular buildings with tiled facade textures, procedural hedges, and props. All static art is merged per chunk with its tint
  in the vertex colours, so hundreds of tinted objects cost one draw call per material per chunk.
- **Roads:** `RoadNetwork` holds nodes, right-hand-traffic lanes and turn connectors, ready for cars (`GetNearestLane`, then follow
  `Lane.Points` and pick from `NextConnectors`). Parked cars and barriers stay out of the lanes.
- **Enemies:** `EnemyAI` uses the baked NavMesh and generated cover points: it takes cover that really blocks the line to the
  player, peeks, fires bursts, dodges or rolls when bullets pass close or the crosshair is on it, relocates when flanked, and hunts
  when it loses sight of you. `PedestrianAI` strolls, runs from gunfire, and punches if attacked.

## Scripts

Everything repeatable is a script in `tools/` ending in one `OK:` or `FAIL:` line. The scripts are not in the repository: they drive
LAGP on the author's machine (generating, importing and levelling the art, which is committed as it came out). Run the Python ones with LAGP's Python:
`C:\AI\ComfyUI\python_embeded\python.exe -s tools\<script>`.

| Script | What it does | Example |
|---|---|---|
| `fetch_refs.py` | Photos of real objects (a prop's `refs` in `assets.json`) from Wikimedia Commons, CC0 / public domain / CC BY only, with credits | `fetch_refs.py --only trabant` |
| `gen_concepts.py` | Concept images for all 3D assets (`assets.json`); a prop with `refs` is drawn by FLUX.3 from its photos | `gen_concepts.py --only car` |
| `gen_models.py` | Concept to shape, clean, texture, rig or FBX; resumable. Characters: a broken shape (more than 3 pieces after clean) is retried with the next seed, `eyefix` projects an eye photo onto dark blob eyes (`C:\AI\tools\eyefix`), `variant` remakes into new folders | `gen_models.py --only car` |
| `gen_textures.py` | Tileable textures, sky, logo, menu keyframe, effect sprites | `gen_textures.py --only hedge` |
| `gen_audio.py` | Sound effects, ambience loop, music loops | `gen_audio.py --only reload` |
| `gen_video.py` | Menu background video from the keyframe | `gen_video.py --seconds 10` |
| `contact_sheet.py` | Contact sheet of generated images, for picking | `contact_sheet.py C:\AI\outputs\downtown_blast\textures` |
| `import_assets.py` | Copies the picks (`asset_picks.json`) into `Assets/Game/Art`, makes normal maps | `import_assets.py` |
| `level_audio.py` | Brings every sound to the same loudness and verifies it (run by `import_assets.py`) | `level_audio.py --check-only` |
| `tile_preview.py` | Shows textures repeated 2 x 2 to check seams | `tile_preview.py out.jpg a.png b.png` |
| `library_sheets.py` | Contact sheets of LAGP Animation Library clips side by side, for choosing clips (`Build/clip_sheets/`) | `library_sheets.py --group walk:LocomotionWalking,ShooterWalking` |
| `clip_report.py` | Length, ground pace and tremor of every character clip | `clip_report.py --model player` |
| `verify_anim.py` | Runs the animation lab by itself: every combination measured and photographed, contact sheets in `Build/verify_anim/` | `verify_anim.py --max-tremor 6` |
| `unity_run.py` | Helper for the two scripts that need Unity: tells whether the project is open in an Editor and runs C# inside it | `from unity_run import eval_in_editor` |
| `check_blur.py` | From the game test's pictures: is the blur on the surroundings and not on the car | `check_blur.py --car 0.7` |
| `make_engine_hum.py` | Synthesises the engine sound as an exact loop (the one sound not from LAGP; run by `import_assets.py`) | `make_engine_hum.py --base 48` |
| `make_derived.py` | Window masks for night lights, soft smoke, red blood (run by `import_assets.py`) | `make_engine_hum.py` | Synthesises the engine sound as an exact loop (the one sound not from LAGP; run by `import_assets.py`) | `make_engine_hum.py --base 48` |
| `make_derived.py` |
| `compile_check.py` | Compiles the scripts in batch mode, lists errors | `level_audio.py` | Brings every sound to the same loudness and verifies it (run by `import_assets.py`) | `level_audio.py --check-only` |
| `tile_preview.py` | Shows textures repeated 2 x 2 to check seams | `tile_preview.py out.jpg a.png b.png` |
| `compile_check.py` |
| `build_game.py` | Generates all content and the Windows player | `build_game.py` (`--skip-content` for code changes only) |
| `verify_game.py` | Plays the built game with a bot, checks the rules, takes screenshots | `verify_game.py --seconds 150` |

Full rebuild from generated assets: `import_assets.py`, then `build_game.py`, then `verify_game.py`.

Gameplay video, played by a bot, with the game's own sound: `python tools/record_gameplay.py --seconds 120` writes `Build/gameplay.mp4` and a sheet of frames to check.
Continuous tyre screech from the screech clips: `python tools/make_skid_loop.py` writes `Assets/Game/Art/Audio/skid_loop_0.wav`.

Time the loading screen and its longest freeze: `python tools/check_loading.py` writes `Build/loading_check/` and lists every stall over 0.1 s with its phase, and how long the city's Awake, Start and first frames took. The city's heavy parts (merged meshes, trees, parked cars) are switched on in segments behind the loading screen (`SegmentLoader`), nearest the player first.

Smaller copy of a video for sending: `python tools/shrink_video.py Build/gameplay.mp4 --mb 28`.
In the Editor: **Tools > Great Heist Auto > Build Everything**. `Assets/Game/Generated` and `Assets/Game/Scenes` are regenerated
by it, so change the builders rather than those files.

## Trees

Trees are one generated model (`tree`, 30,000 triangles, 4096 texture, with `tree_lod` at 5,000 beyond about 70 m). `CityBuilder` straightens it, removes the one branch that sticks out of its crown and smooths its lighting; every tree is an object of its own sharing that mesh.

## Audio levels

Every sound file sits at -21 LUFS (+/- 0.5, checked by `level_audio.py`): one-shots measured on their loudest 400 ms, loops on
integrated loudness. The mix between kinds of sound is set in the game: gunshots near full, music 0.7 on the menu and 0.55 in play,
ambience 0.5, footsteps 0.3 or quieter, and 3D sounds fade with distance.

## Known limits

- Characters are not visible through car windows, because the generated car shell is opaque.
- Cars, benches and other props use box colliders, so a bullet hole can appear slightly off the curved body of a car.
- The hands' hold on a gun is procedural for every gun (the clips' own arms are replaced by IK); the legs use the Mixamo rifle clips.
- Sprint, unarmed crouch, jump, roll and driving still use the Quaternius clips: the library has no Mixamo version without a weapon
  in hand yet.

## Reference photos

The Dacia 1310, Oltcit Club and Trabant 601 concepts were drawn by FLUX.3 from these Wikimedia Commons photos
(`tools/fetch_refs.py`; the photos themselves are not in the repository):

- Dacia 1310: [Dacia (7906583028) (2).jpg](https://commons.wikimedia.org/wiki/File:Dacia_(7906583028)_(2).jpg) and
  [Dacia (7907510754).jpg](https://commons.wikimedia.org/wiki/File:Dacia_(7907510754).jpg) by Torsten Maue, CC BY 2.0;
  [Dacia1310.jpg](https://commons.wikimedia.org/wiki/File:Dacia1310.jpg) by Raf24/Sokoban, public domain.
- Oltcit Club: [Oltcit Auto.jpg](https://commons.wikimedia.org/wiki/File:Oltcit_Auto.jpg), public domain;
  [OLTCIT CLUB 11 R.JPG](https://commons.wikimedia.org/wiki/File:OLTCIT_CLUB_11_R.JPG) by Kelovy, public domain;
  [Oltcit side.jpg](https://commons.wikimedia.org/wiki/File:Oltcit_side.jpg) by Artur Heise, CC BY 2.5.
- Trabant 601: [three-quarter front](https://commons.wikimedia.org/wiki/File:Cream_Trabant_601_S_three-quarter_front_view.jpg),
  [side](https://commons.wikimedia.org/wiki/File:Cream_Trabant_601_S_side_view.jpg) and
  [three-quarter back](https://commons.wikimedia.org/wiki/File:Cream_Trabant_601_S_three-quarter_back_view.jpg) views
  of a cream Trabant 601 S by Renée Kools, CC BY 4.0.

## License

Code under PolyForm Noncommercial 1.0.0, assets under CC BY-NC 4.0; the Mixamo animations stay under Adobe's Mixamo terms.
See [LICENSE.md](LICENSE.md).
