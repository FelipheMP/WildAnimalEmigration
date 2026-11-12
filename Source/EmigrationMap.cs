using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace WildAnimalEmigration;

[DefOf]
public static class EmigrationJobDefOf
{
    public static JobDef WildAnimalEmigration_Leave;

    static EmigrationJobDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(EmigrationJobDefOf));
}

public sealed class EmigrationMap : MapComponent
{
    private const int JobTimeout = 5000;

    private List<ThingDef> activeSpecies = new();
    private readonly Dictionary<int, ThingDef> departures = new();
    private bool checkRequested;
    private int lastCheckTick = -1;
    private int reportDeparturesAt;

    public EmigrationMap(Map map) : base(map) { }

    public override void ExposeData()
    {
        // Remember a departure cycle even if the population has already fallen to the maximum.
        // RimWorld separately saves each animal's current job and its destination.
        Scribe_Collections.Look(ref activeSpecies, "wildAnimalEmigrationActiveSpecies", LookMode.Def);
        Scribe_Values.Look(ref lastCheckTick, "wildAnimalEmigrationLastCheckTick", -1);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            activeSpecies ??= new List<ThingDef>();
            activeSpecies.RemoveAll(species => species == null);
        }
    }

    internal void RequestCheck() => checkRequested = true;

    public override void MapComponentTick()
    {
        if (!map.IsPlayerHome)
            return;

        int tick = Find.TickManager.TicksGame;
        ReportDepartures(tick);

        // Measure from the last check so interval edits take effect on the next tick
        // and reloading a save preserves the time left before the next check.
        if (!checkRequested && lastCheckTick >= 0
            && tick - lastCheckTick < EmigrationMod.Settings.CheckIntervalTicks)
            return;

        bool manual = checkRequested;
        checkRequested = false;
        lastCheckTick = tick;

        // Take a snapshot because starting a job can immediately move or despawn a pawn.
        var groups = map.mapPawns.AllPawnsSpawned.Where(IsResidentWildAnimal).ToLookup(pawn => pawn.def);
        activeSpecies.RemoveAll(species => !groups.Contains(species));
        int started = 0;

        foreach (var group in groups)
            started += CheckSpecies(group.Key, group.ToList());

        if (manual && EmigrationMod.Settings.ShowNotifications)
        {
            string result = started == 0
                ? "Wildlife check complete. No new departures could start."
                : $"Wildlife check complete. Scheduled {started} departures across all species.";
            Messages.Message(result, MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }

    private void ReportDepartures(int tick)
    {
        if (departures.Count == 0)
            return;

        // Batch completed departures independently of the population-check schedule.
        // A multi-day check interval should not delay a departure report for days.
        if (EmigrationMod.Settings.ShowNotifications)
        {
            if (tick < reportDeparturesAt)
                return;

            // Count each pawn once per report and omit any that returned before it went out.
            // The live species count also reflects births, new arrivals, deaths and taming.
            var spawned = map.mapPawns.AllPawnsSpawned;
            var onMap = spawned.Select(animal => animal.thingIDNumber).ToHashSet();
            var residents = spawned.Where(IsResidentWildAnimal).ToLookup(animal => animal.def);
            var groups = departures.Where(entry => !onMap.Contains(entry.Key)).ToLookup(entry => entry.Value);
            foreach (var group in groups)
            {
                string label = group.Key.label;
                Messages.Message($"Wild animal emigration from {map.Parent.Label} ({label}). "
                    + $"{group.Count()} left and {residents[group.Key].Count()} wild residents are on the map now.",
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }
        departures.Clear();
    }

    private int CheckSpecies(ThingDef species, List<Pawn> animals)
    {
        int maximum = EmigrationMod.Settings.MaximumFor(species);
        int target = maximum - 1;
        if (animals.Count <= target)
        {
            activeSpecies.Remove(species);
            return 0;
        }

        if (!activeSpecies.Contains(species))
        {
            if (animals.Count <= maximum)
                return 0;
            activeSpecies.Add(species);
        }

        int alreadyLeaving = animals.Count(HasDepartureJob);
        int places = EmigrationMod.Settings.DepartureSlots(animals.Count - target, alreadyLeaving);
        if (places == 0)
            return 0;

        var protectedAnimals = ProtectedAnimals(animals, out _, out _);
        int started = 0;
        foreach (Pawn animal in animals)
        {
            if (!CanSchedule(animal) || protectedAnimals.Contains(animal) || HasDepartureJob(animal))
                continue;

            if (!RCellFinder.TryFindBestExitSpot(animal, out IntVec3 edge, TraverseMode.ByPawn, canBash: false)
                || !edge.OnEdge(map))
                continue;

            var job = JobMaker.MakeJob(EmigrationJobDefOf.WildAnimalEmigration_Leave, edge);
            job.expiryInterval = JobTimeout;
            if (animal.Awake() && IsIdle(animal))
                animal.jobs.StartJob(job, JobCondition.InterruptOptional);
            else
                animal.jobs.jobQueue.EnqueueLast(job);

            if (animal.Spawned && !HasDepartureJob(animal))
                continue;

            started++;
            if (started == places)
                break;
        }
        return started;
    }

    // A factionless animal in a Lord belongs to another AI group, such as a passing herd.
    private static bool IsResidentWildAnimal(Pawn animal)
    {
        return animal.Spawned && !animal.Dead && animal.RaceProps.Animal
            && animal.Faction == null && animal.GetLord() == null
            && animal.CurJob?.exitMapOnArrival != true;
    }

    internal static bool CanWalkOut(Pawn animal)
        => CanSchedule(animal) && animal.Awake();

    private static bool CanSchedule(Pawn animal)
    {
        return IsResidentWildAnimal(animal) && !animal.Downed && !animal.InMentalState
            && (EmigrationMod.Settings.AllowYoungAnimals || animal.ageTracker.Adult)
            && animal.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
    }

    private static bool HasDepartureJob(Pawn animal)
        => animal.jobs.AllJobs().Any(job => job.def == EmigrationJobDefOf.WildAnimalEmigration_Leave);

    private static bool IsIdle(Pawn animal)
    {
        // Preserve hunting, eating, mating, resting, and fleeing jobs.
        string jobName = animal.CurJobDef?.defName;
        return jobName is null or "Wait" or "Wait_Wander" or "GotoWander";
    }

    private static HashSet<Pawn> ProtectedAnimals(List<Pawn> animals, out int breedingCount, out int motherCount)
    {
        var protectedAnimals = new HashSet<Pawn>();
        if (!EmigrationMod.Settings.AllowMothers)
        {
            // Protect mothers only while their known young are still residents of this map.
            foreach (Pawn young in animals.Where(animal => !animal.ageTracker.Adult))
            {
                Pawn mother = young.GetMother();
                if (mother != null)
                    protectedAnimals.Add(mother);
            }
            protectedAnimals.IntersectWith(animals);
        }
        motherCount = protectedAnimals.Count;

        // Keep at most one adult of each sex. Prefer animals that are already staying.
        var adults = animals.Where(animal => animal.ageTracker.Adult)
            .OrderByDescending(protectedAnimals.Contains)
            .ThenBy(HasDepartureJob);
        Pawn male = adults.FirstOrDefault(animal => animal.gender == Gender.Male);
        Pawn female = adults.FirstOrDefault(animal => animal.gender == Gender.Female);
        breedingCount = 0;
        if (male != null && female != null)
        {
            protectedAnimals.Add(male);
            protectedAnimals.Add(female);
            breedingCount = 2;
        }
        return protectedAnimals;
    }

    internal string DescribeSpecies(ThingDef species)
    {
        var animals = map.mapPawns.AllPawnsSpawned
            .Where(animal => animal.def == species && IsResidentWildAnimal(animal))
            .ToList();
        var settings = EmigrationMod.Settings;
        int maximum = settings.MaximumFor(species);
        int scheduled = animals.Count(HasDepartureJob);
        int young = animals.Count(animal => !animal.ageTracker.Adult);
        var protectedAnimals = ProtectedAnimals(animals, out int breedingCount, out int motherCount);
        int available = animals.Count(animal => CanSchedule(animal)
            && !protectedAnimals.Contains(animal) && !HasDepartureJob(animal));
        string batch = settings.AllSurplusAtOnce ? "All eligible surplus" : settings.BatchSize.ToString();

        return $"{species.LabelCap} on {map.Parent.Label}\n\n"
            + $"Wild residents: {animals.Count}\n"
            + $"Maximum: {maximum}   Departure target: {maximum - 1}\n"
            + $"Batch limit: {batch}   Check interval: {settings.CheckIntervalTicks / EmigrationSettings.TicksPerHour} h\n\n"
            + $"Young residents: {young} (emigration {(settings.AllowYoungAnimals ? "allowed" : "disabled")})\n"
            + $"Adults kept for breeding: {breedingCount}\n"
            + $"Mothers kept with young: {motherCount}\n"
            + $"Departures queued or walking: {scheduled}\n"
            + $"Animals available before checking their route: {available}";
    }

    internal bool CanFinishDeparture(Pawn animal)
    {
        if (!map.IsPlayerHome || !CanWalkOut(animal))
            return false;

        var animals = map.mapPawns.AllPawnsSpawned
            .Where(other => other.def == animal.def && IsResidentWildAnimal(other))
            .ToList();
        int target = EmigrationMod.Settings.MaximumFor(animal.def) - 1;

        // Births, deaths, taming and settings changes can happen during the walk.
        return animals.Count > target && !ProtectedAnimals(animals, out _, out _).Contains(animal);
    }

    internal void RecordDeparture(Pawn animal)
    {
        if (animal.Spawned || !EmigrationMod.Settings.ShowNotifications)
            return;

        if (departures.Count == 0)
            reportDeparturesAt = Find.TickManager.TicksGame + EmigrationSettings.TicksPerHour;

        departures.TryAdd(animal.thingIDNumber, animal.def);
    }
}
