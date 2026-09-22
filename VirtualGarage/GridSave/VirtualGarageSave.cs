using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Definitions;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using Torch.Commands;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRageMath;

namespace VirtualGarage
{
    /// <summary>
    /// Putting grids into the garage: a player's (!g save, !g a_save) and the grids of players gone
    /// for too long (<see cref="VirtualGarageOldGridProcessor"/>).
    ///
    /// A grid is taken with everything joined to it by rotors, pistons and hinges. Its copy is made
    /// and the grids are removed in the same frame - nothing can be taken out of a cargo container
    /// between the two - and the file is written in the background. Should writing fail, the grids
    /// come back where they were.
    /// </summary>
    public class VirtualGarageSave
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static VirtualGarageSave Instance = new VirtualGarageSave();

        /// <summary>!g save / !g a_save: the grid named, or the one the player looks at.</summary>
        public void SaveGrid(IMyCharacter character, long identityId, string gridName, CommandContext context, bool isAdminSave = false)
        {
            var config = Plugin.Instance.Config;
            foreach (var group in Candidates(character, gridName))
            {
                var main = group[0];
                var why = isAdminSave ? null : WhyNot(group, identityId, character);
                if (why != null)
                {
                    context.Respond(why + " " + main.DisplayName);
                    // a grid that may not be saved hides nothing: look at the next one behind it
                    continue;
                }

                var owner = identityId;
                if (isAdminSave)
                {
                    owner = OwnerOf(group);
                    if (owner == 0)
                    {
                        context.Respond("No owner found for " + main.DisplayName);
                        return;
                    }
                }

                var pcu = group.Sum(g => g.BlocksPCU);
                var cost = (long)pcu * config.SavePcuCost;
                if (!isAdminSave && cost > 0)
                {
                    context.Player.TryGetBalanceInfo(out var balance);
                    if (balance < cost)
                    {
                        context.Respond(config.NotEnoughMoneyMessage);
                        return;
                    }
                }

                context.Respond(config.SavingGridResponce + " " + main.DisplayName);
                if (Store(owner, group) == null)
                {
                    context.Respond("Could not save " + main.DisplayName);
                    return;
                }
                if (!isAdminSave && cost > 0) context.Player.RequestChangeBalance(-cost);
                context.Respond("Grid/Cтруктура " + main.DisplayName + " " + config.GridSavedToVirtualGarageResponce);
                return;
            }
            context.Respond(gridName != string.Empty
                ? "No such grid exist with name '" + gridName + "' ."
                : config.NoGridInViewResponce);
        }

        /// <summary>The groups to try, nearest first: the grids with that name, or those on the player's line of sight.</summary>
        private static IEnumerable<List<MyCubeGrid>> Candidates(IMyCharacter character, string gridName)
        {
            var seen = new HashSet<MyCubeGrid>();
            IEnumerable<MyCubeGrid> grids;
            if (gridName != string.Empty)
            {
                grids = MyEntities.GetEntities().OfType<MyCubeGrid>()
                    .Where(g => !g.IsPreview && !g.MarkedForClose && g.DisplayName.Equals(gridName, StringComparison.InvariantCultureIgnoreCase))
                    .OrderBy(g => Vector3D.DistanceSquared(g.PositionComp.GetPosition(), character.GetPosition()))
                    .ToList();
            }
            else
            {
                var head = character.GetHeadMatrix(true, true, false);
                var from = head.Translation + head.Forward * 0.5f;
                var to = head.Translation + head.Forward * 5000.5f;
                var hits = new List<MyPhysics.HitInfo>();
                MyPhysics.CastRay(from, to, hits, 15);
                grids = hits.Select(h => h.HkHitInfo.GetHitEntity() as MyCubeGrid).Where(g => g != null && !g.IsPreview).ToList();
            }
            foreach (var grid in grids)
            {
                if (!seen.Add(grid)) continue;
                var group = Group(grid);
                foreach (var member in group) seen.Add(member);
                yield return group;
            }
        }

        /// <summary>
        /// Whose garage a group goes to: the owner of its biggest grid, of any grid, or - for a
        /// group nobody owns (armor only) - whoever built most of its blocks. 0 when nobody.
        /// </summary>
        public static long OwnerOf(List<MyCubeGrid> group)
        {
            var owner = group[0].BigOwners.FirstOrDefault();
            if (owner != 0) return owner;
            owner = group.SelectMany(g => g.BigOwners).FirstOrDefault(o => o != 0);
            if (owner != 0) return owner;
            return group.SelectMany(g => g.CubeBlocks).Select(b => b.BuiltBy).Where(b => b != 0)
                .GroupBy(b => b).OrderByDescending(b => b.Count()).Select(b => b.Key).FirstOrDefault();
        }

        /// <summary>Everybody the group belongs to: its owners, or its builders when nobody owns it.</summary>
        public static List<long> PeopleOf(List<MyCubeGrid> group)
        {
            var owners = group.SelectMany(g => g.BigOwners).Where(o => o != 0).Distinct().ToList();
            return owners.Count > 0
                ? owners
                : group.SelectMany(g => g.CubeBlocks).Select(b => b.BuiltBy).Where(b => b != 0).Distinct().ToList();
        }

