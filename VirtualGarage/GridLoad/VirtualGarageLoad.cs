using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.GUI;
using Sandbox.ModAPI;
using Scripts.Shared;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace VirtualGarage
{
    /// <summary>
    /// Taking grids out of the garage.
    ///
    /// The blueprint is read in the background; everything that touches the world happens on the
    /// game thread, the way the game pastes a blueprint: the entity ids are remapped, every grid of
    /// the group is created (in parallel, by the game's own CreateFromObjectBuilderParallel) and
    /// they are all added to the world in the same frame, so the rotors, pistons and connectors
    /// between them are whole when the clients are sent the group. A grid created this way needs no
    /// "fix" afterwards: the old code closed and re-created the whole group five seconds after
    /// every load, which the clients saw as the ship vanishing and coming back.
    ///
    /// The file is marked as taken out (and the price paid) only once the grids are in the world.
    /// </summary>
    public static class VirtualGarageLoad
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const int SpawnTimeoutSeconds = 60;

        // files being taken out now: the same file is not taken out twice at once
        private static readonly HashSet<string> Loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where the grids go.</summary>
        public sealed class Placement
        {
            /// <summary>Where they were saved (!g loadbase), if nothing else is there now.</summary>
            public bool Original;

            /// <summary>Otherwise, a free spot within the configured radius of this point (the player).</summary>
            public Vector3D Around;

            /// <summary>Entities the free spot may overlap: the safe zone the player stands in.</summary>
            public MyEntity Ignore;

            /// <summary>The player asked for the main grid to come out dynamic.</summary>
            public bool ConvertToDynamic;

            /// <summary>The blocks keep the owners they were saved with (an admin putting grids back), whatever ChangeOwner says.</summary>
            public bool KeepOwnership;
        }

        /// <summary>
        /// Takes a garage file out: <paramref name="spawned"/> with the grids once they are in the
        /// world, or <paramref name="failed"/> with what went wrong - both on the game thread. The
        /// file stays in the garage if anything fails.
        /// </summary>
        public static void Load(string file, long ownerIdentityId, Placement placement, Action<List<MyCubeGrid>> spawned, Action<string> failed)
        {
            lock (Loading)
            {
                if (!Loading.Add(file))
                {
                    failed("this grid is already being taken out");
                    return;
                }
            }

            Task.Run(() =>
            {
                MyObjectBuilder_CubeGrid[] grids = null;
                string error = null;
                try
                {
                    grids = MyBlueprintUtils.LoadPrefab(file)?.ShipBlueprints?.FirstOrDefault()?.CubeGrids;
                    if (grids == null || grids.Length == 0) error = "the garage file is empty or damaged";
                }
                catch (Exception e)
                {
                    Log.Error(e, "Reading " + file + " failed");
                    error = "the garage file could not be read";
                }

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    void Fail(string why)
                    {
                        lock (Loading) Loading.Remove(file);
                        failed(why);
                    }

                    if (error != null)
                    {
                        Fail(error);
                        return;
                    }
                    try
                    {
                        var list = grids.ToList();
                        error = Prepare(list, ownerIdentityId, placement);
                        if (error != null)
                        {
                            Fail(error);
                            return;
                        }
                        Spawn(list, result =>
                        {
                            GarageFiles.MarkTakenOut(file);
                            lock (Loading) Loading.Remove(file);
                            Log.Info("Taken out of the garage: " + file + " (" + result.Count + " grids)");
                            spawned(result);
                        }, Fail);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Taking " + file + " out failed");
                        Fail("the grid could not be spawned");
                    }
                });
            });
        }

        /// <summary>
        /// Every grid put into any garage in the last <paramref name="minutes"/> (and passing
        /// <paramref name="which"/>) back where it was, with the owners it was saved with, one at a
        /// time, oldest first. A ship whose place is taken stays in the garage; a station comes back
        /// over rock and other stations. <paramref name="report"/> hears how it went. Game thread.
        /// </summary>
        public static void LoadRecent(int minutes, Func<string, bool> which, Action<string> report)
        {
            var since = DateTime.Now.AddMinutes(-minutes);
            var files = GarageFiles.All().Where(f => which(f.File) && System.IO.File.GetLastWriteTime(f.File) >= since)
                .OrderBy(f => System.IO.File.GetLastWriteTime(f.File)).ToList();
            if (files.Count == 0)
            {
                report("Nothing was put into the garages in the last " + minutes + " minutes");
                return;
            }
            report("Taking out " + files.Count + " grids");
            var done = 0;
            var failed = new List<string>();

            void Next(int i)
            {
                if (i >= files.Count)
                {
                    report("Taken out " + done + " of " + files.Count + (failed.Count > 0 ? "; not: " + string.Join("; ", failed) : ""));
                    return;
                }
                var (steamId, file) = files[i];
                var identity = Sandbox.Game.World.MySession.Static.Players.TryGetIdentityId(steamId);
                Load(file, identity, new Placement { Original = true, KeepOwnership = true },
                    grids =>
                    {
                        done++;
                        Next(i + 1);
                    },
                    why =>
                    {
                        failed.Add(GarageFiles.Title(file) + " (" + why + ")");
                        Next(i + 1);
                    });
            }

            Next(0);
        }

        /// <summary>
        /// Spawns a group of grids the way the game pastes a blueprint. Game thread;
        /// <paramref name="done"/> and <paramref name="failed"/> are called on the game thread.
        /// </summary>
        public static void Spawn(List<MyObjectBuilder_CubeGrid> obs, Action<List<MyCubeGrid>> done, Action<string> failed)
        {
            MyEntities.RemapObjectBuilderCollection(obs);
            var created = new MyCubeGrid[obs.Count];
            var left = obs.Count;
            var finished = 0;

            void Finish()
            {
                if (Interlocked.Exchange(ref finished, 1) != 0) return;
                if (created.Any(g => g == null))
                {
                    foreach (var grid in created) grid?.Close();
                    failed("the grid could not be created");
                    return;
                }
                try
                {
                    // all of them in the same frame, as the game does after a paste
                    foreach (var grid in created) MyEntities.Add(grid);
                    AfterSpawn(created);
                    done(created.ToList());
                }
                catch (Exception e)
                {
                    Log.Error(e, "Adding spawned grids failed");
                    failed("the grid could not be added to the world");
                }
            }

            for (var i = 0; i < obs.Count; i++)
            {
                var index = i;
                MyAPIGateway.Entities.CreateFromObjectBuilderParallel(obs[i], false, entity =>
                {
                    created[index] = entity as MyCubeGrid;
                    if (Interlocked.Decrement(ref left) == 0) MyAPIGateway.Utilities.InvokeOnGameThread(Finish);
                });
            }

            // a grid the game could not create never calls back: do not wait for it forever
            Task.Delay(TimeSpan.FromSeconds(SpawnTimeoutSeconds)).ContinueWith(_ =>
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    if (Volatile.Read(ref finished) != 0) return;
                    Log.Error("Spawning a garage grid timed out: " + created.Count(g => g != null) + " of " + created.Length + " grids created");
                    Finish();
                }));
        }

        /// <summary>A dynamic main grid that came out stuck in rock is made a station, on the server and the clients.</summary>
        private static void AfterSpawn(MyCubeGrid[] grids)
        {
            var main = MainGrid(grids);
            if (main.IsStatic || main.Physics == null || !Voxels.IsGridInsideVoxel(main)) return;
            main.Physics.SetSpeeds(Vector3.Zero, Vector3.Zero);
            // the game's own event: runs here and is broadcast to the clients
            MyMultiplayer.RaiseEvent(main, x => x.ConvertToStatic);
        }

        public static MyCubeGrid MainGrid(IEnumerable<MyCubeGrid> grids) => grids.OrderByDescending(g => g.BlocksCount).First();

        // ------------------------------------------------------------------ before the spawn

        /// <summary>Owners, static or dynamic, the place. Returns why the grids cannot come out, or null.</summary>
        private static string Prepare(List<MyObjectBuilder_CubeGrid> grids, long ownerIdentityId, Placement placement)
        {
            var config = Plugin.Instance.Config;
            if (!placement.KeepOwnership) RemapOwnership(grids, ownerIdentityId);

            // Static or dynamic, for the first (main) grid when it is a large one. ConvertToStatic
            // wins when both are set; the player's "dynamic" asks for dynamic.
            var main = grids[0];
            if (main.GridSizeEnum == MyCubeSize.Large)
            {
                if (config.ConvertToStatic && !placement.ConvertToDynamic)
                {
                    main.IsStatic = true;
                    main.IsUnsupportedStation = true;
                }
                else if (config.ConvertToDynamic || placement.ConvertToDynamic)
                {
                    main.IsStatic = false;
                    main.IsUnsupportedStation = false;
                }
            }

            foreach (var grid in grids)
            {
                grid.CreatePhysics = true;
                grid.LinearVelocity = new SerializableVector3();
                grid.AngularVelocity = new SerializableVector3();
                foreach (var block in grid.CubeBlocks)
                    if (block is MyObjectBuilder_Drill drill)
                        drill.Enabled = false;
            }

            var bounds = Bounds(grids);
            if (placement.Original)
            {
                // A station comes back whatever rock or other stations are there now - neither
                // moves, nothing gets thrown about. A ship is not put into anything.
                var occupied = OtherGridsIn(bounds, ignoreStatic: main.IsStatic);
                return occupied == null ? null : "the place is taken by " + occupied;
            }

            var sphere = BoundingSphereD.CreateFromBoundingBox(bounds);
            var around = new BoundingSphereD(placement.Around, config.MaxSpawnRadius);
            var random = new Random();
            var candidate = around.RandomToUniformPointOnSphere(random.NextDouble(), random.NextDouble());
            var free = MyEntities.FindFreePlaceCustom(candidate, (float)sphere.Radius, ignoreEnt: placement.Ignore);
            if (!free.HasValue) return "no room";
            var shift = free.Value - sphere.Center;
            foreach (var grid in grids)
            {
                if (!grid.PositionAndOrientation.HasValue) continue;
                var at = grid.PositionAndOrientation.Value;
                at.Position = (Vector3D)at.Position + shift;
                grid.PositionAndOrientation = at;
            }
            return null;
        }

        /// <summary>
        /// Owner and builder, as configured. The sharing of a block changes only with its owner: a
        /// block given to the player is shared with the faction, a block that keeps its owner keeps
        /// its sharing too.
        /// </summary>
        public static void RemapOwnership(IEnumerable<MyObjectBuilder_CubeGrid> grids, long owner)
        {
            var config = Plugin.Instance.Config;
            foreach (var grid in grids)
                foreach (var block in grid.CubeBlocks)
                {
                    if (config.ChangeBuiltBy) block.BuiltBy = owner;
                    if (config.ChangeOwner && block.Owner != 0)
                    {
                        block.Owner = owner;
                        block.ShareMode = MyOwnershipShareModeEnum.Faction;
                    }
                }
        }

        /// <summary>The world box the grids take, from their blocks and where they were saved.</summary>
        public static BoundingBoxD Bounds(IEnumerable<MyObjectBuilder_CubeGrid> grids)
        {
            var box = BoundingBoxD.CreateInvalid();
            foreach (var grid in grids)
            {
                if (!grid.PositionAndOrientation.HasValue || grid.CubeBlocks.Count == 0) continue;
                var size = MyDefinitionManager.Static.GetCubeSize(grid.GridSizeEnum);
                var min = new Vector3I(int.MaxValue);
                var max = new Vector3I(int.MinValue);
                foreach (var block in grid.CubeBlocks)
                {
                    Vector3I blockMin = block.Min;
                    var extent = Vector3I.Zero;
                    var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(block);
                    if (definition != null)
                    {
                        new MyBlockOrientation(block.BlockOrientation.Forward, block.BlockOrientation.Up).GetMatrix(out var rotation);
                        extent = Vector3I.Abs(Vector3I.Round(Vector3.TransformNormal((Vector3)definition.Size, rotation))) - Vector3I.One;
                    }
                    min = Vector3I.Min(min, blockMin);
                    max = Vector3I.Max(max, blockMin + extent);
                }
                var local = new BoundingBoxD((Vector3D)min * size - size / 2, (Vector3D)max * size + size / 2);
                box.Include(local.TransformFast(grid.PositionAndOrientation.Value.GetMatrix()));
            }
            return box;
        }

        /// <summary>The name of a grid already standing in the box, or null. Voxels never count; static grids not either when <paramref name="ignoreStatic"/>.</summary>
        private static string OtherGridsIn(BoundingBoxD box, bool ignoreStatic)
        {
            var found = new List<MyEntity>();
            MyEntities.GetTopMostEntitiesInBox(ref box, found);
            return found.OfType<MyCubeGrid>()
                .FirstOrDefault(g => !g.MarkedForClose && !g.IsPreview && !(ignoreStatic && g.IsStatic))?.DisplayName;
        }

        // ------------------------------------------------------------------ for the player

        public static void AddGps(MyCubeGrid grid, long identityId)
        {
            var gps = MyAPIGateway.Session?.GPS.Create(grid.DisplayName, grid.DisplayName, grid.PositionComp.GetPosition(), true, true);
            if (gps == null) return;
            gps.GPSColor = Color.Yellow;
            MyAPIGateway.Session.GPS.AddGps(identityId, gps);
        }

        /// <summary>The safe zone the player stands in: a free spot may overlap it.</summary>
        public static MyEntity SafeZoneAround(IMyCharacter character)
        {
            var sphere = new BoundingSphereD(character.GetPosition(), Plugin.Instance.Config.MaxSpawnRadius);
            return MyEntities.GetEntitiesInSphere(ref sphere).OfType<MySafeZone>().FirstOrDefault();
        }
    }
}
