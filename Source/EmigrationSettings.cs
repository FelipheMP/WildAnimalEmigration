using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace WildAnimalEmigration;

public sealed class EmigrationSettings : ModSettings
{
    internal const int MinimumMaximum = 3;
    internal const int MaximumMaximum = 10000;
    internal const int TicksPerHour = 2500;
    internal const int DefaultCheckHours = 24;
    internal const int MaximumCheckHours = 24000;

    private const int StartingMaximum = 10;

    internal int DefaultMaximum = StartingMaximum;
    internal int BatchSize = 4;
    internal int CheckHours = DefaultCheckHours;
    internal bool AllSurplusAtOnce;
    internal bool AllowYoungAnimals;
    internal bool AllowMothers;
    internal bool ShowNotifications = true;
    internal Dictionary<string, int> SpeciesMaximums = new();

    internal int CheckIntervalTicks => Math.Clamp(CheckHours, 1, MaximumCheckHours) * TicksPerHour;

    internal int DepartureSlots(int surplus, int alreadyLeaving)
    {
        // Pawns already walking count against both the surplus and the batch limit.
        int allowed = AllSurplusAtOnce ? surplus : Math.Min(surplus, Math.Max(1, BatchSize));
        return Math.Max(0, allowed - alreadyLeaving);
    }

    internal int MaximumFor(ThingDef species)
    {
        int maximum = SpeciesMaximums.GetValueOrDefault(species.defName, DefaultMaximum);
        return Math.Clamp(maximum, MinimumMaximum, MaximumMaximum);
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref DefaultMaximum, "defaultMaximum", StartingMaximum);
        Scribe_Values.Look(ref BatchSize, "batchSize", 4);
        Scribe_Values.Look(ref CheckHours, "checkHours", DefaultCheckHours);
        Scribe_Values.Look(ref AllSurplusAtOnce, "allSurplusAtOnce", false);
        Scribe_Values.Look(ref AllowYoungAnimals, "allowYoungAnimals", false);
        Scribe_Values.Look(ref AllowMothers, "allowMothers", false);
        Scribe_Values.Look(ref ShowNotifications, "showNotifications", true);
        Scribe_Collections.Look(ref SpeciesMaximums, "speciesMaximums", LookMode.Value, LookMode.Value);
        SpeciesMaximums ??= new Dictionary<string, int>();
    }
}

public sealed class EmigrationMod : Mod
{
    internal static EmigrationSettings Settings { get; private set; }

    private ThingDef selectedSpecies;
    private string defaultMaximumText;
    private string speciesMaximumText;
    private string batchSizeText;
    private string checkHoursText;

    public EmigrationMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<EmigrationSettings>();
    }

    public override string SettingsCategory() => "Wild Animal Emigration";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.Label("Above the maximum, emigration aims to leave one fewer than the maximum.");
        NumberField(listing, "Default maximum per species", ref Settings.DefaultMaximum,
            ref defaultMaximumText, EmigrationSettings.MinimumMaximum, EmigrationSettings.MaximumMaximum);
        listing.Gap();
        listing.CheckboxLabeled("Send all eligible surplus animals at once", ref Settings.AllSurplusAtOnce);
        if (!Settings.AllSurplusAtOnce)
        {
            NumberField(listing, "Batch size per species", ref Settings.BatchSize,
                ref batchSizeText, 1, EmigrationSettings.MaximumMaximum);
        }
        NumberField(listing, "Hours between automatic checks (24 hours = 1 day)",
            ref Settings.CheckHours, ref checkHoursText, 1, EmigrationSettings.MaximumCheckHours);
        listing.CheckboxLabeled("Allow babies and juveniles to emigrate", ref Settings.AllowYoungAnimals);
        listing.CheckboxLabeled("Allow mothers with young on this map to emigrate", ref Settings.AllowMothers);
        listing.CheckboxLabeled("Show emigration and manual check notifications", ref Settings.ShowNotifications);
        listing.Gap();

        if (listing.ButtonText(selectedSpecies?.LabelCap.ToString() ?? "Choose a species to customize"))
        {
            var options = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => def.race?.Animal == true)
                .OrderBy(def => def.label)
                .Select(def => new FloatMenuOption(def.LabelCap, () =>
                {
                    selectedSpecies = def;
                    speciesMaximumText = null;
                }))
                .ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (selectedSpecies != null)
        {
            int previous = Settings.MaximumFor(selectedSpecies);
            int maximum = previous;
            NumberField(listing, "Maximum for " + selectedSpecies.label, ref maximum,
                ref speciesMaximumText, EmigrationSettings.MinimumMaximum, EmigrationSettings.MaximumMaximum);
            if (maximum != previous)
                Settings.SpeciesMaximums[selectedSpecies.defName] = maximum;

            if (Settings.SpeciesMaximums.ContainsKey(selectedSpecies.defName)
                && listing.ButtonText("Use the default for " + selectedSpecies.label))
            {
                Settings.SpeciesMaximums.Remove(selectedSpecies.defName);
                speciesMaximumText = null;
            }
        }

        listing.Gap();
        Map map = Current.Game == null ? null : Find.CurrentMap;
        if (map?.IsPlayerHome == true && selectedSpecies != null
            && listing.ButtonText("Preview departures for " + selectedSpecies.label))
        {
            string preview = map.GetComponent<EmigrationMap>().DescribeSpecies(selectedSpecies);
            Find.WindowStack.Add(new Dialog_MessageBox(preview));
        }
        if (map?.IsPlayerHome == true && listing.ButtonText("Check this colony now"))
        {
            // Let the simulation assign jobs on the next tick, outside the settings UI.
            map.GetComponent<EmigrationMap>().RequestCheck();
            if (Settings.ShowNotifications)
                Messages.Message("Wildlife check queued. Resume the game to run it.", MessageTypeDefOf.NeutralEvent);
        }
        listing.End();
    }

    private static void NumberField(Listing_Standard listing, string label, ref int value,
        ref string buffer, int minimum, int maximum)
    {
        const float gap = 16f;
        const float inputWidth = 110f;
        Rect row = listing.GetRect(Text.LineHeight);
        float labelWidth = row.width - inputWidth - gap;
        Widgets.Label(new Rect(row.x, row.y, labelWidth, row.height), label);
        Widgets.TextFieldNumeric(new Rect(row.x + labelWidth + gap, row.y, inputWidth, row.height),
            ref value, ref buffer, minimum, maximum);
        listing.Gap(listing.verticalSpacing);
    }
}
