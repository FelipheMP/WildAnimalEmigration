# Wild Animal Emigration

A RimWorld 1.7 mod that lets surplus wild animals walk off your colony map. Every species is counted independently. For example, eleven wild monkeys can trigger a departure while three wild deer stay.

**Author:** FelipheMP. Requires RimWorld 1.7 (Waylith). Core is sufficient.

## Install on Windows

1. Download `WildAnimalEmigration.zip` from the GitHub release. In RimWorld, use **Save As** to make a copy of the colony you want to test.
2. Find your RimWorld installation folder. On Steam, right-click RimWorld in your library and choose **Manage → Browse local files**.
3. Extract the ZIP into the installation's `Mods` folder. You should end up with `Mods\WildAnimalEmigration\About\About.xml` and `Mods\WildAnimalEmigration\Assemblies\WildAnimalEmigration.dll`.
4. Start RimWorld 1.7, open **Mods**, and enable **Wild Animal Emigration**. Restart the game if prompted. Core is sufficient.
5. Load the copy of your colony and unpause. By default, the mod checks each colony map once per in-game day.
6. To check immediately, view your colony map and open **Options → Mod settings → Wild Animal Emigration → Check this colony now**. Close the menu and resume the game. The check message counts newly scheduled departures. A later message counts animals that actually left.

## What happens in the game

- The default maximum is **10 per species per colony map**. At **11 or more**, a departure cycle starts and aims for **9**. A cycle already in progress can continue from ten down to nine. Previously saved custom maximums still apply.
- Babies and juveniles count toward the population. By default, they stay, as do mothers with known young of the same species on the map. When both sexes are present among the adults, the mod keeps one of each. Mobile animals can be scheduled while busy or asleep. They wait for their current job to finish before walking.
- Each animal needs a reachable map edge. A hunt, a blocked route, or a change in the population can delay or cancel a departure. Some species may temporarily stay above the target while eligible animals become available.
- Tame animals, visiting faction animals, and animals already in an AI group such as a passing herd are excluded. Wild predators are included. The game handles the off-map pawn when it leaves through its normal exit API.
- In **Mod settings**, set a default maximum or pick a species and give it its own maximum. The target is always one fewer. For example, a maximum of 12 for deer starts departures at 13 and aims for 11. Settings apply across your colonies and saves. Each colony's departure cycles are saved with that colony.

### Mod settings

- **Batch size per species** defaults to 4. Set it to 10 to allow up to ten animals of a species to be queued or walking out at the same time.
- **Send all eligible surplus animals at once** removes the batch limit. If there are 30 wild monkeys and the target is 9, one check can start up to 21 departures. The normal eligibility and population checks still apply.
- **Hours between automatic checks** defaults to 24. Enter 10 for ten in-game hours or 48 for two days. The game remembers when each colony was last checked across save and reload. **Check this colony now** runs on the next game tick and restarts that colony's countdown. Previously saved custom intervals still apply.
- **Allow babies and juveniles to emigrate** lets young animals that can walk join departures. **Allow mothers with young on this map to emigrate** lifts the separate protection for mothers. Both boxes are off by default. Enable both to use the family rules from 1.1.3. The breeding pair is protected in every combination.
- Turn off **Show emigration and manual check notifications** to silence the mod's top-left messages. When enabled, reports show completed departures and the current wild population for that species. Each animal is counted once per report. RimWorld can still spawn new animals, so the current population can rise even after a group has left.
- Choose a species and use **Preview departures** to see the active settings, how many animals are already queued or walking, and how many adults are being kept for breeding or parenting. One mother may count in both categories.

## Read the code in this order

1. **`About/About.xml`** tells RimWorld the mod's name, ID, and supported version. **`Assemblies/WildAnimalEmigration.dll`** is the compiled code that the game loads.
2. **`Defs/Jobs.xml`** registers the emigration job and tells RimWorld which C# class runs it. A *Def* is a named piece of game data. The job's `defName` is also how the game identifies that job in a save.
3. **`Source/EmigrationMap.cs`** is the decision maker. RimWorld creates a `MapComponent` for each loaded map. This one runs on colony maps, groups wild animals by their race definition, and starts jobs for eligible animals in batches. It counts animals already doing the job so it can avoid sending too many.
4. **`Source/DepartureJob.cs`** controls one animal. A `JobDriver` splits a job into steps called *toils*. Here the first step walks to an edge tile. The second rechecks the animal and population, then calls the same `Pawn.ExitMap` API used by the game. This final check matters if a predator killed an animal or the colony tamed one while the group was walking.
5. **`Source/EmigrationSettings.cs`** draws the settings screen and reads and writes settings. RimWorld's `Scribe` API also saves which species have a departure cycle in progress. RimWorld already saves each pawn's current job and destination.

RimWorld supplies both the map hook and the job system, so the mod needs no Harmony patches or extra dependencies.

## Rebuild after a code change

Install the **.NET 10 SDK** and open a terminal in the mod folder, where you can see `README.md` and `Source/`. Set `RIMWORLD_DIR` to the directory that contains RimWorld 1.7's `Assembly-CSharp.dll`. The paths below are Steam examples. Use **Manage → Browse local files** in Steam to find your installation if it differs.

### Windows (PowerShell)

```powershell
$env:RIMWORLD_DIR = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
dotnet build .\Source\WildAnimalEmigration.csproj -c Release
```

### Linux (Bash)

```bash
export RIMWORLD_DIR="$HOME/.local/share/Steam/steamapps/common/RimWorld"
dotnet build ./Source/WildAnimalEmigration.csproj -c Release
```

If the build asks you to set `RIMWORLD_DIR`, check that the directory you chose directly contains `Assembly-CSharp.dll`. Keep the quotes around your path, especially if it contains spaces.

On either platform, the build replaces `Assemblies/WildAnimalEmigration.dll`. If you built in a separate source folder, copy that DLL into the installed mod's `Assemblies/` folder. Close and restart RimWorld to load the new DLL. The project reads the game assemblies from your installation and keeps them out of the mod package.

## Test on a copy of your save

1. Find a species with at least eleven wild animals and another with ten or fewer. Leave the defaults in place and run **Check this colony now**. Watch an eligible animal from the larger group walk toward the edge. Confirm the wild count drops when the animal leaves and the smaller group stays.
2. Let the simulation run until the larger group settles near nine. A heavily juvenile group or a group with blocked exits can take longer. Open the developer log if you see red errors and keep the first relevant error and stack trace.
3. Save while an animal is walking and reload that test save. Confirm that departures can continue. Try taming a departing animal before it reaches the edge and check that it stays.
4. To test the final population check, reduce the number of wild animals in a species to nine while an animal is still walking. It should remain on the map when it reaches the edge.
5. On an overcrowded map, try a batch size of 10, then try **Send all eligible surplus animals at once** on a fresh copy. Try a 24-hour interval and save and reload between checks. Turn off notifications and confirm animals still depart without the mod's messages.
6. For a species with mothers and young, open **Preview departures** and try each checkbox on its own, then both together. Confirm the preview changes before starting a manual check.

The DLL was compiled against RimWorld **1.7.4976 rev1043**.
