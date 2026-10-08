# Achievements

Fifty achievements, earned online and in practice (owner's list of 2026-10-09, item 4).
Open them from **ACHIEVEMENTS** on the main menu or in the Esc menu during a match;
the **GLOBAL RANKING** sits beside them.

## How they work

- **Online achievements are judged by the master server.** At the end of every online round
  the game server reports each player's numbers (kills, flags, the longest shot, the best
  multi-kill...) and the master adds them to the account's career. An achievement unlocks the
  moment its career number reaches the target, and a banner drops in at the top of the screen.
- **Practice achievements are seen by your own game.** No server watches an offline match, so
  the game records them on this computer, shows the banner at once, and claims them for your
  account the next time you sign in to multiplayer.
- **The list is sorted by how many players hold each one**, commonest first, like a store's
  global achievement list. The share is out of every player with a career; the rarer ones are
  marked RARE and ULTRA RARE.
- **Hidden achievements** show a sealed badge and "???" until earned. Their descriptions are
  below, folded away: open the Secret section only if you want the spoilers.
- **Badges** are generated from `tools/ui/badges.py` by `tools/ui/make_icons.py`; this page is
  generated from the catalogue by `tools/ui/write_achievements_doc.py`. Do not edit it by hand.

Metals, easiest to hardest: Bronze, Silver, Gold, Platinum.

| Family | Achievements |
|---|---|
| Multiplayer | 10 |
| Combat | 15 |
| Vehicles | 5 |
| Honor | 5 |
| Hard | 5 |
| Secret | 6 |
| Practice | 4 |
| **All** | **50** |

## Multiplayer

Playing online: matches, rounds won, time served, every map.

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/boots_on_the_ground.png" width="64" alt="BOOTS ON THE GROUND"> | **BOOTS ON THE GROUND**<br>`boots_on_the_ground` | Play your first online match. | Bronze | matches, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/tour_of_duty.png" width="64" alt="TOUR OF DUTY"> | **TOUR OF DUTY**<br>`tour_of_duty` | Play 10 online matches. | Bronze | matches, 10 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/career_soldier.png" width="64" alt="CAREER SOLDIER"> | **CAREER SOLDIER**<br>`career_soldier` | Play 100 online matches. | Silver | matches, 100 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/forever_war.png" width="64" alt="FOREVER WAR"> | **FOREVER WAR**<br>`forever_war` | Spend 24 hours in online battles. | Gold | time played, 24 h |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/victory.png" width="64" alt="VICTORY"> | **VICTORY**<br>`victory` | Win an online round. | Bronze | wins, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/champion.png" width="64" alt="CHAMPION"> | **CHAMPION**<br>`champion` | Win 25 online rounds. | Silver | wins, 25 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/world_traveler.png" width="64" alt="WORLD TRAVELER"> | **WORLD TRAVELER**<br>`world_traveler` | Play an online match on every map. | Silver | maps played, 3 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/night_owl.png" width="64" alt="NIGHT OWL"> | **NIGHT OWL**<br>`night_owl` | Play a Night Mode match. | Bronze | night matches, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/patched_up.png" width="64" alt="PATCHED UP"> | **PATCHED UP**<br>`patched_up` | Fall 100 times and keep coming back. | Bronze | deaths, 100 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/quartermaster.png" width="64" alt="QUARTERMASTER"> | **QUARTERMASTER**<br>`quartermaster` | Score 1,000 points for your side. | Bronze | score, 1,000 |

## Combat

