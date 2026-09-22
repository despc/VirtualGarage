using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;

namespace VirtualGarage
{
    /// <summary>
    /// The garage on disk, and how its files follow the world save so that a crash neither loses
    /// nor copies a grid.
    ///
    ///  name_unsaved.sbc           put into the garage, the world not saved since: the grid is still
    ///                             in the last world save. A crash deletes the file.
    ///  name.sbc                   in the garage.
    ///  name.sbc_spawned_unsaved   taken out, the world not saved since: the grid is not in the last
    ///                             world save. A crash puts the file back - or deletes it, when it
    ///                             was itself unsaved (put in and taken out between two saves).
    ///  name.sbc_spawned           taken out and saved; deleted after a week.
    ///
    /// An operation counts once a world save that saw it is on disk. Every operation gets a
    /// number when it happens, on the game thread; a world save notes the last number when it takes
    /// its snapshot (on the game thread too), and only when that save has been written - not when
    /// the game says "saved", which it does as soon as the snapshot is taken - the operations up to
    /// that number become saved (<see cref="SavePatches"/>).
    /// </summary>
    public static class GarageFiles
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const string Unsaved = "_unsaved.sbc";
        private const string Extension = ".sbc";
        private const string TakenOutUnsaved = "_spawned_unsaved";
        private const string TakenOut = "_spawned";
        private const string LegacyB5 = ".sbcB5";
        private const double KeepTakenOutDays = 7;

        /// <summary>A file being written; it gets its name when it is whole.</summary>
        public const string Partial = ".tmp";

        private static readonly Regex Numbers = new Regex(@"_P-(\d+)_B-(\d+)", RegexOptions.Compiled);

        public static string Root => Plugin.Instance.Config.PathToVirtualGarage;

        public static string FolderOf(ulong steamId) => Path.Combine(Root, steamId.ToString());

        // ------------------------------------------------------------------ what the players see

        /// <summary>The grids in a player's garage, in the order !g list shows and !g load counts.</summary>
        public static List<string> List(ulong steamId)
        {
            var folder = FolderOf(steamId);
            if (!Directory.Exists(folder)) return new List<string>();
            var files = Directory.GetFiles(folder).Where(IsInGarage).ToList();
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>Every grid in every garage, with the Steam id of its garage.</summary>
        public static IEnumerable<(ulong SteamId, string File)> All()
        {
            if (!Directory.Exists(Root)) yield break;
            foreach (var folder in Directory.GetDirectories(Root))
            {
                if (!ulong.TryParse(Path.GetFileName(folder), out var steamId)) continue;
                foreach (var file in Directory.GetFiles(folder).Where(IsInGarage))
                    yield return (steamId, file);
            }
        }

        private static bool IsInGarage(string file) => file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>What !g list shows for a file.</summary>
        public static string Title(string file) => Path.GetFileName(file).Replace(Unsaved, "").Replace(Extension, "");

        /// <summary>The PCU in a file's name, or null.</summary>
        public static int? Pcu(string file)
        {
            var match = Numbers.Match(Path.GetFileName(file));
            return match.Success && int.TryParse(match.Groups[1].Value, out var pcu) ? pcu : (int?)null;
        }

        // ------------------------------------------------------------------ operations

        private enum Kind { PutIn, TakenOut }

        private sealed class Operation
        {
            public Kind Kind;
            public long Number;
            public string File;        // where the file is now
            public bool Written = true; // a put-in whose file is still being written is not
            public bool SavedWhileWriting;
        }

        private static readonly object Lock = new object();
        private static readonly List<Operation> Operations = new List<Operation>();
        private static long _last;

        /// <summary>The number of the last operation. Game thread, when a world snapshot is taken.</summary>
        public static long LastOperation => Interlocked.Read(ref _last);

        /// <summary>
        /// A new file for a grid put into the garage now (game thread). The file is written aside
        /// and given its name by <see cref="Written"/>.
        /// </summary>
        public static string NewFile(ulong steamId, string gridName, int pcu, int blocks)
        {
            var folder = FolderOf(steamId);
            Directory.CreateDirectory(folder);
            var name = gridName.Length <= 30 ? gridName : gridName.Substring(0, 30);
            name = name + "_" + DateTime.Now.ToString("yyyy.MM.dd_HH.mm") + "_P-" + pcu + "_B-" + blocks + "_" + new Random().Next(1000, 10000);
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '.');
            var file = Path.Combine(folder, name + Unsaved);
            lock (Lock)
                Operations.Add(new Operation { Kind = Kind.PutIn, Number = Interlocked.Increment(ref _last), File = file, Written = false });
            return file;
        }

