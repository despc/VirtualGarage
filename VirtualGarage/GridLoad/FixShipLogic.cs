﻿using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace VirtualGarage
{
    public class FixShipLogic
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void DoFixShip(MyCubeGrid grid) => FixGroups(FindLookAtGridGroup(grid));

        private static void FixGroups(List<MyCubeGrid> groups)
        {
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    FixGroup(groups);
                }
                catch (Exception e)
                {
                    Log.Error("Fixship after garage error ", e);
                }
            });
        }

        public static List<MyCubeGrid> FindLookAtGridGroup(MyCubeGrid grid)
        {
            List<MyCubeGrid> groupNodes =
                MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Logical).GetGroupNodes(grid);
            return groupNodes;
        }

        private static void FixGroup(List<MyCubeGrid> groups)
        {
            List<MyObjectBuilder_EntityBase> objectBuilders = new List<MyObjectBuilder_EntityBase>();
            List<MyCubeGrid> myCubeGridList = new List<MyCubeGrid>();
            Dictionary<Vector3I, MyCharacter> characters = new Dictionary<Vector3I, MyCharacter>();

            foreach (MyCubeGrid nodeData in groups)
            {
                myCubeGridList.Add(nodeData);
                // nodeData.Physics.LinearVelocity = Vector3.Zero;
                MyObjectBuilder_EntityBase objectBuilder = nodeData.GetObjectBuilder(true);
                if (!objectBuilders.Contains(objectBuilder))
                {
                    objectBuilders.Add(objectBuilder);
                }

                foreach (var myCubeBlock in nodeData.GetFatBlocks())
                {
                    if (myCubeBlock is MyCockpit)
                    {
                        var cockpit = (MyCockpit) myCubeBlock;
                        MyCharacter myCharacter = cockpit.Pilot;
                        if (myCharacter == null)
                        {
                            continue;
                        }
                        characters[cockpit.Position] = myCharacter;
                        cockpit.RemovePilot();
                    }
                }
            }

            foreach (MyCubeGrid myCubeGrid in myCubeGridList)
            {
                IMyEntity myEntity = myCubeGrid;
                Log.Info($"Fixship after spawn from garage {myCubeGrid.DisplayName}");
                myEntity.Close();
            }

            MyAPIGateway.Entities.RemapObjectBuilderCollection(objectBuilders);
            foreach (MyObjectBuilder_EntityBase cubeGrid in objectBuilders)
                
                MyAPIGateway.Entities.CreateFromObjectBuilderParallel(cubeGrid,
                    completionCallback: ((Action<IMyEntity>) (entity =>
                    {
                        ((MyCubeGrid) entity).DetectDisconnectsAfterFrame();
                        MyAPIGateway.Entities.AddEntity(entity);
                        foreach (var myCubeBlock in ((MyCubeGrid) entity).GetFatBlocks())
                        {
                            if (myCubeBlock is MyCockpit)
                            {
                                var cockpit = (MyCockpit) myCubeBlock;
                                MyCharacter myCharacter;
                                if (!characters.TryGetValue(cockpit.Position, out myCharacter))
                                {
                                    continue;
                                }
                                
                                MyAPIGateway.Parallel.StartBackground(() =>
                                {
                                    MyAPIGateway.Parallel.Sleep(2000); 
                                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                                    {
                                        ((IMyCockpit)cockpit).AttachPilot(myCharacter);
                                    });
                                });
                            }
                        }
                    })));
        }
    }
}