Kills and how they were made: multi-kills, streaks, headshots, long shots, blades and grenades.

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/first_blood.png" width="64" alt="FIRST BLOOD"> | **FIRST BLOOD**<br>`first_blood` | Get your first kill online. | Bronze | kills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/decorated.png" width="64" alt="DECORATED"> | **DECORATED**<br>`decorated` | Get 100 kills online. | Silver | kills, 100 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/veteran.png" width="64" alt="VETERAN"> | **VETERAN**<br>`veteran` | Get 1,000 kills online. | Gold | kills, 1,000 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/two_for_one.png" width="64" alt="TWO FOR ONE"> | **TWO FOR ONE**<br>`two_for_one` | Kill two enemies within three seconds. | Bronze | best multi kill, 2 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/triple_threat.png" width="64" alt="TRIPLE THREAT"> | **TRIPLE THREAT**<br>`triple_threat` | Three kills, each within three seconds of the last. | Silver | best multi kill, 3 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/unstoppable.png" width="64" alt="UNSTOPPABLE"> | **UNSTOPPABLE**<br>`unstoppable` | Kill 10 enemies without dying. | Gold | best streak, 10 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/sharpshooter.png" width="64" alt="SHARPSHOOTER"> | **SHARPSHOOTER**<br>`sharpshooter` | Land 50 headshot kills. | Bronze | headshots, 50 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/long_shot.png" width="64" alt="LONG SHOT"> | **LONG SHOT**<br>`long_shot` | Kill an enemy 150 metres away or more. | Silver | longest kill, 150 m |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/up_close.png" width="64" alt="UP CLOSE"> | **UP CLOSE**<br>`up_close` | Kill an enemy with a blade or a wrench. | Bronze | melee kills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/frag_out.png" width="64" alt="FRAG OUT"> | **FRAG OUT**<br>`frag_out` | Kill two enemies with a single grenade. | Silver | grenade double kills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/demolition_expert.png" width="64" alt="DEMOLITION EXPERT"> | **DEMOLITION EXPERT**<br>`demolition_expert` | Get 100 kills with explosives. | Gold | explosive kills, 100 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/payback.png" width="64" alt="PAYBACK"> | **PAYBACK**<br>`payback` | Kill the soldier who last killed you. | Bronze | revenge kills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/man_vs_machine.png" width="64" alt="MAN VS MACHINE"> | **MAN VS MACHINE**<br>`man_vs_machine` | Kill 100 bots online. | Bronze | bot kills, 100 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/player_hunter.png" width="64" alt="PLAYER HUNTER"> | **PLAYER HUNTER**<br>`player_hunter` | Kill 50 human players. | Silver | player kills, 50 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/night_stalker.png" width="64" alt="NIGHT STALKER"> | **NIGHT STALKER**<br>`night_stalker` | Get 100 kills in Night Mode. | Silver | night kills, 100 |

## Vehicles

Fighting from the jeeps, tanks, helicopters and boats, and against them.

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/road_rage.png" width="64" alt="ROAD RAGE"> | **ROAD RAGE**<br>`road_rage` | Run an enemy over. | Bronze | roadkills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/tank_buster.png" width="64" alt="TANK BUSTER"> | **TANK BUSTER**<br>`tank_buster` | Destroy an enemy tank with its crew inside. | Silver | tanks destroyed, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/armored_fist.png" width="64" alt="ARMORED FIST"> | **ARMORED FIST**<br>`armored_fist` | Get 50 kills from a tank. | Gold | tank kills, 50 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/air_superiority.png" width="64" alt="AIR SUPERIORITY"> | **AIR SUPERIORITY**<br>`air_superiority` | Get 25 kills from a helicopter. | Gold | helicopter kills, 25 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/anchors_aweigh.png" width="64" alt="ANCHORS AWEIGH"> | **ANCHORS AWEIGH**<br>`anchors_aweigh` | Get 10 kills from a boat. | Silver | boat kills, 10 |

## Honor

Playing for the side: flags taken, rounds carried, comebacks.

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/flag_bearer.png" width="64" alt="FLAG BEARER"> | **FLAG BEARER**<br>`flag_bearer` | Help capture a flag. | Bronze | flags captured, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/blitzkrieg.png" width="64" alt="BLITZKRIEG"> | **BLITZKRIEG**<br>`blitzkrieg` | Help capture 50 flags. | Gold | flags captured, 50 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/mvp.png" width="64" alt="MOST VALUABLE"> | **MOST VALUABLE**<br>`mvp` | Win a round with the most points of anyone in it. | Silver | mvp rounds, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/against_all_odds.png" width="64" alt="AGAINST ALL ODDS"> | **AGAINST ALL ODDS**<br>`against_all_odds` | Win a round after your side trailed by 100 points. | Gold | comeback wins, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/conqueror.png" width="64" alt="CONQUEROR"> | **CONQUEROR**<br>`conqueror` | Win 100 online rounds. | Gold | wins, 100 |