        /// <summary>
        /// The file of a put-in is whole: it gets its name - the saved one, if a world save that
        /// saw the put-in was written meanwhile.
        /// </summary>
        public static void Written(string file, string partial)
        {
            lock (Lock)
            {
                var operation = Operations.FirstOrDefault(o => o.Kind == Kind.PutIn && o.File == file);
                if (operation != null && operation.SavedWhileWriting)
                {
                    Operations.Remove(operation);
                    Move(partial, SavedName(file));
                    return;
                }
                if (operation != null) operation.Written = true;
                Move(partial, file);
            }
        }

        /// <summary>A put-in whose file could not be written: nothing to remember.</summary>
        public static void Abandoned(string file)
        {
            lock (Lock) Operations.RemoveAll(o => o.File == file);
        }

        /// <summary>The grids of a file are in the world now. Game thread.</summary>
        public static void MarkTakenOut(string file)
        {
            lock (Lock)
            {
                var taken = file + TakenOutUnsaved;
                if (!Move(file, taken)) return;
                foreach (var operation in Operations.Where(o => o.File == file)) operation.File = taken;
                Operations.Add(new Operation { Kind = Kind.TakenOut, Number = Interlocked.Increment(ref _last), File = taken });
            }
        }

        /// <summary>A world save that saw every operation up to <paramref name="last"/> is on disk. Any thread.</summary>
        public static void OnWorldSaved(long last)
        {
            lock (Lock)
            {
                foreach (var operation in Operations.Where(o => o.Number <= last).OrderBy(o => o.Number).ToList())
                {
                    if (operation.Kind == Kind.PutIn && !operation.Written)
                    {
                        // still being written: it takes the saved name when it is whole
                        operation.SavedWhileWriting = true;
                        continue;
                    }
                    Operations.Remove(operation);
                    var from = operation.File;
                    var to = operation.Kind == Kind.PutIn ? SavedName(from) : from.Substring(0, from.Length - TakenOutUnsaved.Length) + TakenOut;
                    if (from == to || !File.Exists(from)) continue;
                    if (!Move(from, to)) continue;
                    foreach (var other in Operations.Where(o => o.File == from)) other.File = to;
                }
            }
        }

        /// <summary>The name of a put-in file once the world has been saved: "_unsaved.sbc" becomes ".sbc", whatever follows.</summary>
        private static string SavedName(string file)
        {
            var at = file.LastIndexOf(Unsaved, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? file : file.Substring(0, at) + Extension + file.Substring(at + Unsaved.Length);
        }

        // ------------------------------------------------------------------ when the world loads

        /// <summary>
        /// The world was loaded: what happened after its last save did not happen. Grids put into
        /// the garage are still in the world, grids taken out are not.
        /// </summary>
        public static void OnWorldLoaded()
        {
            lock (Lock) Operations.Clear();
            foreach (var file in AllFiles())
            {
                if (file.EndsWith(Unsaved, StringComparison.OrdinalIgnoreCase) || file.EndsWith(Partial, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("Put into the garage after the last world save, the grid is still in the world: " + file + " deleted");
                    Delete(file);
                }
                else if (file.EndsWith(TakenOutUnsaved, StringComparison.OrdinalIgnoreCase))
                {
                    var original = file.Substring(0, file.Length - TakenOutUnsaved.Length);
                    if (original.EndsWith(Unsaved, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn("Put in and taken out after the last world save, the grid is in the world: " + file + " deleted");
                        Delete(file);
                    }
                    else
                    {
                        Log.Warn("Taken out after the last world save, the grid is not in the world: " + file + " back in the garage");
                        Move(file, original);
                    }
                }
            }
        }

        /// <summary>Old leftovers: grids taken out a week ago, and the .sbcB5 caches the game leaves next to a blueprint it reads.</summary>
        public static void RemoveTrash()
        {
            foreach (var file in AllFiles())
            {
                if (file.EndsWith(LegacyB5, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(TakenOut, StringComparison.OrdinalIgnoreCase) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays > KeepTakenOutDays)
                    Delete(file);
            }
        }

        private static IEnumerable<string> AllFiles()
        {
            if (!Directory.Exists(Root)) return Enumerable.Empty<string>();
            return Directory.GetDirectories(Root).SelectMany(Directory.GetFiles).ToList();
        }

        /// <summary>A rename, retried a little while the file is in use.</summary>
        private static bool Move(string from, string to)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                    return true;
                }
                catch (Exception e) when (attempt < 10)
                {
                    Log.Warn(e, "Renaming " + from + " failed, retrying");
                    Thread.Sleep(20);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Renaming " + from + " to " + to + " failed");
                    return false;
                }
            }
        }

        private static void Delete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e)
            {
                Log.Error(e, "Deleting " + file + " failed");
            }
        }
    }
}