        /// <summary>A grid and everything joined to it by rotors, pistons and hinges; the biggest first.</summary>
        public static List<MyCubeGrid> Group(MyCubeGrid grid) =>
            MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(grid)
                .OrderByDescending(g => g.BlocksCount).ToList();

        /// <summary>Why a player may not put a group into the garage, or null.</summary>
        private static string WhyNot(List<MyCubeGrid> group, long identityId, IMyCharacter character)
        {
            var config = Plugin.Instance.Config;
            // every grid of the group, not only the first: a player's rotor head on somebody else's ship would take the ship along
            if (group.Any(g => g.BigOwners.Count > 0 && !g.BigOwners.Contains(identityId)))
                return config.OnlyOwnerCanSaveResponce;
            if (group.All(g => g.BigOwners.Count == 0))
                return config.OnlyOwnerCanSaveResponce;
            if (Vector3D.DistanceSquared(group[0].PositionComp.GetPosition(), character.GetPosition()) > config.MaxRangeToGrid * config.MaxRangeToGrid)
                return config.GridToFarResponce;
            if (group.Sum(g => g.BlocksPCU) > config.MaxPCUForGridOnSave)
                return config.GridPCUOverLimitResponce;
            if (group.Sum(g => g.BlocksCount) > config.MaxBlocksForGridOnSave)
                return config.GridBlocksOverLimitResponce;
            return null;
        }

        /// <summary>
        /// Puts a group into the owner's garage: pilots out, programs stopped, drills off, the copy
        /// made and the grids removed - on the game thread, in one go - and the file written in the
        /// background. Returns the file, or null when nothing was done.
        /// </summary>
        public static string Store(long ownerIdentityId, List<MyCubeGrid> group)
        {
            var steamId = MyAPIGateway.Players.TryGetSteamId(ownerIdentityId);
            if (steamId == 0)
            {
                Log.Warn("Not putting " + group[0].DisplayName + " into the garage: its owner " + ownerIdentityId + " has no Steam id");
                return null;
            }

            var obs = new List<MyObjectBuilder_CubeGrid>();
            foreach (var grid in group)
            {
                Quiet(grid);
                obs.Add((MyObjectBuilder_CubeGrid)grid.GetObjectBuilder(true));
            }
            var pcu = group.Sum(g => g.BlocksPCU);
            var blocks = group.Sum(g => g.BlocksCount);
            var file = GarageFiles.NewFile(steamId, group[0].DisplayName, pcu, blocks);
            var definitions = Blueprint(obs, group[0].DisplayName, file);
            foreach (var grid in group) grid.Close();

            Task.Run(() =>
            {
                bool written;
                try
                {
                    // written aside and renamed: the garage shows the file only once it is whole
                    var partial = file + GarageFiles.Partial;
                    written = MyObjectBuilderSerializerKeen.SerializeXML(partial, false, definitions);
                    if (written) GarageFiles.Written(file, partial);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Writing " + file + " failed");
                    written = false;
                }
                if (written)
                {
                    Log.Info("Put into the garage: " + file);
                    return;
                }
                GarageFiles.Abandoned(file);
                // the grids are gone from the world and not on disk: put them back where they were
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    VirtualGarageLoad.Spawn(obs,
                        grids => Log.Error("Could not write " + file + "; the grids were put back into the world"),
                        why => Log.Error("Could not write " + file + " nor put the grids back (" + why + "): " + group[0].DisplayName + " is lost")));
            });
            return file;
        }

        /// <summary>Nobody sits in it, no program runs, no drill turns.</summary>
        private static void Quiet(MyCubeGrid grid)
        {
            foreach (var block in grid.GetFatBlocks())
            {
                try
                {
                    switch (block)
                    {
                        case MyCockpit cockpit:
                            cockpit.RemovePilot();
                            break;
                        case MyProgrammableBlock programmable:
                            Plugin.m_myProgrammableBlockKillProgramm?.Invoke(programmable, new object[] { MyProgrammableBlock.ScriptTerminationReason.None });
                            break;
                        case MyShipDrill drill:
                            drill.Enabled = false;
                            break;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Quieting " + block.DisplayNameText + " before saving failed");
                }
            }
        }

        private static MyObjectBuilder_Definitions Blueprint(List<MyObjectBuilder_CubeGrid> obs, string name, string file)
        {
            var blueprint = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            blueprint.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)),
                MyUtils.StripInvalidChars(System.IO.Path.GetFileNameWithoutExtension(file)));
            blueprint.CubeGrids = obs.ToArray();
            blueprint.DLCs = DLCs(blueprint.CubeGrids);
            blueprint.RespawnShip = false;
            blueprint.DisplayName = MyGameService.UserName;
            blueprint.OwnerSteamId = Sync.MyId;
            blueprint.CubeGrids[0].DisplayName = name;
            var definitions = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Definitions>();
            definitions.ShipBlueprints = new[] { blueprint };
            return definitions;
        }

        private static string[] DLCs(MyObjectBuilder_CubeGrid[] grids)
        {
            var dlcs = new HashSet<string>();
            foreach (var grid in grids)
                foreach (var block in grid.CubeBlocks)
                {
                    var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(block);
                    if (definition?.DLCs != null) dlcs.UnionWith(definition.DLCs);
                }
            return dlcs.ToArray();
        }
    }
}
