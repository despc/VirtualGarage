using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NLog;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace VirtualGarage
{
    /// <summary>
    /// When a world save is really on disk. MySession.OnSaved fires as soon as the snapshot of
    /// the world is taken, before a byte is written - the plugin used to count its files saved
    /// right then, and a crash while the world was being written left garage and world apart.
    ///
    /// MySession.Save takes the snapshot on the game thread, in one go: it notes the number of the
    /// last garage operation it saw. When that snapshot has been written, successfully, the
    /// operations up to that number count as saved. (The snapshot's tiny constructor would be the
    /// obvious place to note it, but the JIT inlines it into MySession.Save and a patch on it never runs.)
    /// </summary>
    [PatchShim]
    public static class SavePatches
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static readonly ConditionalWeakTable<MySessionSnapshot, StrongBox<long>> Seen =
            new ConditionalWeakTable<MySessionSnapshot, StrongBox<long>>();

        // the last garage operation when MySession.Save began (game thread)
        private static long _atSnapshot;

        public static void Patch(PatchContext ctx)
        {
            var taken = typeof(MySession).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == nameof(MySession.Save) && m.ReturnType == typeof(bool) &&
                                                 m.GetParameters().FirstOrDefault()?.ParameterType == typeof(MySessionSnapshot).MakeByRefType())
                        ?? throw new MissingMethodException("MySession.Save(out MySessionSnapshot, ...)");
            // the save that writes the world, whether the game calls it directly or from SaveParallel
            var written = typeof(MySessionSnapshot).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                              .FirstOrDefault(m => m.Name == "Save" && m.ReturnType == typeof(bool) &&
                                                   m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Func<bool>))
                          ?? throw new MissingMethodException("MySessionSnapshot.Save(Func<bool>, ...)");
            ctx.GetPattern(taken).Prefixes.Add(Method(nameof(SnapshotStarting)));
            ctx.GetPattern(taken).Suffixes.Add(Method(nameof(SnapshotTaken)));
            ctx.GetPattern(written).Suffixes.Add(Method(nameof(SnapshotWritten)));
            Log.Info("World save tracking installed");
        }

        private static MethodInfo Method(string name) => typeof(SavePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static void SnapshotStarting()
        {
            _atSnapshot = GarageFiles.LastOperation;
        }

        private static void SnapshotTaken(bool __result, ref MySessionSnapshot snapshot)
        {
            try
            {
                if (!__result || snapshot == null) return;
                Seen.Remove(snapshot);
                Seen.Add(snapshot, new StrongBox<long>(_atSnapshot));
                Log.Info("World snapshot taken after garage operation " + _atSnapshot);
            }
            catch (Exception e)
            {
                Log.Error(e, "Noting a world snapshot failed");
            }
        }

        private static void SnapshotWritten(MySessionSnapshot __instance, bool __result)
        {
            try
            {
                if (!Seen.TryGetValue(__instance, out var last))
                {
                    Log.Warn("A world save " + (__result ? "was written" : "failed") + " whose snapshot the garage did not see; its files stay as they are");
                    return;
                }
                Log.Info("World save " + (__result ? "written" : "FAILED") + "; garage operations up to " + last.Value + (__result ? " count as saved" : " stay unsaved"));
                if (__result) GarageFiles.OnWorldSaved(last.Value);
            }
            catch (Exception e)
            {
                Log.Error(e, "Marking the garage saved failed");
            }
        }
    }
}