## Hard

The long grind and the rare feat. Most players never see these.

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/war_machine.png" width="64" alt="WAR MACHINE"> | **WAR MACHINE**<br>`war_machine` | Get 10,000 kills online. | Platinum | kills, 10,000 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/legend_never_dies.png" width="64" alt="LEGENDS NEVER DIE"> | **LEGENDS NEVER DIE**<br>`legend_never_dies` | Kill 25 enemies without dying. | Platinum | best streak, 25 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/head_hunter.png" width="64" alt="HEAD HUNTER"> | **HEAD HUNTER**<br>`head_hunter` | Land 500 headshot kills. | Gold | headshots, 500 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/eagle_eye.png" width="64" alt="EAGLE EYE"> | **EAGLE EYE**<br>`eagle_eye` | Kill an enemy with a headshot from 300 metres. | Platinum | longest headshot, 300 m |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/flawless.png" width="64" alt="FLAWLESS"> | **FLAWLESS**<br>`flawless` | Finish a round with 15 kills and no deaths. | Platinum | flawless rounds, 1 |

## Secret

Hidden in the game until earned: the page shows a sealed badge and "???".

<details>
<summary>Spoilers: the hidden achievements</summary>

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/gravity_wins.png" width="64" alt="GRAVITY WINS"> | **GRAVITY WINS**<br>`gravity_wins` | Die from a fall. | Bronze | fall deaths, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/sleeping_with_the_fishes.png" width="64" alt="SLEEPING WITH THE FISHES"> | **SLEEPING WITH THE FISHES**<br>`sleeping_with_the_fishes` | Drown. | Bronze | drown deaths, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/friendly_fire.png" width="64" alt="FRIENDLY FIRE"> | **FRIENDLY FIRE**<br>`friendly_fire` | Kill a teammate. It happens. | Bronze | team kills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/own_goal.png" width="64" alt="OWN GOAL"> | **OWN GOAL**<br>`own_goal` | Blow yourself up. | Bronze | own explosive deaths, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/not_today.png" width="64" alt="NOT TODAY"> | **NOT TODAY**<br>`not_today` | Get a kill within three seconds of deploying. | Silver | quick kills, 1 |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/hat_trick.png" width="64" alt="HAT TRICK"> | **HAT TRICK**<br>`hat_trick` | Three kills in a row, every one a headshot. | Gold | headshot run, 3 |

</details>

## Practice

Earned offline, against bots, and on the How to play guide. Claimed for your account the next time you sign in.

| Badge | Achievement | How to earn it | Metal | Tracked by |
|---|---|---|---|---|
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/basic_training.png" width="64" alt="BASIC TRAINING"> | **BASIC TRAINING**<br>`basic_training` | Finish a practice match. | Bronze | Seen by your game |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/bot_buster.png" width="64" alt="BOT BUSTER"> | **BOT BUSTER**<br>`bot_buster` | Get 25 kills in one practice match. | Silver | Seen by your game |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/one_man_army.png" width="64" alt="ONE-MAN ARMY"> | **ONE-MAN ARMY**<br>`one_man_army` | Win a practice match with 100 bots. | Gold | Seen by your game |
| <img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/student_of_war.png" width="64" alt="STUDENT OF WAR"> | **STUDENT OF WAR**<br>`student_of_war` | Read every tab of How to play. | Bronze | Seen by your game |

## The hidden badge

<img src="../Ironfront_Reborn/Assets/Resources/IronfrontUi/Achievements/_hidden.png" width="64" alt="Hidden achievement"> What every secret
achievement shows until it is earned.
