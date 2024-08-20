using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using HarmonyLib;

namespace IMWG {
[StaticConstructorOnStartup]
public class ImmersiveWorldgen {
	static ImmersiveWorldgen() {
		var harmony = new Harmony("piri.immersive.worldgen");
		harmony.PatchAll();
	}
}
[HarmonyPatch(typeof(TileFinder))]
[HarmonyPatch(nameof(TileFinder.RandomSettlementTileFor))]
static class Patch_TileFinder_RandomSettlementTileFor {
	static int minDistance = 9;
	//Using Tiles as keys here because getting the Tile object from a WorldGrid index is easy, while the reverse is not.
	static Dictionary<Tile, int> eligibleTiles;
	static Tile _canary = null;
	static Random _rand = new Random();
	static bool Prefix(Faction faction, ref int __result, bool mustBeAutoChoosable = false, Predicate<int> extraValidator = null) {
		if(_canary == null || _canary != Find.WorldGrid[1]) {
			_canary = Find.WorldGrid[1];
			UpdateEligible();
			//Temporary fix for 5% maps/dev quicktest. TODO: Make more dynamic and add mod settings
			if(Find.WorldGrid.tiles.Count < 5000)
				minDistance = 1;
			else if(Find.WorldGrid.tiles.Count < 20000)
				minDistance = 4;
			else
				minDistance = 9;
			Log.Message(String.Format("Immersive Worldgen: Refreshed eligibleTiles. Tile count: {0}, minDistance set to: {1}", Find.WorldGrid.tiles.Count, minDistance));
		}
		//http://uniinu.blog.jp/archives/15710487.html
		//basicMemberKind under FactionDef is only applicable to player factions; it controls what kinds of pawns show up in the character selection screen. AI factions have this set to null, so we can't use this to retrieve comfy temperatures. Unfortunate.
		//The Realistic Planets mod reaches the race ThingDef through the faction leader, but because FactionGenerator.NewGenerateFaction places an initial settlement before generating a leader, we have to either give up on temperature control for the first settlement, or replace the whole NewGeneratedFaction method to call TryGenerateNewLeader() before it like that mod does. Reaching it through pawnGroupMakers should be safer.
		float minTemp = -200f, maxTemp = 200f;
		//Assuming all races need the same amount of water for now since there's no water consumption stat, at least not in vanilla.
		float minRainfall = 700f;
		
		if(faction != null && !faction.IsPlayer) {
			//Ugly, but hopefully faster than Faction.RandomPawnKind(). Maybe TODO: cache temperature values
			List<PawnGroupMaker> groupMakers = faction.def?.pawnGroupMakers;
			if(groupMakers != null && groupMakers.Count > 0 && groupMakers[0].options != null && groupMakers[0].options.Count > 0) {
				ThingDef race = groupMakers[0].options[0]?.kind?.race;
				if(race != null) {
					minTemp = race.GetStatValueAbstract(StatDefOf.ComfyTemperatureMin);
					maxTemp = race.GetStatValueAbstract(StatDefOf.ComfyTemperatureMax);
				}
			}
		}
		foreach(int index in RandomValues(eligibleTiles).Take(500)) {
			Tile t = Find.WorldGrid[index];
			int diceRoll;
			if(extraValidator != null && !extraValidator(index))
				continue;
			else {
				//If Tile temperature is outside of the race's comfy range, decide randomly whether to settle or not
				//by rolling a 100-sided die. If the roll is lower than the value from some formula, look elsewhere.
				if(t.temperature < minTemp || maxTemp < t.temperature) {
					int tempDiff = (int)(t.temperature < minTemp ? minTemp - t.temperature : t.temperature - maxTemp);
					diceRoll = _rand.Next(0, 99);
					int avoidance = (tempDiff * tempDiff / 15) + (tempDiff / 2);
					if(diceRoll < avoidance) continue;
					//else if(avoidance >= 40) Log.Message(String.Format("Dice roll (temperature) success with temp {0}, difference {1}, chance {2}%.", t.temperature, tempDiff, (100 - avoidance)));
				}
				if(t.rainfall < minRainfall && (t.Rivers == null || t.Rivers.Count == 0)) {
					int rainDiff = (int)(minRainfall - t.rainfall);
					diceRoll = _rand.Next(0, 99);
					int avoidance = rainDiff * 100 / (int)(minRainfall);
					if(diceRoll < avoidance) continue;
					//else if(avoidance >= 40) Log.Message(String.Format("Dice roll (rainfall) success with rainfall {0}, difference {1}, chance {2}%.", t.rainfall, rainDiff, (100 - avoidance)));
				}
				__result = eligibleTiles[t];
				Takeout(t);
				return false;
			}
		}
		//Fail
		Log.Warning(String.Format("Immersive Worldgen: Settlement placement failed for faction {0}.", faction?.Name ?? "null"));
		__result = 0;
		return false;
	}
	static void Takeout(Tile center) {
		Takeout(eligibleTiles[center]);
	}
	//Removes the tile and any tiles within range.
	static void Takeout(int center) {
		var visited = new HashSet<int>();
		var neighbors = new List<int>();
		//https://stackoverflow.com/a/15303419
		//Note: As of Mono 4.7.2 and Rimworld 1.4, using Queue seems to necessitate specifically referencing the mscorlib.dll, System.dll and System.Core.dll that Rimworld ships with.
		var queue = new Queue<int>();
		var nextQueue = new Queue<int>();

		queue.Enqueue(center);
		for(int distFromCenter = 0; distFromCenter < minDistance; distFromCenter++) {
			while(queue.Count > 0) {
				int current = queue.Dequeue();
				visited.Add(current);
				Find.WorldGrid.GetTileNeighbors(current, neighbors);
				foreach(int dest in neighbors) {
					if(!visited.Contains(dest)) {
						nextQueue.Enqueue(dest);
					}
				}
			}
			(queue, nextQueue) = (nextQueue, queue);
		}

		foreach(int index in visited) {
			//No need to check whether key is present.
			//https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2.remove
			//"This method returns false if key is not found in the Dictionary<TKey,TValue>."
			eligibleTiles.Remove(Find.WorldGrid[index]);
		}
	}
	//https://stackoverflow.com/a/1028324
	//https://stackoverflow.com/a/36629416
	static IEnumerable<TValue> RandomValues<TKey, TValue>(IDictionary<TKey, TValue> dict) {
		List<TValue> values = Enumerable.ToList(dict.Values);
		while(true) {
			int index = _rand.Next(0, values.Count - 1);
			yield return values[index];
			values.RemoveAt(index);
		}
	}
	internal static void UpdateEligible() {
		IEnumerable<int> tileIndices = Enumerable.Range(1, Find.WorldGrid.tiles.Count - 1).Where(x => TileFinder.IsValidTileForNewSettlement(x));
		eligibleTiles = tileIndices.ToDictionary(x => Find.WorldGrid[x], x => x);
		//It's not guaranteed that all faction bases are generated at the same time(example: when using Vanilla Expanded Framework's NewFactionSpawningUtility to spawn new factions mid-game), so Takeout all tiles with pre-existing bases here.
		foreach(Settlement s in Find.WorldObjects.Settlements) {
			Takeout(s.Tile);
		}
	}
}

/*//This didn't work. Something calls RandomSettlementTileFor before GenerateFactionsIntoWorld.
[HarmonyPatch(typeof(FactionGenerator))]
[HarmonyPatch(nameof(FactionGenerator.GenerateFactionsIntoWorld))]
static class Patch_FactionGenerator_GenerateFactionsIntoWorld {
	static bool Prefix(List<FactionDef> factions = null) {
		Patch_TileFinder_RandomSettlementTileFor.UpdateEligible();
		return true;
	}
}*/
}
