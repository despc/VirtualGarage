using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.API.Managers;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace VirtualGarage
{
    [Category("g")]
    public class VirtualGarageCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const int SaveCooldownSeconds = 10;
        private const int LoadCooldownSeconds = 10;
        private static readonly Dictionary<ulong, DateTime> CooldownSave = new Dictionary<ulong, DateTime>();
        private static readonly Dictionary<ulong, DateTime> CooldownLoad = new Dictionary<ulong, DateTime>();

        private static Config Config => Plugin.Instance.Config;

        /// <summary>
        /// The list of commands. It used to be a separate "!g" command, which Torch refused to
        /// register next to this "g" category ("Command path g is already registered").
        /// </summary>
        [Command("help", "VirtualGarage commands list.")]
        [Permission(MyPromoteLevel.None)]
        public void Help()
        {
            var manager = Context.Torch.CurrentSession?.Managers.GetManager<CommandManager>();
            if (manager == null) return;
            manager.Commands.GetNode(new List<string> { "g" }, out var node);
            if (node == null) return;
            var level = Context.Player?.PromoteLevel ?? MyPromoteLevel.Admin;
            var commands = node.Subcommands.Where(c => c.Value.Command == null || c.Value.Command.MinimumPromoteLevel <= level)
                .Select(c => "!g " + c.Key + " - " + c.Value.Command?.HelpText);
            Context.Respond("VirtualGarage:\n" + string.Join("\n", commands));
        }

        [Command("list", "List grids in garage.")]
        [Permission(MyPromoteLevel.None)]
        public void List()
        {
            var player = Context.Player;
            if (player == null) return;
            try
            {
                var files = GarageFiles.List(player.SteamUserId);
                if (files.Count == 0)
                {
                    Context.Respond(Config.NoGridsInVirtualGarageRespond);
                    return;
                }
                var text = new StringBuilder(Config.GridsInVirtualGarageRespond + " \n");
                for (var i = 0; i < files.Count; i++) text.Append(i + 1).Append(". ").Append(GarageFiles.Title(files[i])).Append('\n');
                Context.Respond(text.ToString());
            }
            catch (Exception e)
            {
                Log.Error(e, "!g list failed");
            }
        }

        [Command("save", "Save grid by looking at its position")]
        [Permission(MyPromoteLevel.None)]
        public void SaveGridToStorage(string gridName = "")
        {
            var player = Context.Player;
            if (player == null || !CooledDown(CooldownSave, player.SteamUserId, SaveCooldownSeconds)) return;
            DoSaveGrid(gridName, false);
        }

        [Command("a_save", "Admin: put a player's grid, named or looked at, into its owner's garage (the builder's when nobody owns it); free, no checks")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSaveGridToStorage(string gridName = "")
        {
            DoSaveGrid(gridName, true);
        }

        private void DoSaveGrid(string gridName, bool isAdminSave)
        {
            var player = Context.Player;
            var character = player?.Character;
            if (character == null) return;
            if (!isAdminSave && (TooMuchGravity(character) || EnemyNear(player))) return;
            Log.Warn("VirtualGarage: " + player.DisplayName + " sent !g " + (isAdminSave ? "a_save" : "save") + " " + gridName);
            try
            {
                VirtualGarageSave.Instance.SaveGrid(character, player.IdentityId, gridName, Context, isAdminSave);
            }
            catch (Exception e)
            {
                Log.Error(e, "!g save failed");
            }
        }

        /// <summary>Every player's grid in the world into its garage - the owner's, or the builder's when nobody owns it.</summary>
        [Command("a_saveall", "Admin: put every player's grid in the world into the garages")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminSaveAll()
        {
            var groups = VirtualGarageOldGridProcessor.PlayerGroups(g => true);
            Log.Warn("VirtualGarage: " + (Context.Player?.DisplayName ?? "console") + " puts all " + groups.Count + " player grid groups into the garages");
            VirtualGarageOldGridProcessor.OldGridProcessor.Enqueue(groups);
            Context.Respond(groups.Count + " grid groups go into their garages, one per frame");
        }

        /// <summary>
        /// Every grid put into any garage in the last minutes back where it was, with its owners -
        /// one at a time. A ship whose place is taken stays in the garage, a station comes back over rock and other stations.
        /// </summary>
        [Command("a_loadrecent", "Admin: take out, where they were, all grids put into the garages in the last N minutes")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminLoadRecent(int minutes)
        {
            Log.Warn("VirtualGarage: " + (Context.Player?.DisplayName ?? "console") + " takes out the grids of the last " + minutes + " minutes");
            var context = Context;
            VirtualGarageLoad.LoadRecent(minutes, file => true, text => context.Respond(text));
        }

        [Command("loadbase", "Load grid from VirtualGarage by number in the same coordinates")]
        [Permission(MyPromoteLevel.None)]
        public void LoadBase(int index) => DoLoad(index, false, true);

        [Command("load", "Load grid from VirtualGarage by number")]
        [Permission(MyPromoteLevel.None)]
        public void Load(int index, bool spawnDynamic = false) => DoLoad(index, spawnDynamic, Config.OnlyLoadBase);

        private void DoLoad(int index, bool spawnDynamic, bool original)
        {
            var player = Context.Player;
            var character = player?.Character;
            if (character == null) return;
            if (!CooledDown(CooldownLoad, player.SteamUserId, LoadCooldownSeconds)) return;

            var files = GarageFiles.List(player.SteamUserId);
            if (files.Count == 0)
            {
                Context.Respond(Config.NoGridsInVirtualGarageRespond);
                return;
            }
            if (index < 1 || index > files.Count)
            {
                Context.Respond("There is no grid number " + index + " in your garage: see !g list");
                return;
            }
            var file = files[index - 1];

            // taking out is free: the garage is paid for when a grid is put into it (SavePcuCost)
            if (!original && TooMuchGravity(character)) return;
            if (EnemyNear(player)) return;

            var context = Context;
            var identityId = player.IdentityId;
            Log.Info("VirtualGarage: " + player.DisplayName + " takes out " + file);
            VirtualGarageLoad.Load(file, identityId, new VirtualGarageLoad.Placement
                {
                    Original = original,
                    Around = character.GetPosition(),
                    Ignore = VirtualGarageLoad.SafeZoneAround(character),
                    ConvertToDynamic = spawnDynamic,
                },
                grids =>
                {
                    var main = VirtualGarageLoad.MainGrid(grids);
                    if (main.BigOwners.Count > 0) VirtualGarageLoad.AddGps(main, identityId);
                    foreach (var grid in grids)
                        context.Respond(Config.GridSpawnedToWorldRespond + " :" + grid.DisplayName);
                },
                why =>
                {
                    Log.Info("VirtualGarage: " + file + " not taken out: " + why);
                    context.Respond(why == "no room" ? Config.NoRoomToSpawnRespond : "Cannot take the grid out: " + why);
                });
        }

        // ------------------------------------------------------------------ checks

        private bool CooledDown(Dictionary<ulong, DateTime> calls, ulong steamId, int seconds)
        {
            if (calls.TryGetValue(steamId, out var last) && last.AddSeconds(seconds) > DateTime.Now)
            {
                Context.Respond($"try again after {seconds} sec");
                return false;
            }
            calls[steamId] = DateTime.Now;
            return true;
        }

        private bool TooMuchGravity(IMyCharacter character)
        {
            MyGravityProviderSystem.CalculateNaturalGravityInPoint(character.GetPosition(), out var gravity);
            if (gravity <= Config.MinAllowedGravityToLoad) return false;
            Context.Respond($"{Config.VirtualGarageNotAllowedInGravityMoreThanResponce} > {Config.MinAllowedGravityToLoad}");
            return true;
        }

        private bool EnemyNear(IMyPlayer player)
        {
            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            var at = player.Character.GetPosition();
            foreach (var other in players)
            {
                if (other.IsBot || other.GetRelationTo(player.IdentityId) != MyRelationsBetweenPlayerAndBlock.Enemies) continue;
                var character = other.Character;
                if (character == null || character.IsDead || !character.IsPlayer) continue;
                if (Vector3D.Distance(other.GetPosition(), at) >= Config.EnemyPlayerInRange) continue;
                Log.Warn("VirtualGarage: enemy " + other.DisplayName + " near " + player.DisplayName);
                Context.Respond(Config.EnemyNearByChatRespond);
                return true;
            }
            return false;
        }
    }
}
