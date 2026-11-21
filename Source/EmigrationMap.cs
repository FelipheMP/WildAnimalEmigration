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
    // One candidate is processed per tick, bounding route searches during large checks.
    private IEnumerator<bool> pendingCheck;
    private bool checkInProgress;
    private bool reportCheck;
    private int scheduledByCheck;
    private readonly List<Pawn> validationAnimals = new();
    private readonly HashSet<Pawn> validationProtected = new();
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
        Scribe_Values.Look(ref checkInProgress, "wildAnimalEmigrationCheckInProgress", false);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            activeSpecies ??= new List<ThingDef>();
            activeSpecies.RemoveAll(species => species == null);
            // Rebuild unfinished work from live pawns. Existing jobs are counted again.
            checkRequested = checkInProgress;
        }
    }

    internal void RequestCheck() => checkRequested = true;

    public override void MapComponentTick()
    {
        if (!map.IsPlayerHome)
            return;

        int tick = Find.TickManager.TicksGame;
        ReportDepartures(tick);

        if (pendingCheck == null && (checkRequested || lastCheckTick < 0
            || tick - lastCheckTick >= EmigrationMod.Settings.CheckIntervalTicks))
        {
            reportCheck = checkRequested;
            checkRequested = false;
            lastCheckTick = tick;
            scheduledByCheck = 0;
            checkInProgress = true;
            pendingCheck = RunCheck().GetEnumerator();
        }

        if (pendingCheck == null)
            return;

        if (pendingCheck.MoveNext())
        {
            if (pendingCheck.Current)
                scheduledByCheck++;
            return;
        }

        pendingCheck.Dispose();
        pendingCheck = null;
        checkInProgress = false;
        if (reportCheck && EmigrationMod.Settings.ShowNotifications)
        {
            Messages.Message($"Wildlife check complete. Scheduled {scheduledByCheck} departures across all species.",
                MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }

    private IEnumerable<bool> RunCheck()
    {
        // Snapshot membership before jobs can move or despawn pawns.
        var groups = map.mapPawns.AllPawnsSpawned.Where(IsResidentWildAnimal).ToLookup(pawn => pawn.def);
        activeSpecies.RemoveAll(species => !groups.Contains(species));
        foreach (var group in groups)
        {
            foreach (bool scheduled in CheckSpecies(group.Key, group.ToList()))
                yield return scheduled;
            yield return false;
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

    private IEnumerable<bool> CheckSpecies(ThingDef species, List<Pawn> animals)
    {
        int maximum = EmigrationMod.Settings.MaximumFor(species);
        int target = maximum - 1;
        if (animals.Count <= target)
        {
            activeSpecies.Remove(species);
            yield break;
        }

        if (!activeSpecies.Contains(species))
        {
            if (animals.Count <= maximum)
                yield break;
            activeSpecies.Add(species);
        }

        int alreadyLeaving = animals.Count(HasDepartureJob);
        int places = EmigrationMod.Settings.DepartureSlots(animals.Count - target, alreadyLeaving);
        if (places == 0)
            yield break;

        int started = 0;
        foreach (Pawn animal in animals)
        {
            // Yield before each candidate, including rejected ones, so crowded maps
            // cannot perform hundreds of route searches in one simulation tick.
            yield return false;
            if (animal.Map != map || !CanSchedule(animal) || HasDepartureJob(animal)
                || !CanFinishDeparture(animal))
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
            yield return true;
            if (started == places)
                yield break;
        }
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

    private static HashSet<Pawn> ProtectedAnimals(List<Pawn> animals, out int breedingCount, out int motherCount, HashSet<Pawn> buffer = null)
    {
        var protectedAnimals = buffer ?? new HashSet<Pawn>();
        protectedAnimals.Clear();
        if (!EmigrationMod.Settings.AllowMothers)
        {
            // Protect mothers only while their known young are still residents of this map.
            foreach (Pawn young in animals)
            {
                if (young.ageTracker.Adult)
                    continue;
                Pawn mother = young.GetMother();
                if (mother != null)
                    protectedAnimals.Add(mother);
            }
            protectedAnimals.IntersectWith(animals);
        }
        motherCount = protectedAnimals.Count;

        // Rank adults in one pass. Protected mothers rank first, then animals
        // without an existing departure job. Equal ranks retain the original order.
        Pawn male = null;
        Pawn female = null;
        int maleRank = int.MaxValue;
        int femaleRank = int.MaxValue;
        foreach (Pawn animal in animals)
        {
            if (!animal.ageTracker.Adult || (animal.gender != Gender.Male && animal.gender != Gender.Female))
                continue;
            int rank = protectedAnimals.Contains(animal) ? 0 : 2;
            if (HasDepartureJob(animal))
                rank++;
            if (animal.gender == Gender.Male && rank < maleRank)
            {
                male = animal;
                maleRank = rank;
            }
            if (animal.gender == Gender.Female && rank < femaleRank)
            {
                female = animal;
                femaleRank = rank;
            }
        }
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

        // Reuse storage but rebuild from live pawns. Deaths, births, taming and
        // changes from other mods can occur even within a tick, so no stale cache.
        validationAnimals.Clear();
        foreach (Pawn other in map.mapPawns.AllPawnsSpawned)
        {
            if (other.def == animal.def && IsResidentWildAnimal(other))
                validationAnimals.Add(other);
        }
        int target = EmigrationMod.Settings.MaximumFor(animal.def) - 1;
        return validationAnimals.Count > target
            && !ProtectedAnimals(validationAnimals, out _, out _, validationProtected).Contains(animal);
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
