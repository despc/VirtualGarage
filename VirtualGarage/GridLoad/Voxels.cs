using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions.SessionComponents;
using VRageMath;

namespace Scripts.Shared {
    public static class Voxels {
        
        public static bool IsGridInsideVoxel(IMyCubeGrid cubeGrid) { 
            try {
                MyGridPlacementSettings grid_settings = new MyGridPlacementSettings();
                MyGridPlacementSettings settings = new MyGridPlacementSettings();

                settings.CanAnchorToStaticGrid = true;
                settings.EnablePreciseRotationWhenSnapped = true;
                settings.SearchHalfExtentsDeltaAbsolute = 0;
                settings.SearchHalfExtentsDeltaRatio = 0;
                settings.SnapMode = SnapMode.Base6Directions;

                grid_settings = settings;//WTF HERE??

                var vsettings = new VoxelPlacementSettings();
                vsettings.PlacementMode = VoxelPlacementMode.Volumetric;
                vsettings.MaxAllowed = 0.95f;
                vsettings.MinAllowed = 0;

                var grid_vsettings = new VoxelPlacementSettings();
                grid_vsettings.PlacementMode = VoxelPlacementMode.Volumetric;
                grid_vsettings.MaxAllowed = 0.25f;
                grid_vsettings.MinAllowed = 0;

                grid_settings.VoxelPlacement = grid_vsettings;
                settings.VoxelPlacement = vsettings;

                MatrixD grid_worldMatrix = cubeGrid.WorldMatrix;
                BoundingBoxD cubeGrid_localAABB = cubeGrid.LocalAABB;

                return MyCubeGrid.IsAabbInsideVoxel(grid_worldMatrix, cubeGrid_localAABB, grid_settings);
            } catch (Exception e) {
                
                //return;
            }
            return false;
        }
    }
}