using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace VirtualGarage
{
    /// <summary>
    /// When the world is loaded: the garage's files are set right for the last world save (a crash
    /// neither loses nor copies a grid, see <see cref="GarageFiles"/>), and, with OldGridDays above
    /// zero, the grids of players who have not been online for that many days go into their
    /// garages. Groups go one per frame, each group once; the admin's !g a_saveall uses the same
    /// queue for every grid in the world.
    /// </summary>
    public class VirtualGarageOldGridProcessor
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static VirtualGarageOldGridProcessor OldGridProcessor = new VirtualGarageOldGridProcessor();

        private const int StartDelayMs = 10000;

        public void OnLoaded()
        {
            try
            {
                GarageFiles.OnWorldLoaded();
            }
            catch (Exception e)
            {
                Log.Error(e, "Setting the garage right for the loaded world failed");
            }
            Task.Run(async () =>
            {
                await Task.Delay(StartDelayMs);
                MyAPIGateway.Utilities.InvokeOnGameThread(QueueOldGroups);
                await Task.Delay(new Random().Next(60000, 180000));
                try
                {
                    GarageFiles.RemoveTrash();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Cleaning the garage failed");
                }
            });
        }

        // ------------------------------------------------------------------ the queue, on the game thread

        private readonly Queue<List<MyCubeGrid>> _queue = new Queue<List<MyCubeGrid>>();
        private bool _running;

        /// <summary>
        /// Every group of grids in the world that belongs to a player - an owner, or a builder when
        /// nobody owns it - and passes <paramref name="take"/>. Game thread.
        /// </summary>
        public static List<List<MyCubeGrid>> PlayerGroups(Func<List<MyCubeGrid>, bool> take)
        {
            var result = new List<List<MyCubeGrid>>();
            var seen = new HashSet<MyCubeGrid>();
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
            {
                if (grid.MarkedForClose || grid.IsPreview || seen.Contains(grid)) continue;
                var group = VirtualGarageSave.Group(grid);
                seen.UnionWith(group);
                if (!BelongsToPlayer(group)) continue;
                if (take(group)) result.Add(group);
            }
            return result;
        }

        /// <summary>Puts the groups into their people's garages, one per frame. Game thread.</summary>
        public void Enqueue(IEnumerable<List<MyCubeGrid>> groups)
        {
            foreach (var group in groups) _queue.Enqueue(group);
            if (_running) return;
            _running = true;
            StoreNext();
        }

        // The search for old groups goes over the world this many blocks a frame: a group with no owner is
        // looked at block by block for its builders, and the whole world in one frame was a stall.
        private const int BlocksPerFrame = 20000;
        private List<MyCubeGrid> _scan = new List<MyCubeGrid>();
        private int _scanCursor;
        private readonly HashSet<MyCubeGrid> _scanSeen = new HashSet<MyCubeGrid>();
        private readonly List<List<MyCubeGrid>> _scanOld = new List<List<MyCubeGrid>>();
        private int _scanDays;

        private void QueueOldGroups()
        {
            _scanDays = Plugin.Instance.Config.OldGridDays;
            if (_scanDays <= 0) return;
            _scan = MyEntities.GetEntities().OfType<MyCubeGrid>().ToList();
            _scanCursor = 0;
            _scanSeen.Clear();
            _scanOld.Clear();
            ScanStep();
        }

        /// <summary>The next groups of the search for old ones, <see cref="BlocksPerFrame"/> blocks; then the next frame. Game thread.</summary>
        private void ScanStep()
        {
            try
            {
                var blocks = 0;
                while (_scanCursor < _scan.Count && blocks < BlocksPerFrame)
                {
                    var grid = _scan[_scanCursor++];
                    if (grid.MarkedForClose || grid.IsPreview || _scanSeen.Contains(grid)) continue;
                    var group = VirtualGarageSave.Group(grid);
                    _scanSeen.UnionWith(group);
                    blocks += 1 + group.Sum(g => g.BlocksCount);
                    if (BelongsToPlayer(group) && !group.Any(x => x.DisplayName.Contains("@")) && IsOld(group, _scanDays))
                        _scanOld.Add(group);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Looking for old grids failed");
                _scanCursor = _scan.Count;
            }

            if (_scanCursor < _scan.Count)
            {
                MyAPIGateway.Utilities.InvokeOnGameThread(ScanStep, StartAt: NextFrame());
                return;
            }
            var old = _scanOld.ToList();
            _scan = new List<MyCubeGrid>();
            _scanSeen.Clear();
            _scanOld.Clear();
            if (old.Count > 0) Log.Warn(old.Count + " grid groups of players gone for " + _scanDays + "+ days go into their garages");
            Enqueue(old);
        }

        private static int NextFrame() => (MySession.Static?.GameplayFrameCounter ?? 0) + 1;

        private void StoreNext()
        {
            if (_queue.Count == 0)
            {
                _running = false;
                return;
            }
            var group = _queue.Dequeue();
            try
            {
                if (group.All(g => !g.MarkedForClose && !g.Closed))
                {
                    var owner = VirtualGarageSave.OwnerOf(group);
                    Log.Warn("Into the garage of " + (Sync.Players.TryGetIdentity(owner)?.DisplayName ?? owner.ToString()) + ": " + group[0].DisplayName);
                    VirtualGarageSave.Store(owner, group);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Putting a grid into the garage failed");
            }
            MyAPIGateway.Utilities.InvokeOnGameThread(StoreNext, StartAt: NextFrame());
        }

        // ------------------------------------------------------------------ whose, and how old

        /// <summary>A group has a garage to go to: the one it goes to is a player's, with a Steam id.</summary>
        private static bool BelongsToPlayer(List<MyCubeGrid> group)
        {
            var owner = VirtualGarageSave.OwnerOf(group);
            return owner != 0 && !MySession.Static.Players.IdentityIsNpc(owner) && MySession.Static.Players.TryGetSteamId(owner) != 0;
        }

        /// <summary>Everybody the group belongs to - its owners, or its builders when nobody owns it - has been away that long.</summary>
        private static bool IsOld(List<MyCubeGrid> group, int days)
        {
            foreach (var person in VirtualGarageSave.PeopleOf(group))
            {
                var identity = Sync.Players.TryGetIdentity(person);
                if (identity == null) continue;
                if ((DateTime.Now - identity.LastLogoutTime).TotalDays < days) return false;
            }
            return true;
        }
    }
}
