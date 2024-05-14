using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Scripts.Shared;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

namespace VirtualGarage
{
    [Category("g")]
    public class VirtualGarageCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private int saveCooldown = 10;
        private int loadCooldown = 10;
        public static Dictionary<ulong, DateTime> CooldownSave = new Dictionary<ulong, DateTime>();
        public static Dictionary<ulong, DateTime> CooldownLoad = new Dictionary<ulong, DateTime>();
        
        [Command("list", "List grids in garage.", null)]
        [Permission(MyPromoteLevel.None)]
        public void List()
        {
            try
            {
                IMyPlayer player = Context.Player;
                if (player == null)
                    return;
                long identityId = player.IdentityId;
                if (!Directory.Exists(Path.Combine(Plugin.Instance.Config.PathToVirtualGarage,
                        MyAPIGateway.Players.TryGetSteamId(identityId).ToString())))
                {
                    Context.Respond(Plugin.Instance.Config.NoGridsInVirtualGarageRespond, (string) null,
                        (string) null);
                }
                else
                {
                    string[] files =
                        Directory.GetFiles(
                            Path.Combine(Plugin.Instance.Config.PathToVirtualGarage,
                                Context.Player.SteamUserId.ToString()), "*.sbc");
                    List<string> all =
                        new List<string>((IEnumerable<string>) files).FindAll(
                            (Predicate<string>) (s => s.EndsWith(".sbc")));
                    if (files.Length == 0 || all.Count<string>() == 0)
                    {
                        Context.Respond(Plugin.Instance.Config.NoGridsInVirtualGarageRespond, (string) null,
                            (string) null);
                    }
                    else
                    {
                        List<string> resultListFiles =
                            new List<string>();
                        all.SortNoAlloc<string>((Comparison<string>) ((s, s1) =>
                            string.Compare(s, s1, StringComparison.Ordinal)));
                        all.ForEach((Action<string>) (s => resultListFiles.Add(s.Replace(".sbc", ""))));
                        string str = Plugin.Instance.Config.GridsInVirtualGarageRespond + " \n";
                        for (int index = 1; index < resultListFiles.Count + 1; ++index)
                            str = string.Format("{0}{1}. {2}\n", (object) str, (object) index,
                                (object) Path.GetFileName(resultListFiles[index - 1]));
                        Context.Respond(str, (string) null, (string) null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error<Exception>(ex);
            }
        }

        [Command("save", "Save grid by looking at its position", null)]
        [Permission(MyPromoteLevel.None)]
        public void SaveGridToStorage(string gridName = "")
        {
            var playerSteamUserId = Context.Player.SteamUserId;
            if (CooldownSave.TryGetValue(playerSteamUserId, out DateTime lastCall))
            {
                
                if (lastCall.AddSeconds(saveCooldown) > DateTime.Now)
                {
                    Context.Respond($"try again after {saveCooldown} sec", (string) null,
                        (string) null);
                    return;
                }
            }
            CooldownSave[Context.Player.SteamUserId] = DateTime.Now; 
            DoSaveGrid(gridName);
        }
        
        [Command("a_save", "Save any grid by looking at its position", null)]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSaveGridToStorage(string gridName = "")
        {
            DoSaveGrid(gridName, true);
        }

        private void DoSaveGrid(string gridName, bool isAdminSave = false)
        {
            IMyPlayer player = Context.Player;
            if (player == null)
                return;
            
            if (isAdminSave)
            {
                Log.Warn("VirtualGarage:" + Context.Player.DisplayName + " send *!g a_save " +
                         gridName + "*");
                VirtualGarageSave.Instance.SaveGrid(player.Character, player.IdentityId, gridName, Context, true);
                return;
            }
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            
            float naturalGravityMultiplier;
            MyGravityProviderSystem.CalculateNaturalGravityInPoint(player.Character.GetPosition(),
                out naturalGravityMultiplier);
            
            if (naturalGravityMultiplier >
                (double) Plugin.Instance.Config.MinAllowedGravityToLoad)
            {
                Context.Respond(
                    string.Format("{0} > {1}",
                        Plugin.Instance.Config.VirtualGarageNotAllowedInGravityMoreThanResponce,
                        Plugin.Instance.Config.MinAllowedGravityToLoad));
                return;
            }
            
            foreach (IMyPlayer myPlayer in players)
            {
                if (myPlayer.GetRelationTo(player.IdentityId) == MyRelationsBetweenPlayerAndBlock.Enemies)
                {
                    IMyCharacter character = myPlayer.Character;
                    if (!myPlayer.IsBot && character != null && !character.IsDead && character.IsPlayer &&
                        Vector3D.Distance(myPlayer.GetPosition(), player.Character.GetPosition()) <
                        Plugin.Instance.Config.EnemyPlayerInRange)
                    {
                        Log.Warn("Enemy:" + myPlayer.DisplayName + myPlayer.IsBot.ToString());
                        Context.Respond(Plugin.Instance.Config.EnemyNearByChatRespond, (string) null,
                            (string) null);
                        return;
                    }
                }
            }

            try
            {
                if (player.Character == null)
                    return;
                Log.Warn("VirtualGarage:" + Context.Player.DisplayName + " send *!g save " +
                                               gridName + "*");
                VirtualGarageSave.Instance.SaveGrid(player.Character, player.IdentityId, gridName, Context);
            }
            catch (Exception ex)
            {
                Log.Error<Exception>(ex);
            }
        }

        [Command("loadbase", "Load grid from VirtualGarage by number in the same coordinates", null)]
        [Permission(MyPromoteLevel.None)]
        public void LoadBase(int index) => this.Load(index, loadbase: true);

        [Command("load", "Load grid from VirtualGarage by number", null)]
        [Permission(MyPromoteLevel.None)]
        public void Load(int index, bool spawnDynamic = false, bool loadbase = false)
        {
            var playerSteamUserId = Context.Player.SteamUserId;
            if (CooldownLoad.TryGetValue(playerSteamUserId, out DateTime lastCall))
            {
                
                if (lastCall.AddSeconds(loadCooldown) > DateTime.Now)
                {
                    Context.Respond($"try again after {loadCooldown} sec", (string) null,
                        (string) null);
                    return;
                }
            }
            CooldownLoad[Context.Player.SteamUserId] = DateTime.Now; 
            if (Plugin.Instance.Config.OnlyLoadBase)
            {
                loadbase = true;
            }
            if (!Directory.Exists(Path.Combine(Plugin.Instance.Config.PathToVirtualGarage,
                    Context.Player.SteamUserId.ToString())))
            {
                Context.Respond(Plugin.Instance.Config.NoGridsInVirtualGarageRespond, (string) null,
                    (string) null);
            }
            else
            {
                Path.Combine(Plugin.Instance.Config.PathToVirtualGarage, Context.Player.SteamUserId.ToString());
                string[] files =
                    Directory.GetFiles(
                        Path.Combine(Plugin.Instance.Config.PathToVirtualGarage,
                            Context.Player.SteamUserId.ToString()), "*.sbc");
                List<string> all = new List<string>(files).FindAll(
                        (Predicate<string>) (s => s.EndsWith(".sbc")));
                if (files.Length == 0 || all.Count<string>() == 0)
                {
                    Context.Respond(Plugin.Instance.Config.NoGridsInVirtualGarageRespond);
                }
                else
                {
                    var cost = 0;
                    all.SortNoAlloc((Comparison<string>) ((s, s1) =>
                        string.Compare(s, s1, StringComparison.Ordinal)));
                    string str = all[index - 1];
                    
                    if (Plugin.Instance.Config.LoadPcuCost != 0)
                    {
                        var pcu = Int32.Parse(str.Substring(str.IndexOf("_P-") + 3, str.IndexOf("_B-") - str.IndexOf("_P-") - 3));
                        cost = pcu * Plugin.Instance.Config.LoadPcuCost;
                        Context.Player.TryGetBalanceInfo(out long balance);
                        if (balance < cost)
                        {
                            Context.Respond(Plugin.Instance.Config.NotEnoughMoneyMessage, (string) null,
                                (string) null);
                            return;
                        }
                    }
                    IMyPlayer player = Context.Player;
                    if (player == null)
                        return;
                    long identityId = player.IdentityId;
                    IMyCharacter character1 = player.Character;
                    float naturalGravityMultiplier;
                    MyGravityProviderSystem.CalculateNaturalGravityInPoint(character1.GetPosition(),
                        out naturalGravityMultiplier);
                    if (!loadbase && (double) naturalGravityMultiplier >
                        (double) Plugin.Instance.Config.MinAllowedGravityToLoad)
                    {
                        Context.Respond(
                            string.Format("{0} > {1}",
                                Plugin.Instance.Config.VirtualGarageNotAllowedInGravityMoreThanResponce,
                                Plugin.Instance.Config.MinAllowedGravityToLoad), (string) null, (string) null);
                    }
                    else
                    {
                        List<IMyPlayer> players = new List<IMyPlayer>();
                        MyAPIGateway.Players.GetPlayers(players);
                        foreach (IMyPlayer myPlayer in players)
                        {
                            if (myPlayer.GetRelationTo(identityId) == MyRelationsBetweenPlayerAndBlock.Enemies)
                            {
                                IMyCharacter character2 = myPlayer.Character;
                                if (!myPlayer.IsBot && character2 != null && !character2.IsDead &&
                                    character2.IsPlayer &&
                                    Vector3D.Distance(myPlayer.GetPosition(), character1.GetPosition()) <
                                    Plugin.Instance.Config.EnemyPlayerInRange)
                                {
                                    Log.Warn("Enemy:" + myPlayer.DisplayName +
                                                                   myPlayer.IsBot.ToString());
                                    Context.Respond(Plugin.Instance.Config.EnemyNearByChatRespond, (string) null,
                                        (string) null);
                                    return;
                                }
                            }
                        }

                        Vector3D? spawnPosition = new Vector3D?();
                        if (!loadbase)
                            spawnPosition = VirtualGarageLoad.SpawnPosition(character1);
                        if (loadbase || spawnPosition.HasValue)
                        {
                            Context.Player.RequestChangeBalance(-cost);
                            VirtualGarageLoad.DoSpawnGrids(identityId, str, spawnPosition,
                                (Delegate.AddListenerDelegate)((grid, identity, spawned, totalGrids) =>
                                {
                                    spawned.Add(grid);
                                    if (totalGrids == spawned.Count)
                                    {
                                        MyCubeGrid maingrid = null;
                                        foreach (var g in spawned)
                                        {
                                            if (maingrid == null)
                                            {
                                                maingrid = g;
                                            }
                                            else
                                            {
                                                if (g.BlocksCount > maingrid.BlocksCount)
                                                {
                                                    maingrid = g;
                                                }
                                            }

                                            MyAPIGateway.Entities.AddEntity(g, true);
                                            if (!g.IsStatic)
                                            {
                                                if (Voxels.IsGridInsideVoxel(g))
                                                {
                                                    g.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                                    g.ConvertToStatic();
                                                    MyMultiplayer.RaiseEvent(grid, x => x.ConvertToStatic);
                                                    foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                                                    {
                                                        MyMultiplayer.RaiseEvent(grid, x => x.ConvertToStatic, new EndpointId(player.Id.SteamId));
                                                    }
                                                }
                                            }
                                        }

                                        if (grid.BigOwners.Count > 0)
                                        {
                                            VirtualGarageLoad.AddGps(maingrid, identity);
                                        }
                                        MyAPIGateway.Parallel.StartBackground(() =>
                                        {
                                            MyAPIGateway.Parallel.Sleep(5000); 
                                            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                                            {
                                                FixShipLogic.DoFixShip(maingrid);
                                            });
                                        });
                                        
                                    }
                                }), spawnDynamic);
                            foreach (MyObjectBuilder_CubeGrid cubeGrid in MyBlueprintUtils.LoadPrefab(str)
                                         .ShipBlueprints[0].CubeGrids)
                            {
                                Context.Respond(
                                    Plugin.Instance.Config.GridSpawnedToWorldRespond + " :" + cubeGrid?.DisplayName,
                                    (string) null, (string) null);
                                Log.Info("Структура: " + cubeGrid?.DisplayName +
                                         " перенесена в мир");
                            }

                            Task.Run(() =>
                            {
                                while (true)
                                {
                                    try
                                    {
                                        var spawnedSuffix = "_spawned_unsaved";
                                        if (File.Exists(str + spawnedSuffix))
                                            File.Delete(str + spawnedSuffix);
                                        File.Move(str, str + spawnedSuffix);
                                        break;
                                    }
                                    catch (Exception e)
                                    {
                                        Log.Error("Rename exception, retry", e);
                                    }

                                    Thread.Sleep(50);
                                }
                            });
                        }
                        else
                        {
                            Context.Respond(Plugin.Instance.Config.NoRoomToSpawnRespond, (string) null,
                                (string) null);
                            Log.Info("Слишком много всего вокруг, найдите место посвободнее " +
                                                           str);
                        }
                    }
                }
            }
        }
    }
}