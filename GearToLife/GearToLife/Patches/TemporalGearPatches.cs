using DeathExile;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace deathexile.Patches;

[HarmonyPatchCategory("deathexile")]
internal static class TemporalGearPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemTemporalGear), "OnHeldInteractStop")]
    public static void AfterUsingGear(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
    {
        if 
        (
            byEntity.World.Side != EnumAppSide.Server ||
            byEntity is not EntityPlayer entityPlayer ||
            entityPlayer.World.PlayerByUid(entityPlayer.PlayerUID) is not IServerPlayer player ||
            secondsUsed < 3.45
        )
        {
            return;
        }

        DeathExileModSystem.AddLife(player);
    }
}