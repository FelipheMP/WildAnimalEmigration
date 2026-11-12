using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace WildAnimalEmigration;

public sealed class DepartureJob : JobDriver
{
    // Walking off the map requires no interaction with a reserved object.
    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        // A toil is one step in a RimWorld job. First walk, then attempt to leave.
        this.FailOn(() => !EmigrationMap.CanWalkOut(pawn));
        Toil walk = Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
        Action startWalking = walk.initAction;
        // A queued animal can become protected or be tamed while it finishes another job.
        // Keep the same two toil positions so existing saves can resume this job.
        walk.initAction = () =>
        {
            if (pawn.Map.GetComponent<EmigrationMap>().CanFinishDeparture(pawn))
                startWalking();
            else
                EndJobWith(JobCondition.Incompletable);
        };
        yield return walk;

        Toil leave = ToilMaker.MakeToil("WildAnimalEmigration_Exit");
        leave.initAction = LeaveMap;
        leave.defaultCompleteMode = ToilCompleteMode.Instant;
        yield return leave;
    }

    private void LeaveMap()
    {
        Map map = pawn.Map;
        var emigration = map.GetComponent<EmigrationMap>();
        if (!pawn.Position.OnEdge(map) || !emigration.CanFinishDeparture(pawn))
            return;

        // Use the same Anomaly check and pawn exit API as the vanilla Goto job.
        if (ModsConfig.AnomalyActive && !MetalhorrorUtility.TryPawnExitMap(pawn))
            return;

        // Keep a local reference because ExitMap can end this job during despawn.
        Pawn animal = pawn;
        Rot4 direction = CellRect.WholeMap(map).GetClosestEdge(animal.Position);
        animal.ExitMap(allowedToJoinOrCreateCaravan: false, direction);
        emigration.RecordDeparture(animal);
    }
}
